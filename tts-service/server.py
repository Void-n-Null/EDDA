"""
EDDA TTS Microservice - Chatterbox Turbo

Provides text-to-speech synthesis using Chatterbox Turbo (350M params).
Designed to run as a Docker container with NVIDIA GPU access.

Endpoints:
  GET  /health     - Health check (returns model status)
  POST /tts        - Generate speech from text (returns WAV audio)
"""

import io
import logging
import os
import time
from contextlib import asynccontextmanager
from typing import Optional

import torch
import torchaudio
from fastapi import FastAPI, HTTPException, Response
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
    
    DEVICE: str = os.getenv("TTS_DEVICE", "cuda" if torch.cuda.is_available() else "cpu")
    PORT: int = int(os.getenv("TTS_PORT", "5000"))
    LOG_LEVEL: str = os.getenv("LOG_LEVEL", "INFO")
    
    # Audio settings
    SAMPLE_RATE: int = 24000  # Chatterbox native sample rate
    
    # Model warmup
    WARMUP_TEXT: str = "Hello, this is a warmup test."


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
    
    @classmethod
    def get_instance(cls) -> "TTSModel":
        if cls._instance is None:
            cls._instance = TTSModel()
        return cls._instance
    
    def load(self) -> bool:
        """Load the Chatterbox Turbo model."""
        try:
            logger.info(f"Loading Chatterbox Turbo on device: {self.device}")
            start = time.perf_counter()
            
            # Import and load Chatterbox
            from chatterbox.tts import ChatterboxTTS
            
            self.model = ChatterboxTTS.from_pretrained(device=self.device)
            
            self.load_time_ms = (time.perf_counter() - start) * 1000
            logger.info(f"Model loaded in {self.load_time_ms:.0f}ms")
            
            # Warmup inference (first inference is always slower)
            self._warmup()
            
            self.is_ready = True
            self.last_error = None
            return True
            
        except Exception as e:
            self.last_error = str(e)
            logger.error(f"Failed to load model: {e}")
            return False
    
    def _warmup(self):
        """Run a warmup inference to prime CUDA kernels."""
        logger.info("Running warmup inference...")
        start = time.perf_counter()
        
        try:
            _ = self.model.generate(Config.WARMUP_TEXT)
            warmup_ms = (time.perf_counter() - start) * 1000
            logger.info(f"Warmup complete in {warmup_ms:.0f}ms")
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
    if not model.load():
        logger.error("Model failed to load - service will return 503 on /tts")
    
    yield
    
    # Shutdown: Cleanup
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
    voice_reference: Optional[str] = Field(
        None,
        description="Path to reference audio for voice cloning (on server filesystem)",
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
    )


@app.post("/tts")
async def text_to_speech(request: TTSRequest):
    """
    Generate speech from text.
    
    Returns streaming WAV audio (24kHz, 16-bit, mono).
    """
    model = TTSModel.get_instance()
    
    if not model.is_ready:
        raise HTTPException(
            status_code=503,
            detail="TTS model not loaded. Check /health for details.",
        )
    
    try:
        start = time.perf_counter()
        
        # Generate audio
        audio = model.generate(
            text=request.text,
            audio_prompt_path=request.voice_reference,
            exaggeration=request.exaggeration,
            cfg_weight=request.cfg_weight,
        )
        
        generation_ms = (time.perf_counter() - start) * 1000
        
        # Convert to WAV bytes
        wav_buffer = io.BytesIO()
        
        # Ensure audio is 2D (1, samples)
        if audio.dim() == 1:
            audio = audio.unsqueeze(0)
        
        # Move to CPU for saving
        audio_cpu = audio.cpu()
        
        torchaudio.save(
            wav_buffer,
            audio_cpu,
            model.sample_rate,
            format="wav",
        )
        wav_buffer.seek(0)
        
        # Calculate audio duration
        audio_duration_s = audio.shape[-1] / model.sample_rate
        rtf = generation_ms / (audio_duration_s * 1000)  # Real-time factor
        
        logger.info(
            f"TTS: {len(request.text)} chars -> {audio_duration_s:.2f}s audio "
            f"in {generation_ms:.0f}ms ({1/rtf:.1f}x realtime)"
        )
        
        return StreamingResponse(
            wav_buffer,
            media_type="audio/wav",
            headers={
                "X-Generation-Time-Ms": str(int(generation_ms)),
                "X-Audio-Duration-S": f"{audio_duration_s:.2f}",
                "X-Realtime-Factor": f"{1/rtf:.1f}",
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

