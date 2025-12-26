import asyncio
import websockets
import json
import pyaudio
import base64
import yaml
import sys
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

def calculate_rms(audio_bytes: bytes) -> float:
    """Calculate RMS (root mean square) energy of audio samples."""
    samples = np.frombuffer(audio_bytes, dtype=np.int16).astype(np.float32)
    if len(samples) == 0:
        return 0.0
    return float(np.sqrt(np.mean(samples ** 2)))

def audio_level_bar(rms: float, max_rms: float = 5000, width: int = 30) -> str:
    """Create a visual level bar from RMS value."""
    level = min(rms / max_rms, 1.0)
    filled = int(level * width)
    return '█' * filled + '░' * (width - filled)

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

async def stream_audio(websocket, stream):
    """Stream audio to the connected websocket with VAD-based filtering."""
    print("Streaming audio with Silero VAD...")
    chunk_count = 0
    last_log_time = datetime.now()
    
    # Pre-buffer for capturing speech onset
    pre_buffer = deque(maxlen=PRE_BUFFER_CHUNKS)
    is_speaking = False
    silence_chunks = 0
    max_silence_chunks = 15  # ~450ms of silence to end speech segment
    
    while True:
        data = stream.read(CHUNK_SIZE, exception_on_overflow=False)
        chunk_count += 1
        
        # Calculate audio level for monitoring
        raw_rms = calculate_rms(data)
        
        # Resample from capture rate to target rate (16kHz for Whisper and VAD)
        resampled_data = resample_audio(data, CAPTURE_RATE, TARGET_RATE)
        
        # Detect speech using Silero VAD
        try:
            speech_prob = detect_speech(resampled_data, VAD_SAMPLE_RATE)
            is_speech = speech_prob > VAD_THRESHOLD
        except Exception as e:
            print(f"\n[ERROR] VAD failed: {e}")
            speech_prob = 0.0
            is_speech = False
        
        # Log audio levels periodically (every 100ms for smooth updates)
        now = datetime.now()
        if (now - last_log_time).total_seconds() >= 0.1:
            bar = audio_level_bar(raw_rms)
            speech_indicator = f"🎤 SPEECH ({speech_prob:.2f})" if is_speech else f"        ({speech_prob:.2f})"
            # Overwrite same line with \r
            print(f"\r[AUDIO] {bar} {raw_rms:5.0f} {speech_indicator}", end="", flush=True)
            last_log_time = now
        
        # Speech detection state machine
        if is_speaking:
            # Currently in speech segment
            if is_speech:
                # Continue speech
                silence_chunks = 0
                encoded_data = base64.b64encode(resampled_data).decode('utf-8')
                message = {
                    "type": "audio_chunk",
                    "data": encoded_data,
                    "timestamp": datetime.now().isoformat()
                }
                await websocket.send(json.dumps(message))
            else:
                # Potential end of speech, count silence
                silence_chunks += 1
                if silence_chunks >= max_silence_chunks:
                    # End of speech segment
                    is_speaking = False
                    silence_chunks = 0
                    print()  # Newline after speech segment
                    print("[AUDIO] Speech segment ended")
                else:
                    # Still in grace period, send the chunk
                    encoded_data = base64.b64encode(resampled_data).decode('utf-8')
                    message = {
                        "type": "audio_chunk",
                        "data": encoded_data,
                        "timestamp": datetime.now().isoformat()
                    }
                    await websocket.send(json.dumps(message))
        else:
            # Not currently speaking
            if is_speech:
                # Start of speech! Flush pre-buffer and start streaming
                is_speaking = True
                silence_chunks = 0
                print()  # Newline before new segment
                print("[AUDIO] Speech detected, flushing pre-buffer and streaming...")
                
                # Send pre-buffered chunks
                for buffered_chunk in pre_buffer:
                    encoded_data = base64.b64encode(buffered_chunk).decode('utf-8')
                    message = {
                        "type": "audio_chunk",
                        "data": encoded_data,
                        "timestamp": datetime.now().isoformat()
                    }
                    await websocket.send(json.dumps(message))
                
                # Send current chunk
                encoded_data = base64.b64encode(resampled_data).decode('utf-8')
                message = {
                    "type": "audio_chunk",
                    "data": encoded_data,
                    "timestamp": datetime.now().isoformat()
                }
                await websocket.send(json.dumps(message))
            else:
                # No speech, add to pre-buffer
                pre_buffer.append(resampled_data)

async def connect_and_stream(p, device_index):
    """Connect to server and stream. Retries forever on failure."""
    stream = None
    
    while True:
        try:
            # Open audio stream
            if stream is None:
                stream = p.open(format=pyaudio.paInt16,
                               channels=CHANNELS,
                               rate=CAPTURE_RATE,
                               input=True,
                               input_device_index=device_index,
                               frames_per_buffer=CHUNK_SIZE)
            
            print(f"Connecting to {SERVER_URL}...")
            async with websockets.connect(SERVER_URL) as websocket:
                print("Connected!")
                await stream_audio(websocket, stream)
                
        except websockets.exceptions.ConnectionClosed as e:
            print(f"Connection closed: {e}. Reconnecting in {RECONNECT_DELAY}s...")
        except ConnectionRefusedError:
            print(f"Server not available. Retrying in {RECONNECT_DELAY}s...")
        except OSError as e:
            print(f"Network error: {e}. Retrying in {RECONNECT_DELAY}s...")
        except Exception as e:
            print(f"Error: {e}. Retrying in {RECONNECT_DELAY}s...")
        
        await asyncio.sleep(RECONNECT_DELAY)

async def main():
    print("[PI] EDDA Client starting...")
    
    # Init audio (this can fail if no mic)
    p, device_index = init_audio()
    if p is None:
        print("Failed to initialize audio. Exiting.")
        return
    
    try:
        await connect_and_stream(p, device_index)
    except KeyboardInterrupt:
        print("Interrupted by user.")
    finally:
        p.terminate()

if __name__ == "__main__":
    try:
        asyncio.run(main())
    except KeyboardInterrupt:
        print("\nShutting down.")
