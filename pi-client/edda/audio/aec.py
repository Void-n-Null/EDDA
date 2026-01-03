"""
Acoustic Echo Cancellation using speexdsp via pyaec.

This module provides application-level AEC by:
1. Buffering the reference signal (what we're playing to speakers)
2. Applying echo cancellation to the microphone input

Key insight: We already HAVE the reference signal - it's the PCM data
we send to the speaker. The challenge is TIME SYNCHRONIZATION - matching
what the mic hears to what was playing at that moment.
"""

import threading
import time
from collections import deque
from dataclasses import dataclass
from typing import Optional, Tuple
import numpy as np


@dataclass
class AecConfig:
    """Configuration for acoustic echo cancellation."""
    
    sample_rate: int = 16000
    frame_size: int = 160  # 10ms at 16kHz - must match speex requirements
    filter_length_ms: int = 400  # Echo tail length in milliseconds
    enable_preprocess: bool = True  # Enable speex noise suppression
    buffer_duration_ms: int = 15000  # How much playback history to keep (15s for long sentences)
    speaker_to_mic_delay_ms: int = 50  # Estimated delay from speaker to mic (acoustic + buffer)
    
    @property
    def filter_length(self) -> int:
        """Filter length in samples."""
        return int(self.sample_rate * self.filter_length_ms / 1000)
    
    @property
    def buffer_samples(self) -> int:
        """Buffer size in samples."""
        return int(self.sample_rate * self.buffer_duration_ms / 1000)
    
    @property
    def delay_samples(self) -> int:
        """Speaker-to-mic delay in samples."""
        return int(self.sample_rate * self.speaker_to_mic_delay_ms / 1000)


class ReferenceBuffer:
    """
    Thread-safe ring buffer for storing playback audio with TIME TRACKING.
    
    Unlike a simple FIFO, this buffer tracks WHEN audio was added and
    allows reading based on elapsed time since playback started.
    This is essential for WAV playback where we register all audio upfront
    but need to match it to real-time mic capture.
    """
    
    def __init__(self, max_samples: int, sample_rate: int):
        self._samples: np.ndarray = np.zeros(max_samples, dtype=np.int16)
        self._max_samples = max_samples
        self._sample_rate = sample_rate
        self._lock = threading.Lock()
        
        # Circular buffer pointers
        self._write_pos = 0
        self._total_written = 0
        
        # Time tracking for playback sync
        self._playback_start_time: Optional[float] = None
        self._playback_start_sample: int = 0
        
        # Track where current playback audio starts (captured BEFORE write)
        self._pending_start_sample: int = 0
    
    def begin_playback_registration(self) -> None:
        """
        Mark that we're about to register audio for a new playback.
        Call this BEFORE write() to capture the correct start position.
        
        Also CLEARS the buffer to prevent accumulation across sentences.
        Each sentence gets fresh timing from position 0.
        
        NOTE: Does NOT clear _playback_start_time - that's controlled by
        start_playback() and end_playback() to avoid breaking AEC while
        previous audio is still playing.
        """
        with self._lock:
            # Clear buffer for fresh start - prevents timing drift across sentences
            self._samples.fill(0)
            self._write_pos = 0
            self._total_written = 0
            self._pending_start_sample = 0
            # DON'T clear _playback_start_time here! Previous sentence may still be playing
    
    def start_playback(self) -> None:
        """Mark the start of actual playback (audio begins playing)."""
        with self._lock:
            self._playback_start_time = time.monotonic()
            # Use the position captured when registration began
            self._playback_start_sample = self._pending_start_sample
            
            # Debug disabled - uncomment for troubleshooting
            # samples_available = self._total_written - self._playback_start_sample
            # duration_ms = samples_available * 1000 // self._sample_rate
            # print(f"[AEC-DBG] Playback started: start_sample={self._playback_start_sample}, "
            #       f"total_written={self._total_written}, duration={duration_ms}ms")
    
    def write(self, samples: np.ndarray) -> None:
        """
        Add samples to the reference buffer.
        
        Args:
            samples: Audio samples as numpy array (int16 or float32)
        """
        # Ensure int16 format
        if samples.dtype == np.float32:
            samples = (samples * 32767).astype(np.int16)
        elif samples.dtype != np.int16:
            samples = samples.astype(np.int16)
        
        with self._lock:
            n = len(samples)
            
            # Write in chunks to handle wrap-around
            space_at_end = self._max_samples - self._write_pos
            if n <= space_at_end:
                self._samples[self._write_pos:self._write_pos + n] = samples
            else:
                # Wrap around
                self._samples[self._write_pos:] = samples[:space_at_end]
                self._samples[:n - space_at_end] = samples[space_at_end:]
            
            self._write_pos = (self._write_pos + n) % self._max_samples
            self._total_written += n
    
    def read_for_time(self, num_samples: int, delay_samples: int = 0) -> Tuple[Optional[np.ndarray], bool]:
        """
        Read samples corresponding to current playback time.
        
        This calculates which reference samples should have been playing
        at this moment based on elapsed time since playback started.
        
        Args:
            num_samples: Number of samples to read
            delay_samples: Additional delay to account for speaker-to-mic path
            
        Returns:
            Tuple of (samples array or None, whether we're within playback window)
        """
        with self._lock:
            if self._playback_start_time is None:
                return None, False
            
            # Calculate how many samples should have played by now
            elapsed = time.monotonic() - self._playback_start_time
            samples_elapsed = int(elapsed * self._sample_rate) - delay_samples
            
            if samples_elapsed < 0:
                # Haven't reached playback yet (still in delay period)
                return np.zeros(num_samples, dtype=np.int16), True
            
            # Calculate the buffer position for these samples
            target_sample = self._playback_start_sample + samples_elapsed
            samples_in_buffer = self._total_written - self._playback_start_sample
            
            if target_sample >= self._total_written:
                # Playback has ended (or we're past what was buffered)
                return None, False
            
            # Calculate buffer index (accounting for circular buffer)
            buffer_offset = target_sample % self._max_samples
            
            # Read samples (handle wrap-around)
            result = np.zeros(num_samples, dtype=np.int16)
            for i in range(num_samples):
                idx = (buffer_offset + i) % self._max_samples
                if self._playback_start_sample + samples_elapsed + i < self._total_written:
                    result[i] = self._samples[idx]
            
            return result, True
    
    def end_playback(self) -> None:
        """Mark the end of playback session."""
        with self._lock:
            self._playback_start_time = None
    
    @property
    def is_playing(self) -> bool:
        """Check if playback is active."""
        with self._lock:
            return self._playback_start_time is not None
    
    def available(self) -> int:
        """Return number of samples currently in buffer."""
        with self._lock:
            return min(self._total_written, self._max_samples)
    
    def clear(self) -> None:
        """Clear the buffer."""
        with self._lock:
            self._samples.fill(0)
            self._write_pos = 0
            self._total_written = 0
            self._playback_start_time = None
            self._playback_start_sample = 0


class EchoCanceller:
    """
    Application-level Acoustic Echo Cancellation.
    
    Uses TIME-BASED synchronization to match mic capture with speaker playback.
    This is essential for WAV playback where we register audio upfront but
    playback happens in real-time through ALSA buffering.
    
    Usage:
        aec = EchoCanceller(config)
        
        # When playing audio, register it and start playback timing:
        aec.register_playback(pcm_bytes)
        aec.start_playback()
        
        # When capturing audio, cancel the echo:
        clean_audio = aec.cancel_echo(mic_bytes)
        
        # When playback ends:
        aec.end_playback()
    """
    
    def __init__(self, config: Optional[AecConfig] = None):
        self.config = config or AecConfig()
        self._aec = None
        self._reference_buffer = ReferenceBuffer(
            self.config.buffer_samples, 
            self.config.sample_rate
        )
        self._initialized = False
        self._lock = threading.Lock()
        
        # Statistics for debugging
        self._frames_processed = 0
        self._frames_cancelled = 0
    
    def initialize(self) -> bool:
        """
        Initialize the AEC engine.
        
        Returns:
            True if initialization succeeded, False otherwise
        """
        try:
            from pyaec import Aec
            
            self._aec = Aec(
                self.config.frame_size,
                self.config.filter_length,
                self.config.sample_rate,
                self.config.enable_preprocess
            )
            self._initialized = True
            
            print(f"[AEC] Initialized: frame_size={self.config.frame_size}, "
                  f"filter_length={self.config.filter_length} samples "
                  f"({self.config.filter_length_ms}ms), "
                  f"delay={self.config.speaker_to_mic_delay_ms}ms, "
                  f"sample_rate={self.config.sample_rate}Hz")
            return True
            
        except ImportError as e:
            print(f"[AEC] pyaec not available: {e}")
            print("[AEC] Echo cancellation will be disabled")
            return False
        except Exception as e:
            print(f"[AEC] Failed to initialize: {e}")
            return False
    
    def begin_playback_registration(self) -> None:
        """
        Call this BEFORE registering audio to capture the correct start position.
        For WAV playback, call this before register_playback().
        """
        self._reference_buffer.begin_playback_registration()
    
    def register_playback(self, pcm_bytes: bytes, sample_rate: int = 16000, auto_start: bool = False, is_first_chunk: bool = False) -> None:
        """
        Register audio being played to speakers as the reference signal.
        
        For WAV playback, call this BEFORE playback starts with the full audio,
        then call start_playback() when audio actually begins.
        
        For streaming playback (write_stream), set auto_start=True so timing
        starts with the first chunk.
        
        Args:
            pcm_bytes: Raw PCM audio bytes (int16, mono)
            sample_rate: Sample rate of the audio
            auto_start: If True, start playback timing immediately
            is_first_chunk: If True, capture buffer start position (for streaming)
        """
        if not pcm_bytes:
            return
        
        # For streaming: capture start position on first chunk
        if is_first_chunk:
            self._reference_buffer.begin_playback_registration()
        
        # Convert to numpy array
        samples = np.frombuffer(pcm_bytes, dtype=np.int16).copy()
        
        # Resample if needed (should match AEC sample rate)
        if sample_rate != self.config.sample_rate:
            from scipy import signal
            ratio = self.config.sample_rate / sample_rate
            new_length = int(len(samples) * ratio)
            samples = signal.resample(samples, new_length).astype(np.int16)
        
        # Add to reference buffer
        self._reference_buffer.write(samples)
        
        # Auto-start playback timing if requested (for streaming)
        if auto_start and not self._reference_buffer.is_playing:
            self._reference_buffer.start_playback()
    
    def start_playback(self) -> None:
        """Explicitly start playback timing. Usually auto-started by register_playback."""
        self._reference_buffer.start_playback()
    
    def end_playback(self) -> None:
        """Signal that playback has ended."""
        self._reference_buffer.end_playback()
    
    def cancel_echo(self, mic_bytes: bytes) -> bytes:
        """
        Process microphone audio to remove echo from speaker playback.
        
        Uses time-based synchronization to read the correct reference samples
        that correspond to what was actually playing when the mic captured.
        
        Args:
            mic_bytes: Raw PCM audio from microphone (int16, mono)
            
        Returns:
            Processed audio with echo cancelled (same format as input)
        """
        if not self._initialized or self._aec is None:
            return mic_bytes
        
        if not self._reference_buffer.is_playing:
            return mic_bytes
        
        self._frames_processed += 1
        
        # Convert to numpy
        mic_samples = np.frombuffer(mic_bytes, dtype=np.int16).copy()
        
        # Process in frame-sized chunks
        frame_size = self.config.frame_size
        delay_samples = self.config.delay_samples
        output_samples = []
        frames_with_ref = 0
        frames_without_ref = 0
        ref_energy_sum = 0.0
        
        for i in range(0, len(mic_samples), frame_size):
            mic_frame = mic_samples[i:i + frame_size]
            
            # Pad if needed (last frame might be short)
            original_len = len(mic_frame)
            if len(mic_frame) < frame_size:
                mic_frame = np.pad(mic_frame, (0, frame_size - len(mic_frame)))
            
            # Get corresponding reference frame based on playback timing
            ref_frame, within_playback = self._reference_buffer.read_for_time(
                frame_size, delay_samples
            )
            
            if ref_frame is not None and within_playback:
                # We have reference - apply echo cancellation
                frames_with_ref += 1
                ref_energy_sum += np.sqrt(np.mean(ref_frame.astype(np.float32) ** 2))
                try:
                    processed = self._aec.cancel_echo(mic_frame, ref_frame)
                    output_samples.append(processed[:original_len] if original_len < frame_size else processed)
                    self._frames_cancelled += 1
                except Exception as e:
                    # Fallback to original on error
                    output_samples.append(mic_frame[:original_len] if original_len < frame_size else mic_frame)
            else:
                # No reference available or playback ended
                frames_without_ref += 1
                output_samples.append(mic_frame[:original_len] if original_len < frame_size else mic_frame)
        
        # Debug output disabled - uncomment for troubleshooting
        # if self._frames_processed % 30 == 0:
        #     avg_ref_energy = ref_energy_sum / max(frames_with_ref, 1)
        #     print(f"[AEC-DBG] frames_w_ref={frames_with_ref}, frames_no_ref={frames_without_ref}, "
        #           f"avg_ref_rms={avg_ref_energy:.0f}")
        
        # Concatenate
        if output_samples:
            result = np.concatenate(output_samples)
            return result.astype(np.int16).tobytes()
        return mic_bytes
    
    @property
    def is_active(self) -> bool:
        """Check if AEC is actively processing (playback is in progress)."""
        return self._reference_buffer.is_playing
    
    @property
    def stats(self) -> dict:
        """Get AEC statistics."""
        return {
            "initialized": self._initialized,
            "frames_processed": self._frames_processed,
            "frames_cancelled": self._frames_cancelled,
            "reference_buffer_samples": self._reference_buffer.available(),
            "is_playing": self._reference_buffer.is_playing,
        }
    
    def reset(self) -> None:
        """Reset the AEC state."""
        self._reference_buffer.clear()
        self._frames_processed = 0
        self._frames_cancelled = 0
        
        # Re-initialize AEC if needed
        if self._initialized:
            self.initialize()
