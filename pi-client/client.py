#!/usr/bin/env python3
"""
EDDA Voice Client - Main entry point.

Captures audio from microphone, performs VAD, streams to server,
and plays back TTS responses.
"""

import asyncio
import sys
import yaml
from datetime import datetime

import websockets

from edda.audio import AudioDevice, AudioProcessor, AudioPlayer
from edda.audio.device import AudioConfig, AudioStallError
from edda.network import ServerConnection
from edda.network.connection import MessageType
from edda.speech import SpeechDetector, SpeechEvent
from edda.speech.detector import SpeechConfig


# Force unbuffered output
sys.stdout.reconfigure(line_buffering=True)
sys.stderr.reconfigure(line_buffering=True)


def load_config(path: str = "config.yaml") -> dict:
    """Load configuration from YAML file."""
    try:
        with open(path, "r") as f:
            return yaml.safe_load(f)
    except Exception as e:
        print(f"Error loading {path}: {e}")
        sys.exit(1)


async def receive_messages(connection: ServerConnection, websocket, 
                           player: AudioPlayer, playback_event: asyncio.Event,
                           detector: SpeechDetector):
    """Listen for incoming messages from the server."""
    try:
        async for msg in connection.receive_messages(websocket):
            if msg.type == MessageType.AUDIO_PLAYBACK:
                audio = msg.audio
                
                # Calculate time-to-first-audio on first chunk
                if audio.chunk == 1 and detector.last_speech_end_time:
                    ttfa = (datetime.now() - detector.last_speech_end_time).total_seconds() * 1000
                    print(f"\n⚡ TIME TO FIRST AUDIO: {ttfa:.0f}ms")
                    detector.clear_speech_end_time()
                
                print(f"[RECV] Audio chunk {audio.chunk}/{audio.total_chunks}")
                print(f"[RECV] Decoded {len(audio.data)} bytes of audio")
                
                # Signal that we're playing (pauses mic capture)
                playback_event.set()
                
                # Play in executor to not block
                loop = asyncio.get_event_loop()
                await loop.run_in_executor(None, player.play_wav, audio.data)
                
            elif msg.type == MessageType.RESPONSE_COMPLETE:
                print("[RECV] Response complete - resuming mic capture")
                playback_event.clear()
                
    except websockets.exceptions.ConnectionClosed:
        raise


async def capture_and_send(connection: ServerConnection, websocket,
                           device: AudioDevice, processor: AudioProcessor,
                           detector: SpeechDetector, playback_event: asyncio.Event,
                           config: dict):
    """Capture audio from mic and send to server with VAD filtering."""
    print("Listening for speech...")
    
    stream = device.open_input_stream()
    if stream is None:
        raise RuntimeError("Failed to open audio stream")
    
    audio_config = device.config
    stall_timeout = config.get('audio', {}).get('stall_timeout', 5.0)
    last_audio_time = datetime.now()
    
    try:
        while True:
            # Pause capture during playback
            if playback_event.is_set():
                await asyncio.sleep(0.05)
                continue
            
            # Read audio with stall detection
            loop = asyncio.get_event_loop()
            try:
                data = await asyncio.wait_for(
                    loop.run_in_executor(
                        None, 
                        lambda: stream.read(audio_config.chunk_size, exception_on_overflow=False)
                    ),
                    timeout=stall_timeout
                )
                last_audio_time = datetime.now()
            except asyncio.TimeoutError:
                stall_duration = (datetime.now() - last_audio_time).total_seconds()
                raise AudioStallError(f"Audio device stalled for {stall_duration:.1f}s")
            
            # Resample and detect speech
            resampled = processor.resample(data, audio_config.capture_rate, audio_config.target_rate)
            speech_prob = processor.detect_speech(resampled, audio_config.target_rate)
            is_speech = speech_prob > processor.vad_threshold
            
            # Process through state machine
            result = detector.process(resampled, is_speech, speech_prob)
            
            if result.event == SpeechEvent.STARTED:
                # Send pre-buffered chunks + current
                for chunk in result.chunks_to_send:
                    await connection.send_audio_chunk(websocket, chunk)
                    
            elif result.event == SpeechEvent.CONTINUING:
                # Send current chunk
                for chunk in result.chunks_to_send:
                    await connection.send_audio_chunk(websocket, chunk)
                    
            elif result.event == SpeechEvent.ENDED:
                # Signal end of speech
                await connection.send_end_speech(websocket)
                
    finally:
        device.close_stream()


async def connect_and_stream(device: AudioDevice, processor: AudioProcessor,
                             player: AudioPlayer, detector: SpeechDetector,
                             connection: ServerConnection, config: dict):
    """Connect to server and stream. Reconnects on failure."""
    reconnect_delay = config.get('network', {}).get('reconnect_delay', 3)
    
    while True:
        try:
            async with connection.connect() as websocket:
                print("Connected!")
                
                playback_event = asyncio.Event()
                
                capture_task = asyncio.create_task(
                    capture_and_send(connection, websocket, device, processor,
                                     detector, playback_event, config)
                )
                receive_task = asyncio.create_task(
                    receive_messages(connection, websocket, player,
                                     playback_event, detector)
                )
                
                done, pending = await asyncio.wait(
                    [capture_task, receive_task],
                    return_when=asyncio.FIRST_COMPLETED
                )
                
                for task in pending:
                    task.cancel()
                    try:
                        await task
                    except asyncio.CancelledError:
                        pass
                
                for task in done:
                    task.result()
                    
        except AudioStallError as e:
            print(f"\n[FATAL] {e}")
            print("Audio device requires restart. Exiting...")
            sys.exit(1)
        except websockets.exceptions.ConnectionClosed as e:
            print(f"Connection closed: {e}. Reconnecting in {reconnect_delay}s...")
        except ConnectionRefusedError:
            print(f"Server not available. Retrying in {reconnect_delay}s...")
        except OSError as e:
            print(f"Network error: {e}. Retrying in {reconnect_delay}s...")
        except Exception as e:
            print(f"Error: {e}. Retrying in {reconnect_delay}s...")
            import traceback
            traceback.print_exc()
        
        detector.reset()
        await asyncio.sleep(reconnect_delay)


async def main():
    print("[PI] EDDA Client starting...")
    
    # Load config
    config = load_config()
    
    # Build audio config
    audio_cfg = config.get('audio', {})
    audio_config = AudioConfig(
        capture_rate=audio_cfg.get('capture_rate', 48000),
        target_rate=audio_cfg.get('target_rate', 16000),
        chunk_size=audio_cfg.get('chunk_size', 1440),
        channels=audio_cfg.get('channels', 1),
        input_device_name=audio_cfg.get('input_device_name', 'SoloCast')
    )
    
    # Build speech config from audio params
    vad_cfg = config.get('vad', {})
    speech_config = SpeechConfig.from_audio_config(
        chunk_size=audio_config.chunk_size,
        sample_rate=audio_config.target_rate,
        pre_buffer_ms=vad_cfg.get('pre_buffer_ms', 300.0),
        silence_duration_ms=vad_cfg.get('silence_duration_ms', 900.0)
    )
    
    # Initialize components
    device = AudioDevice(audio_config)
    if not device.initialize():
        print("Failed to initialize audio. Exiting.")
        return
    
    processor = AudioProcessor(vad_threshold=vad_cfg.get('threshold', 0.5))
    if not processor.load_vad_model():
        print("Failed to load VAD model. Exiting.")
        device.close()
        return
    
    player = AudioPlayer()
    detector = SpeechDetector(speech_config)
    
    server_cfg = config.get('server', {})
    server_url = f"ws://{server_cfg.get('host', '10.0.0.176')}:{server_cfg.get('port', 8080)}/ws"
    connection = ServerConnection(server_url)
    
    try:
        await connect_and_stream(device, processor, player, detector, connection, config)
    except KeyboardInterrupt:
        print("Interrupted by user.")
    finally:
        device.close()


if __name__ == "__main__":
    try:
        asyncio.run(main())
    except KeyboardInterrupt:
        print("\nShutting down.")
