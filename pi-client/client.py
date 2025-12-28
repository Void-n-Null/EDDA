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
from edda.cache import CacheManager
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
                           detector: SpeechDetector, cache_manager: CacheManager):
    """Listen for incoming messages from the server."""
    try:
        tts_first_pcm_chunk = True
        stream_chunk_counts = {"loading": 0, "tts": 0}
        pending_response_complete = False
        async for msg in connection.receive_messages(websocket):
            if msg.type == MessageType.AUDIO_STREAM_START:
                start = msg.stream_start
                if start is None:
                    continue
                
                # Starting any stream implies playback mode (pause mic capture)
                playback_event.set()
                
                # New stream: stop anything currently playing (loading or prior TTS)
                player.stop_current()
                
                # Reset TTFA marker when TTS stream starts
                if start.stream == "tts":
                    tts_first_pcm_chunk = True
                
                ok = player.start_stream(
                    stream_kind=start.stream,
                    sample_rate=start.sample_rate,
                    channels=start.channels,
                    sample_format=start.sample_format,
                    tempo=start.tempo,
                )
                if not ok:
                    print(f"[WARN] Failed to start stream {start.stream}")
                else:
                    stream_chunk_counts[start.stream] = 0
                
            elif msg.type == MessageType.AUDIO_STREAM_CHUNK:
                chunk = msg.stream_chunk
                if chunk is None:
                    continue
                
                # TTFA: measure when first PCM chunk of TTS arrives
                if tts_first_pcm_chunk and detector.last_speech_end_time:
                    # Only treat it as TTFA when we're receiving TTS stream data
                    if chunk.stream == "tts":
                        ttfa = (datetime.now() - detector.last_speech_end_time).total_seconds() * 1000
                        print(f"\n⚡ TIME TO FIRST AUDIO: {ttfa:.0f}ms")
                        detector.clear_speech_end_time()
                        tts_first_pcm_chunk = False
                
                stream_chunk_counts[chunk.stream] = stream_chunk_counts.get(chunk.stream, 0) + 1
                if stream_chunk_counts[chunk.stream] % 25 == 0:
                    print(f"[RECV] stream={chunk.stream} chunks={stream_chunk_counts[chunk.stream]}")
                player.write_stream(chunk.data)
                
            elif msg.type == MessageType.AUDIO_STREAM_END:
                if msg.stream:
                    print(f"[RECV] stream_end: {msg.stream}")
                player.end_stream()
                # Note: TTS no longer uses streaming (uses audio_sentence instead)
                # Loading audio still uses streaming
            
            elif msg.type == MessageType.AUDIO_SENTENCE:
                sentence = msg.audio_sentence
                if sentence is None:
                    continue
                
                # Signal that we're playing (pauses mic capture)
                playback_event.set()
                
                # Stop any loading audio that might be playing
                player.stop_current()
                
                # Calculate TTFA on first sentence
                if sentence.sentence_index == 1 and detector.last_speech_end_time:
                    ttfa = (datetime.now() - detector.last_speech_end_time).total_seconds() * 1000
                    print(f"\n⚡ TIME TO FIRST AUDIO: {ttfa:.0f}ms")
                    detector.clear_speech_end_time()
                
                # Log sentence metadata
                print(f"[RECV] Sentence {sentence.sentence_index}/{sentence.total_sentences}: "
                      f"{sentence.duration_ms}ms @ {sentence.sample_rate}Hz "
                      f"(tempo={sentence.tempo_applied:.3f}x, size={len(sentence.data)}B)")
                
                # Play sentence in executor (blocking playback)
                loop = asyncio.get_running_loop()
                await loop.run_in_executor(
                    None, 
                    player.play_wav_sentence,
                    sentence.data,
                    sentence.sentence_index,
                    sentence.total_sentences,
                    sentence.duration_ms,
                    sentence.sample_rate,
                    sentence.tempo_applied
                )
                
                # If this was the last sentence and we've already got RESPONSE_COMPLETE, resume mic
                if sentence.sentence_index == sentence.total_sentences and pending_response_complete:
                    print("[RECV] Final sentence played - resuming mic capture")
                    playback_event.clear()
                    pending_response_complete = False
                
            if msg.type == MessageType.AUDIO_LOADING:
                # Loading audio - plays while waiting for TTS, can be interrupted
                audio = msg.audio
                print(f"[RECV] Loading audio ({len(audio.data)} bytes) - will be cut off when TTS ready")
                
                # Signal that we're playing (pauses mic capture)
                playback_event.set()
                
                # Start async playback (non-blocking, can be interrupted)
                player.play_wav_async(audio.data)
                
            elif msg.type == MessageType.AUDIO_PLAYBACK:
                audio = msg.audio
                
                # Stop any loading audio that might be playing
                player.stop_current()
                
                # Calculate time-to-first-audio on first chunk
                if audio.chunk == 1 and detector.last_speech_end_time:
                    ttfa = (datetime.now() - detector.last_speech_end_time).total_seconds() * 1000
                    print(f"\n⚡ TIME TO FIRST AUDIO: {ttfa:.0f}ms")
                    detector.clear_speech_end_time()
                
                print(f"[RECV] Audio chunk {audio.chunk}/{audio.total_chunks}")
                print(f"[RECV] Decoded {len(audio.data)} bytes of audio")
                
                # Signal that we're playing (pauses mic capture)
                playback_event.set()
                
                # Play in executor to not block (blocking playback for real TTS)
                loop = asyncio.get_running_loop()
                await loop.run_in_executor(None, player.play_wav, audio.data)
                
            elif msg.type == MessageType.AUDIO_CACHE_PLAY:
                cache_play = msg.cache_play
                if cache_play is None:
                    continue
                
                print(f"[CACHE] Requested to play: {cache_play.cache_key} (loop={cache_play.loop})")
                
                # Check if we have it cached
                cached_data = cache_manager.get(cache_play.cache_key)
                if cached_data:
                    print(f"[CACHE] Playing from cache: {cache_play.cache_key} ({len(cached_data)}B)")
                    
                    # Signal playback mode
                    playback_event.set()
                    
                    # Play async if looping, otherwise blocking
                    if cache_play.loop:
                        player.play_wav_async(cached_data)
                    else:
                        loop = asyncio.get_running_loop()
                        await loop.run_in_executor(None, player.play_wav, cached_data)
                else:
                    print(f"[CACHE] Not cached, waiting for store: {cache_play.cache_key}")
            
            elif msg.type == MessageType.AUDIO_CACHE_STORE:
                cache_store = msg.cache_store
                if cache_store is None:
                    continue
                
                # Check if already cached
                if cache_manager.has(cache_store.cache_key):
                    print(f"[CACHE] Already cached: {cache_store.cache_key}")
                else:
                    # Store in cache
                    cache_manager.store(
                        cache_store.cache_key,
                        cache_store.data,
                        cache_store.sample_rate,
                        cache_store.channels,
                        cache_store.duration_ms
                    )
                    
                    # If we haven't played it yet (from AUDIO_CACHE_PLAY), play it now
                    # Check if the player isn't already playing something
                    print(f"[CACHE] Stored and playing: {cache_store.cache_key}")
                    playback_event.set()
                    player.play_wav_async(cache_store.data)
            
            elif msg.type == MessageType.RESPONSE_COMPLETE:
                # Server is done sending.
                pending_response_complete = True
                
                # Stop any async playback (loading audio loops)
                # If TTS sentences are playing, they'll handle cleanup themselves
                # If no TTS was sent, we need to stop loading audio and resume capture
                player.stop_current()
                
                # If playback_event is still set (meaning no TTS sentences cleared it), clear now
                if playback_event.is_set():
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
                             connection: ServerConnection, cache_manager: CacheManager,
                             config: dict):
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
                                     playback_event, detector, cache_manager)
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
    
    # Initialize cache manager
    cache_cfg = config.get('cache', {})
    cache_manager = CacheManager(
        cache_dir=cache_cfg.get('directory', './cache'),
        clear_policy=cache_cfg.get('clear_policy', 'never'),
        max_size_mb=cache_cfg.get('max_size_mb', 100)
    )
    
    server_cfg = config.get('server', {})
    server_url = f"ws://{server_cfg.get('host', '10.0.0.176')}:{server_cfg.get('port', 8080)}/ws"
    connection = ServerConnection(server_url)
    
    try:
        await connect_and_stream(device, processor, player, detector, connection, cache_manager, config)
    except KeyboardInterrupt:
        print("Interrupted by user.")
    finally:
        device.close()


if __name__ == "__main__":
    try:
        asyncio.run(main())
    except KeyboardInterrupt:
        print("\nShutting down.")
