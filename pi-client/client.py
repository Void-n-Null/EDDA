#!/usr/bin/env python3
"""
EDDA Voice Client - Main entry point.

Thin coordinator: initializes components, wires them together, runs connection loop.
"""

import asyncio
import logging
import sys
from pathlib import Path
import yaml

import websockets

from edda.audio import AudioDevice, AudioProcessor, AudioPlayer, EchoCanceller, AecConfig
from edda.audio.device import AudioConfig, AudioStallError
from edda.cache import CacheManager
from edda.network import ServerConnection, MessageHandler
from edda.speech import SpeechDetector, SpeechConfig, InputPipeline


# Unbuffered output
sys.stdout.reconfigure(line_buffering=True)
sys.stderr.reconfigure(line_buffering=True)

# File-based logging as backup (in case systemd logging fails)
log_dir = Path(__file__).parent / "logs"
log_dir.mkdir(exist_ok=True)
logging.basicConfig(
    level=logging.INFO,
    format='%(asctime)s [%(levelname)s] %(message)s',
    handlers=[
        logging.StreamHandler(sys.stdout),  # Also print to stdout
        logging.FileHandler(log_dir / "edda-client.log"),
    ]
)
logger = logging.getLogger(__name__)


def load_config(path: str = "config.yaml") -> dict:
    """Load configuration from YAML file."""
    try:
        with open(path, "r") as f:
            return yaml.safe_load(f)
    except Exception as e:
        logger.error(f"Error loading {path}: {e}")
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
        echo_canceller=components.get("echo_canceller"),
        audio_player=components["player"],
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
                logger.info("Connected to server!")
                await run_session(websocket, components, config)

        except AudioStallError as e:
            logger.critical(f"FATAL: {e}")
            logger.critical("Audio device requires restart. Exiting...")
            sys.exit(1)
        except websockets.exceptions.ConnectionClosed as e:
            logger.warning(f"Connection closed: {e}. Reconnecting in {reconnect_delay}s...")
        except ConnectionRefusedError:
            logger.warning(f"Server not available. Retrying in {reconnect_delay}s...")
        except OSError as e:
            logger.error(f"Network error: {e}. Retrying in {reconnect_delay}s...")
        except Exception as e:
            logger.error(f"Unexpected error: {e}. Retrying in {reconnect_delay}s...", exc_info=True)

        detector.reset()
        await asyncio.sleep(reconnect_delay)


def init_components(config: dict) -> dict:
    """Initialize all components from config. Returns dict or exits on failure."""
    audio_cfg = config.get("audio", {})
    vad_cfg = config.get("vad", {})
    cache_cfg = config.get("cache", {})
    server_cfg = config.get("server", {})
    aec_cfg = config.get("aec", {})

    # Audio config
    audio_config = AudioConfig(
        capture_rate=audio_cfg.get("capture_rate", 48000),
        target_rate=audio_cfg.get("target_rate", 16000),
        chunk_size=audio_cfg.get("chunk_size", 1440),
        channels=audio_cfg.get("channels", 1),
        input_device_name=audio_cfg.get("input_device_name", "default"),
        echo_cancellation=audio_cfg.get("echo_cancellation", True),
        vad_threshold_normal=vad_cfg.get("threshold", 0.5),
        vad_threshold_playback=audio_cfg.get("vad_threshold_playback", 0.92),
    )

    # Audio device
    device = AudioDevice(audio_config)
    if not device.initialize():
        logger.error("Failed to initialize audio device. Exiting.")
        sys.exit(1)

    # Audio processor (VAD)
    processor = AudioProcessor(vad_threshold=vad_cfg.get("threshold", 0.5))
    if not processor.load_vad_model():
        logger.error("Failed to load VAD model. Exiting.")
        device.close()
        sys.exit(1)

    # Echo canceller (AEC) - speexdsp-based echo cancellation
    echo_canceller = None
    if audio_cfg.get("echo_cancellation", True) and aec_cfg.get("enabled", True):
        aec_config = AecConfig(
            sample_rate=audio_cfg.get("target_rate", 16000),
            frame_size=aec_cfg.get("frame_size", 160),  # 10ms at 16kHz
            filter_length_ms=aec_cfg.get("filter_length_ms", 400),  # 400ms echo tail
            enable_preprocess=aec_cfg.get("enable_preprocess", True),
            buffer_duration_ms=aec_cfg.get("buffer_duration_ms", 5000),
            speaker_to_mic_delay_ms=aec_cfg.get("speaker_to_mic_delay_ms", 50),
        )
        echo_canceller = EchoCanceller(aec_config)
        if echo_canceller.initialize():
            logger.info("AEC (speexdsp) initialized successfully")
        else:
            logger.warning("AEC initialization failed, falling back to threshold-based filtering")
            echo_canceller = None

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
        "player": AudioPlayer(echo_canceller=echo_canceller),
        "detector": SpeechDetector(speech_config),
        "cache": CacheManager(
            cache_dir=cache_cfg.get("directory", "./cache"),
            clear_policy=cache_cfg.get("clear_policy", "never"),
            max_size_mb=cache_cfg.get("max_size_mb", 100),
        ),
        "connection": ServerConnection(server_url),
        "echo_canceller": echo_canceller,
    }


async def main():
    logger.info("=" * 60)
    logger.info("EDDA Voice Client Starting")
    logger.info("=" * 60)

    config = load_config()
    components = init_components(config)

    try:
        await connection_loop(components, config)
    except KeyboardInterrupt:
        logger.info("Interrupted by user.")
    except Exception as e:
        logger.critical(f"Fatal error in main loop: {e}", exc_info=True)
        raise
    finally:
        logger.info("Closing audio device...")
        components["device"].close()
        logger.info("Shutdown complete.")


if __name__ == "__main__":
    try:
        asyncio.run(main())
    except KeyboardInterrupt:
        logger.info("Keyboard interrupt - shutting down.")
    except Exception as e:
        logger.critical(f"Unhandled exception at top level: {e}", exc_info=True)
        sys.exit(1)
