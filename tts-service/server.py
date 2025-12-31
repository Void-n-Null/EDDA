"""
EDDA TTS Microservice - Chatterbox Turbo

Provides text-to-speech synthesis using Chatterbox Turbo (350M params).
Designed to run as a Docker container with NVIDIA GPU access.

Endpoints:
  GET  /health          - Health check (returns model status)
  POST /tts             - Generate speech from text (returns WAV audio)
  GET  /voice/{voice_id} - Check if voice is cached
  POST /voice/{voice_id} - Upload voice audio for caching
"""

import asyncio
import hashlib
import io
import logging
import os
import random
import time
from contextlib import asynccontextmanager
from pathlib import Path
from typing import Optional

import torch
import torchaudio
from fastapi import FastAPI, HTTPException, Response, UploadFile, File
from fastapi.responses import StreamingResponse
from pydantic import BaseModel, Field

# ============================================================================
# CUDA Optimizations (must be set before model loading)
# ============================================================================
if torch.cuda.is_available():
    # Auto-tune CUDA kernels for the specific GPU
    torch.backends.cudnn.benchmark = True
    # Allow TF32 on Ampere+ GPUs (ignored on older GPUs like 2070)
    torch.backends.cuda.matmul.allow_tf32 = True
    torch.backends.cudnn.allow_tf32 = True

# Configure logging
logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s [%(levelname)s] %(message)s",
    datefmt="%Y-%m-%d %H:%M:%S",
)
logger = logging.getLogger(__name__)


# ============================================================================
# Configuration
# ============================================================================

class Config:
    """Service configuration from environment variables."""

    DEVICE: str = os.getenv("TTS_DEVICE", "cuda" if torch.cuda.is_available() else "cpu")
    PORT: int = int(os.getenv("TTS_PORT", "5000"))
    LOG_LEVEL: str = os.getenv("LOG_LEVEL", "INFO")

    # Voice cache directory (persistent storage for uploaded voices)
    VOICE_CACHE_DIR: Path = Path(os.getenv("TTS_VOICE_CACHE_DIR", "/tmp/edda-voice-cache"))

    # Performance options
    # torch.compile gives ~20-40% speedup after warmup (requires PyTorch 2.0+)
    # Enable by default on CUDA, disable on CPU (compile doesn't help much there)
    USE_TORCH_COMPILE: bool = os.getenv(
        "TTS_TORCH_COMPILE",
        "true" if torch.cuda.is_available() else "false"
    ).lower() == "true"

    # Number of warmup iterations (more = better perf but slower startup)
    WARMUP_ITERATIONS: int = int(os.getenv("TTS_WARMUP_ITERATIONS", "3"))

    # Audio settings
    SAMPLE_RATE: int = 24000  # Chatterbox native sample rate

    # Model warmup
    WARMUP_TEXT: str = "Hello, this is a warmup test."

    # Model loading retry settings
    LOAD_MAX_RETRIES: int = int(os.getenv("TTS_LOAD_MAX_RETRIES", "10"))
    LOAD_INITIAL_DELAY_S: float = float(os.getenv("TTS_LOAD_INITIAL_DELAY_S", "5"))
    LOAD_MAX_DELAY_S: float = float(os.getenv("TTS_LOAD_MAX_DELAY_S", "120"))
    LOAD_BACKOFF_MULTIPLIER: float = 2.0

    # Background retry interval (if startup fails, keep trying)
    BACKGROUND_RETRY_INTERVAL_S: int = int(os.getenv("TTS_BACKGROUND_RETRY_S", "60"))


# ============================================================================
# Voice Cache
# ============================================================================

class VoiceCache:
    """
    Simple file-based voice cache.
    Stores uploaded voice reference files by their content hash.
    No expiration - voices persist forever (storage is cheap).
    """

    def __init__(self, cache_dir: Path):
        self.cache_dir = cache_dir
        self.cache_dir.mkdir(parents=True, exist_ok=True)
        logger.info(f"Voice cache directory: {self.cache_dir}")

        # Log existing cached voices on startup
        existing = list(self.cache_dir.glob("*.wav"))
        if existing:
            logger.info(f"Found {len(existing)} cached voice(s): {[f.stem for f in existing]}")

    def get_path(self, voice_id: str) -> Path:
        """Get the cache path for a voice ID (doesn't check existence)."""
        # Sanitize voice_id to prevent path traversal
        safe_id = "".join(c for c in voice_id if c.isalnum() or c in "-_")
        return self.cache_dir / f"{safe_id}.wav"

    def exists(self, voice_id: str) -> bool:
        """Check if a voice is cached."""
        return self.get_path(voice_id).exists()

    def get(self, voice_id: str) -> Optional[Path]:
        """Get path to cached voice, or None if not cached."""
        path = self.get_path(voice_id)
        return path if path.exists() else None

    def store(self, voice_id: str, audio_data: bytes) -> Path:
        """
        Store voice audio in cache.

        Args:
            voice_id: Identifier for this voice (typically a content hash)
            audio_data: Raw WAV audio bytes

        Returns:
            Path to the cached file
        """
        path = self.get_path(voice_id)
        path.write_bytes(audio_data)
        logger.info(f"Cached voice '{voice_id}' ({len(audio_data)} bytes) -> {path}")
        return path

    @staticmethod
    def compute_hash(audio_data: bytes) -> str:
        """Compute a short content hash for audio data."""
        return hashlib.sha256(audio_data).hexdigest()[:16]


# Global voice cache instance
voice_cache: Optional[VoiceCache] = None


# ============================================================================
# Model Singleton
# ============================================================================

class TTSModel:
    """
    Singleton wrapper for Chatterbox TTS model.
    Handles lazy loading and warmup.
    """

    _instance: Optional["TTSModel"] = None

    def __init__(self):
        self.model = None
        self.device = Config.DEVICE
        self.is_ready = False
        self.load_time_ms: float = 0
        self.last_error: Optional[str] = None
        self._loading = False  # Prevent concurrent load attempts
        self._background_retry_task: Optional[asyncio.Task] = None

    @classmethod
    def get_instance(cls) -> "TTSModel":
        if cls._instance is None:
            cls._instance = TTSModel()
        return cls._instance

    def load(self) -> bool:
        """Load the Chatterbox Turbo model (single attempt)."""
        if self._loading:
            logger.warning("Load already in progress, skipping")
            return False

        self._loading = True
        try:
            logger.info(f"Loading Chatterbox Turbo on device: {self.device}")
            start = time.perf_counter()

            # Import and load Chatterbox Turbo (supports paralinguistic tags like [chuckle])
            from chatterbox.tts_turbo import ChatterboxTurboTTS

            self.model = ChatterboxTurboTTS.from_pretrained(device=self.device)

            self.load_time_ms = (time.perf_counter() - start) * 1000
            logger.info(f"Model loaded in {self.load_time_ms:.0f}ms")

            # Verify model is actually on the expected device
            self._verify_device()

            # Optional: torch.compile for faster inference (experimental)
            if Config.USE_TORCH_COMPILE:
                logger.info("Compiling model with torch.compile() - this may take a minute...")
                try:
                    # Compile the internal models if accessible
                    # Note: This is experimental and may not work with all model architectures
                    compile_start = time.perf_counter()
                    if hasattr(self.model, 't2s') and self.model.t2s is not None:
                        self.model.t2s = torch.compile(self.model.t2s, mode="reduce-overhead")
                    if hasattr(self.model, 's2a') and self.model.s2a is not None:
                        self.model.s2a = torch.compile(self.model.s2a, mode="reduce-overhead")
                    compile_ms = (time.perf_counter() - compile_start) * 1000
                    logger.info(f"Model compiled in {compile_ms:.0f}ms")
                except Exception as e:
                    logger.warning(f"torch.compile failed (non-fatal): {e}")

            # Warmup inference (first inference is always slower)
            self._warmup()

            self.is_ready = True
            self.last_error = None
            return True

        except Exception as e:
            self.last_error = str(e)
            logger.error(f"Failed to load model: {e}")
            return False
        finally:
            self._loading = False

    def load_with_retry(self) -> bool:
        """
        Load the model with exponential backoff retry.

        Retries up to LOAD_MAX_RETRIES times with exponential backoff.
        Logs each attempt and the wait time before next retry.
        """
        if self.is_ready:
            return True

        delay = Config.LOAD_INITIAL_DELAY_S

        for attempt in range(1, Config.LOAD_MAX_RETRIES + 1):
            logger.info(f"Model load attempt {attempt}/{Config.LOAD_MAX_RETRIES}")

            if self.load():
                logger.info(f"✓ Model loaded successfully on attempt {attempt}")
                return True

            if attempt < Config.LOAD_MAX_RETRIES:
                # Add jitter (±20%) to prevent thundering herd
                jitter = delay * 0.2 * (random.random() * 2 - 1)
                wait_time = min(delay + jitter, Config.LOAD_MAX_DELAY_S)

                logger.warning(
                    f"Model load failed (attempt {attempt}/{Config.LOAD_MAX_RETRIES}). "
                    f"Retrying in {wait_time:.1f}s... Error: {self.last_error}"
                )
                time.sleep(wait_time)
                delay = min(delay * Config.LOAD_BACKOFF_MULTIPLIER, Config.LOAD_MAX_DELAY_S)

        logger.error(
            f"✗ Model failed to load after {Config.LOAD_MAX_RETRIES} attempts. "
            f"Last error: {self.last_error}"
        )
        return False

    async def start_background_retry(self):
        """
        Start a background task that keeps trying to load the model.

        Called when initial load fails. Continues retrying indefinitely
        until the model loads successfully.
        """
        if self.is_ready:
            return

        if self._background_retry_task is not None:
            logger.debug("Background retry already running")
            return

        async def retry_loop():
            attempt = 0
            while not self.is_ready:
                attempt += 1
                await asyncio.sleep(Config.BACKGROUND_RETRY_INTERVAL_S)

                if self.is_ready:
                    break

                logger.info(f"Background model load attempt {attempt}...")

                # Run blocking load in thread pool
                success = await asyncio.get_running_loop().run_in_executor(None, self.load)

                if success:
                    logger.info(f"✓ Background model load succeeded on attempt {attempt}")
                    break
                else:
                    logger.warning(
                        f"Background model load failed (attempt {attempt}). "
                        f"Will retry in {Config.BACKGROUND_RETRY_INTERVAL_S}s. "
                        f"Error: {self.last_error}"
                    )

        self._background_retry_task = asyncio.create_task(retry_loop())
        logger.info(
            f"Started background model retry (interval: {Config.BACKGROUND_RETRY_INTERVAL_S}s)"
        )

    def stop_background_retry(self):
        """Cancel the background retry task if running."""
        if self._background_retry_task is not None:
            self._background_retry_task.cancel()
            self._background_retry_task = None
            logger.info("Background retry task cancelled")

    def _verify_device(self):
        """Verify the model components are on the expected device."""
        try:
            # Check various model components to verify device placement
            devices_found = set()

            # Check t2s (text-to-semantic) model
            if hasattr(self.model, 't2s') and self.model.t2s is not None:
                for param in self.model.t2s.parameters():
                    devices_found.add(str(param.device))
                    break  # Just check first param

            # Check s2a (semantic-to-acoustic) model
            if hasattr(self.model, 's2a') and self.model.s2a is not None:
                for param in self.model.s2a.parameters():
                    devices_found.add(str(param.device))
                    break

            # Check ve (voice encoder) if present
            if hasattr(self.model, 've') and self.model.ve is not None:
                for param in self.model.ve.parameters():
                    devices_found.add(str(param.device))
                    break

            if devices_found:
                logger.info(f"Model components on device(s): {devices_found}")
                if self.device == "cuda" and not any("cuda" in d for d in devices_found):
                    logger.error("⚠️ WARNING: Model requested CUDA but components are on CPU!")
                elif self.device == "cuda":
                    logger.info("✓ Model confirmed on CUDA")
            else:
                logger.warning("Could not verify model device placement")

        except Exception as e:
            logger.warning(f"Device verification failed: {e}")

    def _warmup(self):
        """Run warmup inferences to prime CUDA kernels and torch.compile."""
        logger.info(f"Running {Config.WARMUP_ITERATIONS} warmup iterations...")

        try:
            with torch.inference_mode():
                for i in range(Config.WARMUP_ITERATIONS):
                    start = time.perf_counter()
                    _ = self.model.generate(Config.WARMUP_TEXT)
                    warmup_ms = (time.perf_counter() - start) * 1000

                    if i == 0:
                        logger.info(f"Warmup {i+1}/{Config.WARMUP_ITERATIONS}: {warmup_ms:.0f}ms (first run, compiling)")
                    else:
                        logger.info(f"Warmup {i+1}/{Config.WARMUP_ITERATIONS}: {warmup_ms:.0f}ms")

                # Report expected performance from last warmup
                logger.info(f"Warmup complete. Expected inference time: ~{warmup_ms:.0f}ms")
        except Exception as e:
            logger.warning(f"Warmup failed (non-fatal): {e}")

    def generate(
        self,
        text: str,
        audio_prompt_path: Optional[str] = None,
        exaggeration: float = 0.5,
        cfg_weight: float = 0.5,
    ) -> torch.Tensor:
        """
        Generate speech from text.

        Args:
            text: Text to synthesize
            audio_prompt_path: Optional path to reference audio for voice cloning
            exaggeration: Emotion exaggeration (0.0 = monotone, 1.0 = expressive)
            cfg_weight: Classifier-free guidance weight

        Returns:
            Audio tensor (1, samples) at 24kHz
        """
        if not self.is_ready or self.model is None:
            raise RuntimeError("Model not loaded")

        # Use inference_mode for faster execution (no gradient tracking)
        with torch.inference_mode():
            return self.model.generate(
                text,
                audio_prompt_path=audio_prompt_path,
                exaggeration=exaggeration,
                cfg_weight=cfg_weight,
            )

    @property
    def sample_rate(self) -> int:
        """Get the model's native sample rate."""
        if self.model is not None:
            return self.model.sr
        return Config.SAMPLE_RATE


# ============================================================================
# FastAPI App
# ============================================================================

@asynccontextmanager
async def lifespan(app: FastAPI):
    """Startup and shutdown lifecycle."""
    global voice_cache

    # Startup: Initialize voice cache
    voice_cache = VoiceCache(Config.VOICE_CACHE_DIR)

    # Startup: Load model
    logger.info("=" * 60)
    logger.info("EDDA TTS Service Starting")
    logger.info(f"Device: {Config.DEVICE}")
    logger.info(f"CUDA Available: {torch.cuda.is_available()}")
    if torch.cuda.is_available():
        logger.info(f"CUDA Device: {torch.cuda.get_device_name(0)}")
        logger.info(f"VRAM: {torch.cuda.get_device_properties(0).total_memory / 1024**3:.1f} GB")
    logger.info("=" * 60)

    model = TTSModel.get_instance()

    # Try loading with retries (blocks startup for up to ~10 min worst case)
    if not model.load_with_retry():
        logger.error(
            "Model failed to load after all retries - starting background retry. "
            "Service will return 503 on /tts until model loads."
        )
        # Start background task that keeps trying forever
        await model.start_background_retry()

    yield

    # Shutdown: Cleanup
    model.stop_background_retry()
    logger.info("TTS Service shutting down")


app = FastAPI(
    title="EDDA TTS Service",
    description="Text-to-speech synthesis using Chatterbox Turbo",
    version="1.0.0",
    lifespan=lifespan,
)


# ============================================================================
# Request/Response Models
# ============================================================================

class TTSRequest(BaseModel):
    """Request body for /tts endpoint."""

    text: str = Field(..., min_length=1, max_length=5000, description="Text to synthesize")

    # New: voice_id takes precedence over voice_reference
    voice_id: Optional[str] = Field(
        None,
        description="Voice cache ID (hash). If provided, looks up cached voice file.",
    )

    # Legacy: direct file path (still supported for backwards compatibility)
    voice_reference: Optional[str] = Field(
        None,
        description="Path to reference audio for voice cloning (on server filesystem). Deprecated - use voice_id.",
    )

    exaggeration: float = Field(
        0.5,
        ge=0.0,
        le=1.0,
        description="Emotion exaggeration (0=monotone, 1=expressive)",
    )
    cfg_weight: float = Field(
        0.5,
        ge=0.0,
        le=1.0,
        description="Classifier-free guidance weight",
    )


class HealthResponse(BaseModel):
    """Response body for /health endpoint."""

    status: str
    model_loaded: bool
    device: str
    cuda_available: bool
    cuda_device: Optional[str] = None
    vram_total_gb: Optional[float] = None
    vram_used_gb: Optional[float] = None
    load_time_ms: Optional[float] = None
    last_error: Optional[str] = None
    cached_voices: int = 0


class VoiceCacheStatus(BaseModel):
    """Response for voice cache check."""
    voice_id: str
    cached: bool
    path: Optional[str] = None


# ============================================================================
# Endpoints
# ============================================================================

@app.get("/health", response_model=HealthResponse)
async def health_check():
    """
    Health check endpoint.

    Returns model status and GPU information.
    Used by C# server for health monitoring.
    """
    model = TTSModel.get_instance()

    cuda_device = None
    vram_total = None
    vram_used = None

    if torch.cuda.is_available():
        cuda_device = torch.cuda.get_device_name(0)
        props = torch.cuda.get_device_properties(0)
        vram_total = props.total_memory / 1024**3
        vram_used = torch.cuda.memory_allocated(0) / 1024**3

    # Count cached voices
    cached_count = 0
    if voice_cache:
        cached_count = len(list(voice_cache.cache_dir.glob("*.wav")))

    return HealthResponse(
        status="healthy" if model.is_ready else "unhealthy",
        model_loaded=model.is_ready,
        device=model.device,
        cuda_available=torch.cuda.is_available(),
        cuda_device=cuda_device,
        vram_total_gb=round(vram_total, 2) if vram_total else None,
        vram_used_gb=round(vram_used, 2) if vram_used else None,
        load_time_ms=round(model.load_time_ms, 0) if model.load_time_ms else None,
        last_error=model.last_error,
        cached_voices=cached_count,
    )


@app.get("/voice/{voice_id}", response_model=VoiceCacheStatus)
async def check_voice(voice_id: str):
    """
    Check if a voice is cached.

    Returns 200 with cached=true if voice exists, 200 with cached=false if not.
    The caller should upload the voice if cached=false before using it in /tts.
    """
    if not voice_cache:
        raise HTTPException(status_code=503, detail="Voice cache not initialized")

    cached = voice_cache.exists(voice_id)
    path = str(voice_cache.get(voice_id)) if cached else None

    return VoiceCacheStatus(
        voice_id=voice_id,
        cached=cached,
        path=path,
    )


@app.post("/voice/{voice_id}")
async def upload_voice(voice_id: str, file: UploadFile = File(...)):
    """
    Upload a voice reference audio file for caching.

    The voice_id should be a content hash of the audio (client computes this).
    The server will store the file and make it available for /tts requests.

    Args:
        voice_id: Identifier for this voice (typically SHA256 hash prefix)
        file: WAV audio file upload

    Returns:
        JSON with voice_id and cached path
    """
    if not voice_cache:
        raise HTTPException(status_code=503, detail="Voice cache not initialized")

    # Read the uploaded file
    audio_data = await file.read()

    if len(audio_data) == 0:
        raise HTTPException(status_code=400, detail="Empty file uploaded")

    # Verify it's actually audio (basic check - starts with RIFF for WAV)
    if not audio_data[:4] == b'RIFF':
        raise HTTPException(
            status_code=400,
            detail="Invalid audio format. Expected WAV file."
        )

    # Optional: verify the voice_id matches the content hash
    # (we could enforce this, but it's not strictly necessary)
    computed_hash = VoiceCache.compute_hash(audio_data)
    if voice_id != computed_hash:
        logger.warning(
            f"Voice ID mismatch: client sent '{voice_id}', computed '{computed_hash}'. "
            "Storing with client-provided ID anyway."
        )

    # Store in cache
    path = voice_cache.store(voice_id, audio_data)

    return {
        "voice_id": voice_id,
        "path": str(path),
        "size_bytes": len(audio_data),
        "computed_hash": computed_hash,
    }


@app.post("/tts")
async def text_to_speech(request: TTSRequest):
    """
    Generate speech from text.

    Returns streaming WAV audio (24kHz, 16-bit, mono).

    Voice selection priority:
    1. voice_id - Look up cached voice by ID
    2. voice_reference - Use file path directly (legacy)
    3. None - Use default voice
    """
    model = TTSModel.get_instance()

    if not model.is_ready:
        raise HTTPException(
            status_code=503,
            detail="TTS model not loaded. Check /health for details.",
        )

    # Resolve voice reference path
    voice_path: Optional[str] = None
    voice_mode = "default"

    if request.voice_id:
        # Look up cached voice
        if not voice_cache:
            raise HTTPException(status_code=503, detail="Voice cache not initialized")

        cached_path = voice_cache.get(request.voice_id)
        if cached_path is None:
            raise HTTPException(
                status_code=404,
                detail=f"Voice '{request.voice_id}' not found in cache. Upload it first via POST /voice/{{voice_id}}",
            )
        voice_path = str(cached_path)
        voice_mode = f"cached:{request.voice_id}"

    elif request.voice_reference:
        # Legacy: direct file path
        voice_path = request.voice_reference
        voice_mode = "path"

    logger.info(f"TTS request: mode={voice_mode}, voice_path={voice_path!r}")

    try:
        total_start = time.perf_counter()

        # Generate audio
        gen_start = time.perf_counter()
        audio = model.generate(
            text=request.text,
            audio_prompt_path=voice_path,
            exaggeration=request.exaggeration,
            cfg_weight=request.cfg_weight,
        )
        generation_ms = (time.perf_counter() - gen_start) * 1000

        # Convert to WAV bytes
        encode_start = time.perf_counter()
        wav_buffer = io.BytesIO()

        # Ensure audio is 2D (1, samples)
        if audio.dim() == 1:
            audio = audio.unsqueeze(0)

        # Move to CPU for saving (GPU->CPU transfer)
        audio_cpu = audio.cpu()

        torchaudio.save(
            wav_buffer,
            audio_cpu,
            model.sample_rate,
            format="wav",
        )
        wav_buffer.seek(0)
        encode_ms = (time.perf_counter() - encode_start) * 1000

        total_ms = (time.perf_counter() - total_start) * 1000

        # Calculate audio duration
        audio_duration_s = audio.shape[-1] / model.sample_rate
        rtf = generation_ms / (audio_duration_s * 1000)  # Real-time factor

        logger.info(
            f"TTS [{model.device}] ({voice_mode}): {len(request.text)} chars -> {audio_duration_s:.2f}s audio | "
            f"gen={generation_ms:.0f}ms, encode={encode_ms:.0f}ms, total={total_ms:.0f}ms "
            f"({1/rtf:.1f}x RT)"
        )

        return StreamingResponse(
            wav_buffer,
            media_type="audio/wav",
            headers={
                "X-Generation-Time-Ms": str(int(generation_ms)),
                "X-Audio-Duration-S": f"{audio_duration_s:.2f}",
                "X-Realtime-Factor": f"{1/rtf:.1f}",
                "X-Encode-Time-Ms": str(int(encode_ms)),
                "X-Total-Time-Ms": str(int(total_ms)),
                "X-Voice-Mode": voice_mode,
            },
        )

    except Exception as e:
        logger.error(f"TTS generation failed: {e}")
        raise HTTPException(status_code=500, detail=str(e))


@app.get("/")
async def root():
    """Root endpoint - service info."""
    return {
        "service": "EDDA TTS",
        "model": "Chatterbox Turbo",
        "endpoints": {
            "/health": "GET - Health check",
            "/tts": "POST - Text-to-speech synthesis",
            "/voice/{voice_id}": "GET - Check if voice is cached, POST - Upload voice for caching",
        },
    }


# ============================================================================
# Main Entry Point
# ============================================================================

if __name__ == "__main__":
    import uvicorn

    uvicorn.run(
        "server:app",
        host="0.0.0.0",
        port=Config.PORT,
        log_level=Config.LOG_LEVEL.lower(),
    )
