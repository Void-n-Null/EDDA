#!/usr/bin/env python3
"""
EDDA Voice Client - Main entry point.

Thin coordinator: initializes components, wires them together, runs connection loop.
"""

import asyncio
import sys
import yaml

import websockets

from edda.audio import AudioDevice, AudioProcessor, AudioPlayer
from edda.audio.device import AudioConfig, AudioStallError
from edda.cache import CacheManager
from edda.network import ServerConnection, MessageHandler
from edda.speech import SpeechDetector, SpeechConfig, InputPipeline


# Unbuffered output
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


async def run_session(websocket, components: dict, config: dict) -> None:
    """Run a single connected session until it ends."""
    playback_event = asyncio.Event()
    
    # Create session-scoped handlers
    handler = MessageHandler(
        components["player"],
        components["cache"],
        components["detector"],
        playback_event,
    )
    
    pipeline = InputPipeline(
        components["device"],
        components["processor"],
        components["detector"],
        components["connection"],
        stall_timeout=config.get("audio", {}).get("stall_timeout", 5.0),
    )
    
    # Run input and output pipelines concurrently
    async def receive_loop():
        async for msg in components["connection"].receive_messages(websocket):
            await handler.handle(msg)
    
    capture_task = asyncio.create_task(pipeline.run(websocket, playback_event))
    receive_task = asyncio.create_task(receive_loop())
    
    done, pending = await asyncio.wait(
        [capture_task, receive_task],
        return_when=asyncio.FIRST_COMPLETED,
    )
    
    for task in pending:
        task.cancel()
        try:
            await task
        except asyncio.CancelledError:
            pass
    
    for task in done:
        task.result()


async def connection_loop(components: dict, config: dict) -> None:
    """Reconnection loop with error handling."""
    reconnect_delay = config.get("network", {}).get("reconnect_delay", 3)
    connection = components["connection"]
    detector = components["detector"]
    
    while True:
        try:
            async with connection.connect() as websocket:
                print("Connected!")
                await run_session(websocket, components, config)
                
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


def init_components(config: dict) -> dict:
    """Initialize all components from config. Returns dict or exits on failure."""
    audio_cfg = config.get("audio", {})
    vad_cfg = config.get("vad", {})
    cache_cfg = config.get("cache", {})
    server_cfg = config.get("server", {})
    
    # Audio config
    audio_config = AudioConfig(
        capture_rate=audio_cfg.get("capture_rate", 48000),
        target_rate=audio_cfg.get("target_rate", 16000),
        chunk_size=audio_cfg.get("chunk_size", 1440),
        channels=audio_cfg.get("channels", 1),
        input_device_name=audio_cfg.get("input_device_name", "SoloCast"),
    )
    
    # Audio device
    device = AudioDevice(audio_config)
    if not device.initialize():
        print("Failed to initialize audio. Exiting.")
        sys.exit(1)
    
    # Audio processor (VAD)
    processor = AudioProcessor(vad_threshold=vad_cfg.get("threshold", 0.5))
    if not processor.load_vad_model():
        print("Failed to load VAD model. Exiting.")
        device.close()
        sys.exit(1)
    
    # Speech detection
    speech_config = SpeechConfig.from_audio_config(
        chunk_size=audio_config.chunk_size,
        sample_rate=audio_config.target_rate,
        pre_buffer_ms=vad_cfg.get("pre_buffer_ms", 300.0),
        silence_duration_ms=vad_cfg.get("silence_duration_ms", 900.0),
    )
    
    # Server connection
    server_url = f"ws://{server_cfg.get('host', '10.0.0.176')}:{server_cfg.get('port', 8080)}/ws"
    
    return {
        "device": device,
        "processor": processor,
        "player": AudioPlayer(),
        "detector": SpeechDetector(speech_config),
        "cache": CacheManager(
            cache_dir=cache_cfg.get("directory", "./cache"),
            clear_policy=cache_cfg.get("clear_policy", "never"),
            max_size_mb=cache_cfg.get("max_size_mb", 100),
        ),
        "connection": ServerConnection(server_url),
    }


async def main():
    print("[PI] EDDA Client starting...")
    
    config = load_config()
    components = init_components(config)
    
    try:
        await connection_loop(components, config)
    except KeyboardInterrupt:
        print("Interrupted by user.")
    finally:
        components["device"].close()


if __name__ == "__main__":
    try:
        asyncio.run(main())
    except KeyboardInterrupt:
        print("\nShutting down.")
