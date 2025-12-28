"""
Input pipeline - captures audio, runs VAD, streams speech to server.
"""

import asyncio
from datetime import datetime
from typing import TYPE_CHECKING

if TYPE_CHECKING:
    from ..audio import AudioDevice, AudioProcessor
    from ..audio.device import AudioStallError
    from ..network import ServerConnection
    from .detector import SpeechDetector


class InputPipeline:
    """
    Orchestrates audio capture → VAD → speech detection → server transmission.
    
    This is the client-side input equivalent of the server's VoiceSession.
    Manages the flow from microphone to WebSocket.
    """
    
    def __init__(
        self,
        device: "AudioDevice",
        processor: "AudioProcessor",
        detector: "SpeechDetector",
        connection: "ServerConnection",
        stall_timeout: float = 5.0,
    ):
        self._device = device
        self._processor = processor
        self._detector = detector
        self._connection = connection
        self._stall_timeout = stall_timeout
    
    async def run(self, websocket, playback_event: asyncio.Event) -> None:
        """
        Run the capture loop until connection closes or fatal error.
        
        Args:
            websocket: Active WebSocket connection
            playback_event: Set when playback is active (pauses capture)
        
        Raises:
            AudioStallError: If audio device stalls
            RuntimeError: If stream fails to open
        """
        from ..audio.device import AudioStallError
        from .detector import SpeechEvent
        
        print("Listening for speech...")
        
        stream = self._device.open_input_stream()
        if stream is None:
            raise RuntimeError("Failed to open audio stream")
        
        config = self._device.config
        last_audio_time = datetime.now()
        
        try:
            while True:
                # Pause during playback
                if playback_event.is_set():
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
                await self._process_chunk(websocket, data, config)
                
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
    
    async def _process_chunk(self, websocket, data: bytes, config) -> None:
        """Resample, run VAD, update state machine, send if needed."""
        from .detector import SpeechEvent
        
        # Resample to target rate
        resampled = self._processor.resample(
            data, config.capture_rate, config.target_rate
        )
        
        # Run VAD
        speech_prob = self._processor.detect_speech(resampled, config.target_rate)
        is_speech = speech_prob > self._processor.vad_threshold
        
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
