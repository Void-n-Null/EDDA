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

async def stream_audio(websocket, stream):
    """Stream audio to the connected websocket."""
    print("Streaming audio...")
    chunk_count = 0
    last_log_time = datetime.now()
    
    while True:
        data = stream.read(CHUNK_SIZE, exception_on_overflow=False)
        chunk_count += 1
        
        # Calculate audio level for monitoring
        raw_rms = calculate_rms(data)
        
        # Resample from capture rate to target rate (16kHz for Whisper)
        resampled_data = resample_audio(data, CAPTURE_RATE, TARGET_RATE)
        
        # Log audio levels periodically (every 100ms for smooth updates)
        now = datetime.now()
        if (now - last_log_time).total_seconds() >= 0.1:
            bar = audio_level_bar(raw_rms)
            is_loud = "🎤 SPEECH" if raw_rms > 500 else "        "
            # Overwrite same line with \r
            print(f"\r[AUDIO] {bar} {raw_rms:5.0f} {is_loud}", end="", flush=True)
            last_log_time = now
        
        # Encode to base64 for JSON
        encoded_data = base64.b64encode(resampled_data).decode('utf-8')
        message = {
            "type": "audio_chunk",
            "data": encoded_data,
            "timestamp": datetime.now().isoformat()
        }
        await websocket.send(json.dumps(message))

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
