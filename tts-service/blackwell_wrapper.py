"""
Blackwell GPU Wrapper for TTS Server

This wrapper patches torchaudio.save to use soundfile instead of torchcodec,
which doesn't support BytesIO on newer torchaudio versions (2.9+).

This allows the shared server.py to work on both:
- Older systems (2070 Super) with torchaudio's native backend
- Blackwell GPUs (5070 Ti) with CUDA 12.8 requiring newer torchaudio
"""

import io
import soundfile as sf


def patch_torchaudio_save():
    """Replace torchaudio.save with a soundfile-based implementation."""
    import torchaudio
    
    _original_save = torchaudio.save
    
    def patched_save(uri, src, sample_rate, **kwargs):
        # If saving to a file-like object (BytesIO), use soundfile
        if isinstance(uri, io.BytesIO):
            # torchaudio format is (channels, samples), soundfile wants (samples,) or (samples, channels)
            audio_np = src.squeeze().cpu().numpy()
            sf.write(uri, audio_np, sample_rate, format='WAV')
            return
        
        # For regular file paths, use original torchaudio.save
        return _original_save(uri, src, sample_rate, **kwargs)
    
    torchaudio.save = patched_save
    print("[Blackwell Wrapper] Patched torchaudio.save to use soundfile for BytesIO")


if __name__ == "__main__":
    # Apply patch before importing server
    patch_torchaudio_save()
    
    # Now run the server
    import uvicorn
    from server import app
    
    uvicorn.run(app, host="0.0.0.0", port=5000)
