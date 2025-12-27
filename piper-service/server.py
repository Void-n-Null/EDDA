"""
EDDA TTS Microservice - Piper (Fast Alternative)

Provides text-to-speech synthesis using Piper TTS (ONNX).
Much faster than Chatterbox (~20-50x realtime) but lower quality.

Endpoints:
  GET  /health     - Health check (returns model status)
  POST /tts        - Generate speech from text (returns WAV audio)
"""

import io
import logging
import os
import subprocess
import tempfile
import time
import wave
from contextlib import asynccontextmanager
from pathlib import Path
from typing import Optional

from fastapi import FastAPI, HTTPException
from fastapi.responses import StreamingResponse
from pydantic import BaseModel, Field

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
    
    PORT: int = int(os.getenv("TTS_PORT", "5001"))
    LOG_LEVEL: str = os.getenv("LOG_LEVEL", "INFO")
    
    # Model settings
    MODEL_NAME: str = os.getenv("PIPER_MODEL", "en_US-lessac-medium")
    MODELS_DIR: Path = Path(os.getenv("PIPER_MODELS_DIR", "/app/models"))
    
    # Audio settings  
    SAMPLE_RATE: int = 22050  # Piper default


# ============================================================================
# Piper TTS Wrapper
# ============================================================================

class PiperTTS:
    """
    Wrapper for Piper TTS command-line tool.
    """
    
    _instance: Optional["PiperTTS"] = None
    
    def __init__(self):
        self.model_path: Optional[Path] = None
        self.config_path: Optional[Path] = None
        self.is_ready = False
        self.last_error: Optional[str] = None
        self.sample_rate = Config.SAMPLE_RATE
    
    @classmethod
    def get_instance(cls) -> "PiperTTS":
        if cls._instance is None:
            cls._instance = PiperTTS()
        return cls._instance
    
    def initialize(self) -> bool:
        """Initialize Piper and verify model exists."""
        try:
            # Check if piper is installed
            result = subprocess.run(
                ["piper", "--help"],
                capture_output=True,
                text=True,
                timeout=10
            )
            if result.returncode != 0:
                self.last_error = "Piper not found or not working"
                return False
            
            logger.info("Piper TTS binary found")
            
            # Find model files
            model_name = Config.MODEL_NAME
            models_dir = Config.MODELS_DIR
            
            # Look for model file
            self.model_path = models_dir / f"{model_name}.onnx"
            self.config_path = models_dir / f"{model_name}.onnx.json"
            
            if not self.model_path.exists():
                # Try to download model
                logger.info(f"Model not found at {self.model_path}, attempting download...")
                if not self._download_model(model_name):
                    return False
            
            if not self.model_path.exists() or not self.config_path.exists():
                self.last_error = f"Model files not found: {self.model_path}"
                logger.error(self.last_error)
                return False
            
            logger.info(f"Model loaded: {self.model_path}")
            
            # Get sample rate from config
            try:
                import json
                with open(self.config_path) as f:
                    config = json.load(f)
                    self.sample_rate = config.get("audio", {}).get("sample_rate", 22050)
                    logger.info(f"Sample rate: {self.sample_rate}")
            except Exception as e:
                logger.warning(f"Could not read config, using default sample rate: {e}")
            
            # Run warmup
            self._warmup()
            
            self.is_ready = True
            self.last_error = None
            return True
            
        except Exception as e:
            self.last_error = str(e)
            logger.error(f"Failed to initialize Piper: {e}")
            return False
    
    def _download_model(self, model_name: str) -> bool:
        """Download model from Hugging Face."""
        try:
            Config.MODELS_DIR.mkdir(parents=True, exist_ok=True)
            
            # Use piper's built-in download (if available) or wget
            base_url = f"https://huggingface.co/rhasspy/piper-voices/resolve/main/en/en_US/lessac/medium"
            
            model_url = f"{base_url}/en_US-lessac-medium.onnx"
            config_url = f"{base_url}/en_US-lessac-medium.onnx.json"
            
            logger.info(f"Downloading model from {model_url}...")
            
            # Download model
            subprocess.run([
                "wget", "-q", "-O", str(self.model_path), model_url
            ], check=True, timeout=300)
            
            # Download config
            subprocess.run([
                "wget", "-q", "-O", str(self.config_path), config_url
            ], check=True, timeout=60)
            
            logger.info("Model downloaded successfully")
            return True
            
        except Exception as e:
            logger.error(f"Failed to download model: {e}")
            self.last_error = f"Model download failed: {e}"
            return False
    
    def _warmup(self):
        """Run warmup inference."""
        logger.info("Running warmup inference...")
        start = time.perf_counter()
        
        try:
            _ = self.generate("Hello, this is a warmup test.")
            warmup_ms = (time.perf_counter() - start) * 1000
            logger.info(f"Warmup complete in {warmup_ms:.0f}ms")
        except Exception as e:
            logger.warning(f"Warmup failed (non-fatal): {e}")
    
    def generate(self, text: str) -> bytes:
        """
        Generate speech from text using Piper.
        
        Returns: WAV audio bytes
        """
        if not self.is_ready:
            raise RuntimeError("Piper not initialized")
        
        with tempfile.NamedTemporaryFile(suffix=".wav", delete=False) as tmp:
            tmp_path = tmp.name
        
        try:
            # Run piper
            result = subprocess.run(
                [
                    "piper",
                    "--model", str(self.model_path),
                    "--config", str(self.config_path),
                    "--output_file", tmp_path,
                ],
                input=text,
                capture_output=True,
                text=True,
                timeout=30,
            )
            
            if result.returncode != 0:
                raise RuntimeError(f"Piper failed: {result.stderr}")
            
            # Read output
            with open(tmp_path, "rb") as f:
                wav_data = f.read()
            
            return wav_data
            
        finally:
            # Cleanup
            try:
                os.unlink(tmp_path)
            except:
                pass


# ============================================================================
# FastAPI App
# ============================================================================

@asynccontextmanager
async def lifespan(app: FastAPI):
    """Startup and shutdown lifecycle."""
    logger.info("=" * 60)
    logger.info("EDDA TTS Service (Piper) Starting")
    logger.info(f"Model: {Config.MODEL_NAME}")
    logger.info(f"Models dir: {Config.MODELS_DIR}")
    logger.info("=" * 60)
    
    piper = PiperTTS.get_instance()
    if not piper.initialize():
        logger.error("Piper failed to initialize - service will return 503 on /tts")
    
    yield
    
    logger.info("Piper TTS Service shutting down")


app = FastAPI(
    title="EDDA TTS Service (Piper)",
    description="Fast text-to-speech synthesis using Piper",
    version="1.0.0",
    lifespan=lifespan,
)


# ============================================================================
# Request/Response Models
# ============================================================================

class TTSRequest(BaseModel):
    """Request body for /tts endpoint."""
    
    text: str = Field(..., min_length=1, max_length=5000, description="Text to synthesize")
    voice_reference: Optional[str] = Field(
        None,
        description="Ignored for Piper (no voice cloning support)",
    )
    exaggeration: float = Field(
        0.5,
        ge=0.0,
        le=1.0,
        description="Ignored for Piper",
    )
    cfg_weight: float = Field(
        0.5,
        ge=0.0,
        le=1.0,
        description="Ignored for Piper",
    )


class HealthResponse(BaseModel):
    """Response body for /health endpoint."""
    
    status: str
    model_loaded: bool
    device: str
    cuda_available: bool = False
    cuda_device: Optional[str] = None
    vram_total_gb: Optional[float] = None
    vram_used_gb: Optional[float] = None
    load_time_ms: Optional[float] = None
    last_error: Optional[str] = None
    model_name: Optional[str] = None


# ============================================================================
# Endpoints
# ============================================================================

@app.get("/health", response_model=HealthResponse)
async def health_check():
    """Health check endpoint."""
    piper = PiperTTS.get_instance()
    
    return HealthResponse(
        status="healthy" if piper.is_ready else "unhealthy",
        model_loaded=piper.is_ready,
        device="cpu",  # Piper uses CPU (ONNX)
        cuda_available=False,
        last_error=piper.last_error,
        model_name=Config.MODEL_NAME,
    )


@app.post("/tts")
async def text_to_speech(request: TTSRequest):
    """
    Generate speech from text.
    
    Returns WAV audio.
    """
    piper = PiperTTS.get_instance()
    
    if not piper.is_ready:
        raise HTTPException(
            status_code=503,
            detail="Piper TTS not initialized. Check /health for details.",
        )
    
    try:
        start = time.perf_counter()
        
        # Generate audio
        wav_data = piper.generate(request.text)
        
        generation_ms = (time.perf_counter() - start) * 1000
        
        # Calculate duration from WAV
        wav_buffer = io.BytesIO(wav_data)
        with wave.open(wav_buffer, 'rb') as wf:
            frames = wf.getnframes()
            rate = wf.getframerate()
            audio_duration_s = frames / rate
        wav_buffer.seek(0)
        
        rtf = generation_ms / (audio_duration_s * 1000) if audio_duration_s > 0 else 0
        
        logger.info(
            f"TTS [cpu/onnx]: {len(request.text)} chars -> {audio_duration_s:.2f}s audio | "
            f"gen={generation_ms:.0f}ms ({1/rtf:.1f}x RT)"
        )
        
        return StreamingResponse(
            io.BytesIO(wav_data),
            media_type="audio/wav",
            headers={
                "X-Generation-Time-Ms": str(int(generation_ms)),
                "X-Audio-Duration-S": f"{audio_duration_s:.2f}",
                "X-Realtime-Factor": f"{1/rtf:.1f}" if rtf > 0 else "inf",
            },
        )
        
    except Exception as e:
        logger.error(f"TTS generation failed: {e}")
        raise HTTPException(status_code=500, detail=str(e))


@app.get("/")
async def root():
    """Root endpoint - service info."""
    return {
        "service": "EDDA TTS (Piper)",
        "model": Config.MODEL_NAME,
        "endpoints": {
            "/health": "GET - Health check",
            "/tts": "POST - Text-to-speech synthesis",
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

