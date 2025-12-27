import asyncio
import websockets
import json
import pyaudio
import base64
import yaml
import sys
import io
import wave
import numpy as np
from scipy import signal
from datetime import datetime
from collections import deque
import torch

# Force unbuffered output
sys.stdout.reconfigure(line_buffering=True)
sys.stderr.reconfigure(line_buffering=True)

# Load config
try:
    with open("config.yaml", "r") as f:
        config = yaml.safe_load(f)
except Exception as e:
    print(f"Error loading config.yaml: {e}")
    sys.exit(1)

SERVER_URL = f"ws://{config['server']['host']}:{config['server']['port']}/ws"
CAPTURE_RATE = config['audio'].get('capture_rate', 48000)
TARGET_RATE = config['audio'].get('target_rate', 16000)
CHUNK_SIZE = config['audio'].get('chunk_size', 1440)
CHANNELS = config['audio'].get('channels', 1)
RECONNECT_DELAY = 3  # seconds
AUDIO_STALL_TIMEOUT = 5.0  # seconds - exit if no audio data for this long

# VAD settings
VAD_THRESHOLD = 0.5  # Speech probability threshold (0.0-1.0)
VAD_SAMPLE_RATE = 16000  # Silero VAD expects 16kHz
PRE_BUFFER_CHUNKS = 10  # Keep 10 chunks (~300ms) before speech starts

# Load Silero VAD model
print("Loading Silero VAD model...")
vad_model, vad_utils = torch.hub.load(
    repo_or_dir='snakers4/silero-vad',
    model='silero_vad',
    force_reload=False,
    onnx=False
)
(get_speech_timestamps, save_audio, read_audio, VADIterator, collect_chunks) = vad_utils
print("Silero VAD model loaded.")

def resample_audio(audio_data: bytes, src_rate: int, dst_rate: int) -> bytes:
    """Resample audio from src_rate to dst_rate."""
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

def init_audio():
    """Initialize audio device. Returns (pyaudio instance, device_index) or exits on failure."""
    p = pyaudio.PyAudio()
    
    # Find HyperX SoloCast device
    device_index = None
    for i in range(p.get_device_count()):
        dev = p.get_device_info_by_index(i)
        if "SoloCast" in dev['name']:
            device_index = i
            break
    
    if device_index is None:
        print("Error: HyperX SoloCast not found!")
        print("Available devices:")
        for i in range(p.get_device_count()):
            dev = p.get_device_info_by_index(i)
            if dev['maxInputChannels'] > 0:
                print(f"  [{i}] {dev['name']} (rate: {dev['defaultSampleRate']})")
        p.terminate()
        return None, None

    dev_info = p.get_device_info_by_index(device_index)
    print(f"Using input device {device_index}: {dev_info['name']}")
    print(f"  Native rate: {dev_info['defaultSampleRate']} Hz")
    print(f"  Capture at: {CAPTURE_RATE} Hz -> Resample to: {TARGET_RATE} Hz")
    
    return p, device_index


def detect_speech(audio_bytes: bytes, sample_rate: int = VAD_SAMPLE_RATE) -> float:
    """
    Detect speech in audio using Silero VAD.
    Returns speech probability (0.0-1.0).
    
    Silero VAD is VERY picky: needs EXACTLY 512 samples at 16kHz (or 256 at 8kHz).
    """
    try:
        # Convert bytes to float32 tensor normalized to [-1, 1]
        samples = np.frombuffer(audio_bytes, dtype=np.int16).astype(np.float32) / 32768.0
        
        # Silero VAD needs EXACTLY 512 samples at 16kHz (or 256 at 8kHz)
        required_samples = 512 if sample_rate == 16000 else 256
        
        if len(samples) < required_samples:
            print(f"\n[VAD DEBUG] Too short: got {len(samples)}, need {required_samples}")
            return 0.0  # Too short, assume no speech
        
        # Take exactly the required number of samples (slice if longer)
        samples = samples[:required_samples]
        
        audio_tensor = torch.from_numpy(samples)
        
        # Silero VAD expects 16kHz mono audio
        with torch.no_grad():
            speech_prob = vad_model(audio_tensor, sample_rate).item()
        
        return speech_prob
    except Exception as e:
        print(f"\n[VAD ERROR] {e}")
        import traceback
        traceback.print_exc()
        return 0.0

class AudioStallError(Exception):
    """Raised when audio device stops providing data."""
    pass

def find_output_device(p):
    """Find the default output device for audio playback."""
    # Try to find headphone or default output
    default_output = p.get_default_output_device_info()
    print(f"Using output device: {default_output['name']}")
    return default_output['index']


def play_wav_audio(p, audio_data: bytes, output_device_index: int):
    """Play WAV audio data using aplay (ALSA handles sample rate conversion)."""
    import subprocess
    import tempfile
    import os
    
    try:
        # Get device info for aplay device name
        device_info = p.get_device_info_by_index(output_device_index)
        
        # Parse WAV to log what we're playing
        wav_buffer = io.BytesIO(audio_data)
        with wave.open(wav_buffer, 'rb') as wf:
            sample_rate = wf.getframerate()
            channels = wf.getnchannels()
            sample_width = wf.getsampwidth()
            print(f"Playing audio: {sample_rate}Hz, {channels}ch, {sample_width * 8}-bit")
        
        # Write to temp file and play with aplay (ALSA handles resampling via plug plugin)
        with tempfile.NamedTemporaryFile(suffix='.wav', delete=False) as tmp:
            tmp.write(audio_data)
            tmp_path = tmp.name
        
        try:
            # Use default ALSA device - the plug plugin will handle resampling
            result = subprocess.run(
                ['aplay', '-q', tmp_path],
                capture_output=True,
                text=True,
                timeout=30
            )
            if result.returncode != 0:
                print(f"[WARN] aplay stderr: {result.stderr}")
            else:
                print("Audio playback complete")
        finally:
            os.unlink(tmp_path)
            
    except Exception as e:
        print(f"[ERROR] Failed to play audio: {e}")
        import traceback
        traceback.print_exc()


async def receive_messages(websocket, p, output_device_index, playback_event):
    """Listen for incoming messages from the server."""
    try:
        async for message in websocket:
            try:
                data = json.loads(message)
                msg_type = data.get("type")
                
                if msg_type == "audio_playback":
                    print("[RECV] Audio playback message received")
                    
                    # Signal that we're playing audio (pauses mic capture)
                    playback_event.set()
                    
                    # Decode and play the audio
                    audio_data = base64.b64decode(data["data"])
                    print(f"[RECV] Decoded {len(audio_data)} bytes of audio")
                    
                    # Play in executor to not block
                    loop = asyncio.get_event_loop()
                    await loop.run_in_executor(
                        None,
                        play_wav_audio,
                        p, audio_data, output_device_index
                    )
                    
                    # Resume mic capture
                    playback_event.clear()
                    print("Resuming mic capture...")
                    
            except json.JSONDecodeError:
                print(f"[WARN] Received non-JSON message: {message[:100]}")
            except Exception as e:
                print(f"[ERROR] Error processing message: {e}")
                import traceback
                traceback.print_exc()
    except websockets.exceptions.ConnectionClosed:
        raise  # Let the outer handler deal with reconnection


async def capture_and_send(websocket, stream, playback_event):
    """Capture audio from mic and send to server with VAD filtering."""
    print("Listening for speech...")
    last_audio_time = datetime.now()
    speech_start_time = None
    
    pre_buffer = deque(maxlen=PRE_BUFFER_CHUNKS)
    is_speaking = False
    silence_chunks = 0
    max_silence_chunks = 10
    chunks_sent = 0
    
    while True:
        # If playback is happening, pause mic capture
        if playback_event.is_set():
            await asyncio.sleep(0.05)
            continue
        
        # Read audio with stall detection
        loop = asyncio.get_event_loop()
        try:
            data = await asyncio.wait_for(
                loop.run_in_executor(None, lambda: stream.read(CHUNK_SIZE, exception_on_overflow=False)),
                timeout=AUDIO_STALL_TIMEOUT
            )
            last_audio_time = datetime.now()
        except asyncio.TimeoutError:
            stall_duration = (datetime.now() - last_audio_time).total_seconds()
            raise AudioStallError(f"Audio device stalled for {stall_duration:.1f}s - exiting for restart")
        
        # Resample from capture rate to target rate
        resampled_data = resample_audio(data, CAPTURE_RATE, TARGET_RATE)
        
        # Detect speech using Silero VAD
        try:
            speech_prob = detect_speech(resampled_data, VAD_SAMPLE_RATE)
            is_speech = speech_prob > VAD_THRESHOLD
        except Exception as e:
            print(f"[ERROR] VAD failed: {e}")
            speech_prob = 0.0
            is_speech = False
        
        # Speech detection state machine
        if is_speaking:
            if is_speech:
                silence_chunks = 0
                chunks_sent += 1
                encoded_data = base64.b64encode(resampled_data).decode('utf-8')
                message = {
                    "type": "audio_chunk",
                    "data": encoded_data,
                    "timestamp": datetime.now().isoformat()
                }
                await websocket.send(json.dumps(message))
            else:
                silence_chunks += 1
                if silence_chunks >= max_silence_chunks:
                    duration = (datetime.now() - speech_start_time).total_seconds()
                    print(f"Speech ended: {duration:.1f}s, {chunks_sent} chunks sent")
                    
                    is_speaking = False
                    silence_chunks = 0
                    speech_start_time = None
                    chunks_sent = 0
                    
                    end_message = {
                        "type": "end_speech",
                        "timestamp": datetime.now().isoformat()
                    }
                    await websocket.send(json.dumps(end_message))
                else:
                    chunks_sent += 1
                    encoded_data = base64.b64encode(resampled_data).decode('utf-8')
                    message = {
                        "type": "audio_chunk",
                        "data": encoded_data,
                        "timestamp": datetime.now().isoformat()
                    }
                    await websocket.send(json.dumps(message))
        else:
            if is_speech:
                is_speaking = True
                silence_chunks = 0
                speech_start_time = datetime.now()
                chunks_sent = len(pre_buffer) + 1
                print(f"Speech started (VAD: {speech_prob:.2f})")
                
                for buffered_chunk in pre_buffer:
                    encoded_data = base64.b64encode(buffered_chunk).decode('utf-8')
                    message = {
                        "type": "audio_chunk",
                        "data": encoded_data,
                        "timestamp": datetime.now().isoformat()
                    }
                    await websocket.send(json.dumps(message))
                
                encoded_data = base64.b64encode(resampled_data).decode('utf-8')
                message = {
                    "type": "audio_chunk",
                    "data": encoded_data,
                    "timestamp": datetime.now().isoformat()
                }
                await websocket.send(json.dumps(message))
            else:
                pre_buffer.append(resampled_data)

async def connect_and_stream(p, input_device_index, output_device_index):
    """Connect to server and stream. Retries forever on failure."""
    stream = None
    
    while True:
        try:
            # Open audio input stream
            if stream is None:
                stream = p.open(format=pyaudio.paInt16,
                               channels=CHANNELS,
                               rate=CAPTURE_RATE,
                               input=True,
                               input_device_index=input_device_index,
                               frames_per_buffer=CHUNK_SIZE)
            
            print(f"Connecting to {SERVER_URL}...")
            async with websockets.connect(SERVER_URL) as websocket:
                print("Connected!")
                
                # Event to signal when audio playback is in progress
                playback_event = asyncio.Event()
                
                # Run capture and receive concurrently
                capture_task = asyncio.create_task(
                    capture_and_send(websocket, stream, playback_event)
                )
                receive_task = asyncio.create_task(
                    receive_messages(websocket, p, output_device_index, playback_event)
                )
                
                # Wait for either task to complete (likely due to error/disconnect)
                done, pending = await asyncio.wait(
                    [capture_task, receive_task],
                    return_when=asyncio.FIRST_COMPLETED
                )
                
                # Cancel pending tasks
                for task in pending:
                    task.cancel()
                    try:
                        await task
                    except asyncio.CancelledError:
                        pass
                
                # Re-raise any exceptions from completed tasks
                for task in done:
                    task.result()  # This will raise if the task failed
                
        except AudioStallError as e:
            print(f"\n[FATAL] {e}")
            print("Audio device requires restart. Exiting...")
            sys.exit(1)
        except websockets.exceptions.ConnectionClosed as e:
            print(f"Connection closed: {e}. Reconnecting in {RECONNECT_DELAY}s...")
        except ConnectionRefusedError:
            print(f"Server not available. Retrying in {RECONNECT_DELAY}s...")
        except OSError as e:
            print(f"Network error: {e}. Retrying in {RECONNECT_DELAY}s...")
        except Exception as e:
            print(f"Error: {e}. Retrying in {RECONNECT_DELAY}s...")
            import traceback
            traceback.print_exc()
        
        await asyncio.sleep(RECONNECT_DELAY)

async def main():
    print("[PI] EDDA Client starting...")
    
    # Init audio (this can fail if no mic)
    p, input_device_index = init_audio()
    if p is None:
        print("Failed to initialize audio. Exiting.")
        return
    
    # Find output device for playback
    output_device_index = find_output_device(p)
    
    try:
        await connect_and_stream(p, input_device_index, output_device_index)
    except KeyboardInterrupt:
        print("Interrupted by user.")
    finally:
        p.terminate()

if __name__ == "__main__":
    try:
        asyncio.run(main())
    except KeyboardInterrupt:
        print("\nShutting down.")
