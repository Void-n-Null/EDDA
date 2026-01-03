"""
Input pipeline - captures audio, runs VAD, streams speech to server.
"""

import asyncio
from datetime import datetime
from typing import TYPE_CHECKING, Optional

if TYPE_CHECKING:
    from ..audio import AudioDevice, AudioProcessor, EchoCanceller
    from ..audio.device import AudioStallError
    from ..audio.playback import AudioPlayer
    from ..network import ServerConnection
    from .detector import SpeechDetector


class InputPipeline:
    """
    Orchestrates audio capture → AEC → VAD → speech detection → server transmission.
    
    This is the client-side input equivalent of the server's VoiceSession.
    Manages the flow from microphone to WebSocket.
    
    When an EchoCanceller is provided, applies acoustic echo cancellation
    to remove speaker audio from the microphone input.
    """
    
    # Number of consecutive VAD triggers before ducking volume
    DUCK_TRIGGER_COUNT = 3
    # Number of consecutive non-triggers before restoring volume
    RESTORE_SILENCE_COUNT = 5
    
    def __init__(
        self,
        device: "AudioDevice",
        processor: "AudioProcessor",
        detector: "SpeechDetector",
        connection: "ServerConnection",
        stall_timeout: float = 5.0,
        echo_canceller: Optional["EchoCanceller"] = None,
        audio_player: Optional["AudioPlayer"] = None,
    ):
        self._device = device
        self._processor = processor
        self._detector = detector
        self._connection = connection
        self._stall_timeout = stall_timeout
        self._echo_canceller = echo_canceller
        self._audio_player = audio_player
        
        # Consecutive trigger tracking for volume ducking
        self._consecutive_triggers = 0
        self._consecutive_silence = 0
        
        # VAD state tracking for sparse logging (avoid spam)
        # Uses hysteresis: need to go above 0.5 before "drop below 0.3" logs again
        self._vad_log_armed = False  # True = we've gone high enough to log next drop
    
    async def run(self, websocket, playback_event: asyncio.Event) -> None:
        """
        Run the capture loop until connection closes or fatal error.
        
        Args:
            websocket: Active WebSocket connection
            playback_event: Set when playback is active (affects VAD threshold or pauses capture)
        
        Raises:
            AudioStallError: If audio device stalls
            RuntimeError: If stream fails to open
        """
        from ..audio.device import AudioStallError
        from .detector import SpeechEvent
        
        stream = self._device.open_input_stream()
        if stream is None:
            raise RuntimeError("Failed to open audio stream")
        
        config = self._device.config
        last_audio_time = datetime.now()
        
        # Log startup with echo cancellation status
        if self._echo_canceller is not None:
            aec_stats = self._echo_canceller.stats
            if aec_stats["initialized"]:
                print(f"Listening for speech... (AEC mode: speexdsp echo cancellation active)")
            else:
                print(f"Listening for speech... (AEC failed to init, using threshold fallback)")
        elif config.echo_cancellation:
            print(f"Listening for speech... (threshold mode: {config.vad_threshold_normal:.2f} -> {config.vad_threshold_playback:.2f} during playback)")
        else:
            print("Listening for speech... (mic will mute during playback)")
        
        try:
            while True:
                # Legacy mode: pause during playback if EC is disabled
                if not config.echo_cancellation and playback_event.is_set():
                    await asyncio.sleep(0.05)
                    continue
                
                # Read audio with stall detection
                try:
                    data = await self._read_audio(stream, config.chunk_size)
                    last_audio_time = datetime.now()
                except asyncio.TimeoutError:
                    stall = (datetime.now() - last_audio_time).total_seconds()
                    raise AudioStallError(f"Audio device stalled for {stall:.1f}s")
                
                # Process through VAD and state machine
                # Pass playback state so we can adjust threshold
                is_playing = playback_event.is_set()
                await self._process_chunk(websocket, data, config, is_playing)
                
        finally:
            self._device.close_stream()
    
    async def _read_audio(self, stream, chunk_size: int) -> bytes:
        """Read audio chunk with timeout."""
        loop = asyncio.get_event_loop()
        return await asyncio.wait_for(
            loop.run_in_executor(
                None,
                lambda: stream.read(chunk_size, exception_on_overflow=False),
            ),
            timeout=self._stall_timeout,
        )
    
    async def _process_chunk(self, websocket, data: bytes, config, is_playing: bool = False) -> None:
        """Resample, apply AEC, run VAD, update state machine, send if needed."""
        from .detector import SpeechEvent
        import numpy as np
        
        # Resample to target rate (16kHz for VAD/AEC)
        resampled = self._processor.resample(
            data, config.capture_rate, config.target_rate
        )
        
        # Apply AEC if available and we have reference audio
        # This removes the speaker audio (TTS playback) from the mic input
        aec_applied = False
        if self._echo_canceller is not None and self._echo_canceller.is_active:
            resampled = self._echo_canceller.cancel_echo(resampled)
            aec_applied = True
        
        # Calculate audio level (RMS in dB)
        samples = np.frombuffer(resampled, dtype=np.int16).astype(np.float32)
        rms = np.sqrt(np.mean(samples ** 2)) if len(samples) > 0 else 0
        db = 20 * np.log10(rms / 32768 + 1e-10)  # dB relative to full scale
        
        # Run VAD on the (possibly AEC-processed) audio
        speech_prob = self._processor.detect_speech(resampled, config.target_rate)
        
        # Use elevated threshold during playback as a safety net
        # With AEC active, this threshold can be lower since echo should be cancelled
        if config.echo_cancellation and is_playing:
            if aec_applied:
                # AEC is doing the heavy lifting - use a moderate threshold
                threshold = config.vad_threshold_normal + 0.20
            else:
                # No AEC - rely on high threshold to filter speaker audio
                threshold = config.vad_threshold_playback
        else:
            threshold = config.vad_threshold_normal
        
        is_speech = speech_prob > threshold
        
        # Track consecutive triggers for volume ducking during playback
        # Only interact with volume when state actually changes to avoid subprocess spam
        if is_playing and self._audio_player is not None:
            if is_speech:
                self._consecutive_triggers += 1
                self._consecutive_silence = 0
                
                # Duck volume after sustained triggers (only if not already ducked)
                if self._consecutive_triggers == self.DUCK_TRIGGER_COUNT:
                    self._audio_player.duck_volume(duck_percent=25)
            else:
                self._consecutive_silence += 1
                # Don't reset trigger count immediately - let it decay
                if self._consecutive_silence > 0:
                    self._consecutive_triggers = max(0, self._consecutive_triggers - 1)
                
                # Restore volume after sustained silence (only if ducked)
                if self._consecutive_silence == self.RESTORE_SILENCE_COUNT and self._audio_player.is_volume_ducked:
                    self._audio_player.restore_volume()
        elif not is_playing:
            # Reset counters when not playing
            if self._consecutive_triggers > 0 or self._consecutive_silence > 0:
                self._consecutive_triggers = 0
                self._consecutive_silence = 0
                # Only restore if actually ducked
                if self._audio_player is not None and self._audio_player.is_volume_ducked:
                    self._audio_player.restore_volume()
        
        # Log audio metrics during playback - only on significant events to reduce spam
        # Uses hysteresis: must go above 0.5 to "arm", then logs when dropping below 0.3
        if is_playing:
            should_log = False
            
            if is_speech:
                # Always log actual triggers
                should_log = True
                self._vad_log_armed = True  # Arm for next drop
            elif speech_prob > 0.5:
                # Arm the logger - next drop below 0.3 will log
                self._vad_log_armed = True
            elif speech_prob < 0.3 and self._vad_log_armed:
                # Dropped below 0.3 after being armed - log and disarm
                should_log = True
                self._vad_log_armed = False
            
            if should_log:
                aec_status = "AEC" if aec_applied else "NO-AEC"
                status = "🔴 TRIGGERED" if is_speech else "⚪ filtered"
                print(f"[{aec_status}] {status} VAD={speech_prob:.2f} (thr={threshold:.2f}) dB={db:.1f}")
        
        # Process through state machine
        result = self._detector.process(resampled, is_speech, speech_prob)
        
        # Send based on event
        if result.event == SpeechEvent.STARTED:
            for chunk in result.chunks_to_send:
                await self._connection.send_audio_chunk(websocket, chunk)
        elif result.event == SpeechEvent.CONTINUING:
            for chunk in result.chunks_to_send:
                await self._connection.send_audio_chunk(websocket, chunk)
        elif result.event == SpeechEvent.ENDED:
            await self._connection.send_end_speech(websocket)
