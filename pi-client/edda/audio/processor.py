"""Audio signal processing: resampling and VAD."""

from typing import Optional
import numpy as np
from scipy import signal
import torch


class AudioProcessor:
    """
    Handles audio signal processing including resampling and VAD.
    
    Usage:
        processor = AudioProcessor(vad_threshold=0.5)
        
        # Resample audio
        resampled = processor.resample(audio_bytes, 48000, 16000)
        
        # Detect speech (multi-window for full chunk analysis)
        speech_prob = processor.detect_speech(resampled, 16000)
    """
    
    # Silero VAD requirements
    VAD_WINDOW_SIZE = 512  # Samples at 16kHz (32ms)
    VAD_SAMPLE_RATE = 16000
    
    def __init__(self, vad_threshold: float = 0.5):
        self.vad_threshold = vad_threshold
        self._vad_model = None
        self._vad_utils = None
    
    def load_vad_model(self) -> bool:
        """
        Load the Silero VAD model.
        Returns True on success, False on failure.
        """
        try:
            print("Loading Silero VAD model...")
            self._vad_model, self._vad_utils = torch.hub.load(
                repo_or_dir='snakers4/silero-vad',
                model='silero_vad',
                force_reload=False,
                onnx=False
            )
            print("Silero VAD model loaded.")
            return True
        except Exception as e:
            print(f"[ERROR] Failed to load VAD model: {e}")
            return False
    
    def resample(self, audio_data: bytes, src_rate: int, dst_rate: int) -> bytes:
        """
        Resample audio from src_rate to dst_rate.
        
        Args:
            audio_data: Raw PCM audio bytes (16-bit signed int)
            src_rate: Source sample rate in Hz
            dst_rate: Destination sample rate in Hz
            
        Returns:
            Resampled audio as bytes
        """
        if src_rate == dst_rate:
            return audio_data
        
        # Convert bytes to numpy array (16-bit signed int)
        samples = np.frombuffer(audio_data, dtype=np.int16).astype(np.float32)
        
        # Calculate resampling ratio
        ratio = dst_rate / src_rate
        new_length = int(len(samples) * ratio)
        
        # Resample using scipy
        resampled = signal.resample(samples, new_length)
        
        # Convert back to 16-bit int
        resampled = np.clip(resampled, -32768, 32767).astype(np.int16)
        
        return resampled.tobytes()
    
    def detect_speech(self, audio_bytes: bytes, sample_rate: int = 16000) -> float:
        """
        Detect speech in audio using Silero VAD with multi-window analysis.
        
        Unlike the old single-window approach that only analyzed the first 512 samples
        and discarded the rest, this processes multiple overlapping windows to analyze
        the FULL audio chunk.
        
        Args:
            audio_bytes: Raw PCM audio bytes (16-bit signed int)
            sample_rate: Sample rate in Hz (must be 16000 for Silero VAD)
            
        Returns:
            Maximum speech probability across all windows (0.0-1.0)
        """
        if self._vad_model is None:
            return 0.0
        
        try:
            # Convert bytes to float32 normalized to [-1, 1]
            samples = np.frombuffer(audio_bytes, dtype=np.int16).astype(np.float32) / 32768.0
            
            if len(samples) < self.VAD_WINDOW_SIZE:
                # Too short for even one window
                return 0.0
            
            # Process multiple windows with 50% overlap
            window_size = self.VAD_WINDOW_SIZE
            hop_size = window_size // 2  # 50% overlap = 256 samples
            
            max_speech_prob = 0.0
            num_windows = 0
            
            # Slide window across the entire audio chunk
            for start in range(0, len(samples) - window_size + 1, hop_size):
                window = samples[start:start + window_size]
                audio_tensor = torch.from_numpy(window)
                
                with torch.no_grad():
                    speech_prob = self._vad_model(audio_tensor, sample_rate).item()
                
                max_speech_prob = max(max_speech_prob, speech_prob)
                num_windows += 1
                
                # Early exit: if we're confident there's speech, no need to check more
                if speech_prob > 0.9:
                    break
            
            return max_speech_prob
            
        except Exception as e:
            print(f"\n[VAD ERROR] {e}")
            import traceback
            traceback.print_exc()
            return 0.0
    
    def detect_speech_single_window(self, audio_bytes: bytes, sample_rate: int = 16000) -> float:
        """
        Legacy single-window VAD detection.
        Only analyzes first 512 samples - kept for comparison/debugging.
        
        Args:
            audio_bytes: Raw PCM audio bytes (16-bit signed int)
            sample_rate: Sample rate in Hz
            
        Returns:
            Speech probability for first window only (0.0-1.0)
        """
        if self._vad_model is None:
            return 0.0
        
        try:
            samples = np.frombuffer(audio_bytes, dtype=np.int16).astype(np.float32) / 32768.0
            
            if len(samples) < self.VAD_WINDOW_SIZE:
                return 0.0
            
            # Only take first window (old behavior)
            samples = samples[:self.VAD_WINDOW_SIZE]
            audio_tensor = torch.from_numpy(samples)
            
            with torch.no_grad():
                speech_prob = self._vad_model(audio_tensor, sample_rate).item()
            
            return speech_prob
            
        except Exception as e:
            print(f"\n[VAD ERROR] {e}")
            return 0.0
    
    def is_speech(self, audio_bytes: bytes, sample_rate: int = 16000) -> bool:
        """
        Check if audio contains speech above threshold.
        
        Args:
            audio_bytes: Raw PCM audio bytes
            sample_rate: Sample rate in Hz
            
        Returns:
            True if speech probability exceeds threshold
        """
        return self.detect_speech(audio_bytes, sample_rate) > self.vad_threshold
