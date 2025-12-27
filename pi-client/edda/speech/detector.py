"""Speech detection state machine."""

from collections import deque
from dataclasses import dataclass, field
from datetime import datetime
from enum import Enum, auto
from typing import List, Optional


class SpeechEvent(Enum):
    """Events emitted by the speech detector."""
    SILENCE = auto()       # No speech detected, buffering
    STARTED = auto()       # Speech just started
    CONTINUING = auto()    # Speech is ongoing
    ENDED = auto()         # Speech just ended


@dataclass
class SpeechConfig:
    """Configuration for speech detection timing."""
    # Pre-buffer duration in milliseconds (audio to keep before speech starts)
    pre_buffer_ms: float = 300.0
    
    # Silence duration in milliseconds before ending speech
    silence_duration_ms: float = 900.0
    
    # Chunk duration in milliseconds (calculated from sample rate and chunk size)
    chunk_duration_ms: float = 30.0  # Will be recalculated
    
    @classmethod
    def from_audio_config(cls, chunk_size: int, sample_rate: int, 
                          pre_buffer_ms: float = 300.0,
                          silence_duration_ms: float = 900.0) -> "SpeechConfig":
        """
        Create config from audio parameters.
        
        Args:
            chunk_size: Audio chunk size in samples
            sample_rate: Audio sample rate in Hz
            pre_buffer_ms: Pre-buffer duration in ms
            silence_duration_ms: Silence before end in ms
        """
        chunk_duration_ms = (chunk_size / sample_rate) * 1000
        return cls(
            pre_buffer_ms=pre_buffer_ms,
            silence_duration_ms=silence_duration_ms,
            chunk_duration_ms=chunk_duration_ms
        )
    
    @property
    def pre_buffer_chunks(self) -> int:
        """Number of chunks to pre-buffer."""
        return max(1, int(self.pre_buffer_ms / self.chunk_duration_ms))
    
    @property
    def max_silence_chunks(self) -> int:
        """Number of silence chunks before ending speech."""
        return max(1, int(self.silence_duration_ms / self.chunk_duration_ms))


@dataclass
class SpeechResult:
    """Result from processing an audio chunk."""
    event: SpeechEvent
    
    # Audio chunks to send (includes pre-buffer on STARTED)
    chunks_to_send: List[bytes] = field(default_factory=list)
    
    # Stats (populated on ENDED)
    duration_seconds: Optional[float] = None
    chunks_sent: Optional[int] = None
    
    # For timing measurements
    speech_ended_at: Optional[datetime] = None


class SpeechDetector:
    """
    State machine for speech detection.
    
    Manages:
    - Pre-buffering audio before speech starts
    - Detecting speech start/end transitions
    - Tracking speech duration and chunks
    - Timing for time-to-first-audio measurements
    
    Usage:
        detector = SpeechDetector(config)
        
        while True:
            audio_chunk = get_audio()
            is_speech = vad.is_speech(audio_chunk)
            
            result = detector.process(audio_chunk, is_speech)
            
            if result.event == SpeechEvent.STARTED:
                for chunk in result.chunks_to_send:
                    send(chunk)
            elif result.event == SpeechEvent.CONTINUING:
                send(result.chunks_to_send[0])
            elif result.event == SpeechEvent.ENDED:
                print(f"Speech ended: {result.duration_seconds}s")
    """
    
    def __init__(self, config: SpeechConfig):
        self.config = config
        
        # Pre-buffer for audio before speech
        self._pre_buffer: deque = deque(maxlen=config.pre_buffer_chunks)
        
        # State
        self._is_speaking: bool = False
        self._silence_chunks: int = 0
        self._chunks_sent: int = 0
        self._speech_start_time: Optional[datetime] = None
        
        # Timing for TTFA measurement
        self._last_speech_end_time: Optional[datetime] = None
    
    @property
    def is_speaking(self) -> bool:
        """Whether speech is currently being detected."""
        return self._is_speaking
    
    @property
    def last_speech_end_time(self) -> Optional[datetime]:
        """Timestamp of last speech end (for TTFA calculation)."""
        return self._last_speech_end_time
    
    def clear_speech_end_time(self):
        """Clear the speech end timestamp (call after TTFA is calculated)."""
        self._last_speech_end_time = None
    
    def process(self, audio_chunk: bytes, is_speech: bool, 
                speech_prob: float = 0.0) -> SpeechResult:
        """
        Process an audio chunk and update state.
        
        Args:
            audio_chunk: Raw audio bytes
            is_speech: Whether VAD detected speech
            speech_prob: Speech probability (for logging)
            
        Returns:
            SpeechResult indicating what action to take
        """
        if self._is_speaking:
            return self._process_speaking(audio_chunk, is_speech)
        else:
            return self._process_silent(audio_chunk, is_speech, speech_prob)
    
    def _process_speaking(self, audio_chunk: bytes, is_speech: bool) -> SpeechResult:
        """Handle state while speech is active."""
        if is_speech:
            # Still speaking
            self._silence_chunks = 0
            self._chunks_sent += 1
            return SpeechResult(
                event=SpeechEvent.CONTINUING,
                chunks_to_send=[audio_chunk]
            )
        else:
            # Possible end of speech
            self._silence_chunks += 1
            
            if self._silence_chunks >= self.config.max_silence_chunks:
                # Speech has ended
                duration = None
                if self._speech_start_time:
                    duration = (datetime.now() - self._speech_start_time).total_seconds()
                
                chunks_count = self._chunks_sent
                
                # Reset state
                self._is_speaking = False
                self._silence_chunks = 0
                self._speech_start_time = None
                chunks_sent = self._chunks_sent
                self._chunks_sent = 0
                
                # Record for TTFA
                self._last_speech_end_time = datetime.now()
                
                print(f"Speech ended: {duration:.1f}s, {chunks_sent} chunks sent")
                
                return SpeechResult(
                    event=SpeechEvent.ENDED,
                    duration_seconds=duration,
                    chunks_sent=chunks_count,
                    speech_ended_at=self._last_speech_end_time
                )
            else:
                # Still in grace period, keep sending
                self._chunks_sent += 1
                return SpeechResult(
                    event=SpeechEvent.CONTINUING,
                    chunks_to_send=[audio_chunk]
                )
    
    def _process_silent(self, audio_chunk: bytes, is_speech: bool, 
                        speech_prob: float) -> SpeechResult:
        """Handle state while no speech is active."""
        if is_speech:
            # Speech just started
            self._is_speaking = True
            self._silence_chunks = 0
            self._speech_start_time = datetime.now()
            
            # Collect pre-buffered chunks + current chunk
            chunks_to_send = list(self._pre_buffer) + [audio_chunk]
            self._chunks_sent = len(chunks_to_send)
            
            print(f"Speech started (VAD: {speech_prob:.2f})")
            
            return SpeechResult(
                event=SpeechEvent.STARTED,
                chunks_to_send=chunks_to_send
            )
        else:
            # No speech, just buffer
            self._pre_buffer.append(audio_chunk)
            return SpeechResult(event=SpeechEvent.SILENCE)
    
    def reset(self):
        """Reset detector state (e.g., after connection loss)."""
        self._pre_buffer.clear()
        self._is_speaking = False
        self._silence_chunks = 0
        self._chunks_sent = 0
        self._speech_start_time = None
        self._last_speech_end_time = None
