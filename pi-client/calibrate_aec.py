#!/usr/bin/env python3
"""
AEC Delay Calibration Script - SIMPLE VERSION

Plays individual beeps one at a time with clear logging.
"""

import subprocess
import time
import wave
import io
import tempfile
import os
import numpy as np
from scipy import signal

# Configuration
SAMPLE_RATE = 44100
NUM_BEEPS = 8
BEEP_DURATION_S = 0.15  # 150ms beep
BEEP_FREQ = 1000  # 1kHz
PAUSE_BETWEEN = 1.5  # 1.5 seconds between beeps


def generate_beep_wav() -> bytes:
    """Generate a single beep as WAV bytes."""
    t = np.linspace(0, BEEP_DURATION_S, int(SAMPLE_RATE * BEEP_DURATION_S))
    beep = np.sin(2 * np.pi * BEEP_FREQ * t)
    
    # Long fade in (50ms) to avoid power spike, short fade out (10ms)
    fade_in = int(SAMPLE_RATE * 0.05)  # 50ms fade in
    fade_out = int(SAMPLE_RATE * 0.01)  # 10ms fade out
    if fade_in > 0:
        beep[:fade_in] *= np.linspace(0, 1, fade_in)
    if fade_out > 0:
        beep[-fade_out:] *= np.linspace(1, 0, fade_out)
    
    # 30% amplitude - gentle on the USB power
    audio = (beep * 32767 * 0.3).astype(np.int16)
    
    # Create WAV
    buf = io.BytesIO()
    with wave.open(buf, 'wb') as wf:
        wf.setnchannels(1)
        wf.setsampwidth(2)
        wf.setframerate(SAMPLE_RATE)
        wf.writeframes(audio.tobytes())
    
    return buf.getvalue()


def play_beep(beep_wav: bytes, beep_num: int) -> float:
    """Play a single beep and return when it started."""
    print(f"  [{beep_num}/{NUM_BEEPS}] Playing beep...", end=" ", flush=True)
    
    start_time = time.monotonic()
    
    # Use default ALSA (with larger buffer to reduce power spikes)
    proc = subprocess.run(
        ["aplay", "-q", "--buffer-size=16384", "-"],
        input=beep_wav,
        capture_output=True,
        timeout=5.0
    )
    
    end_time = time.monotonic()
    duration = (end_time - start_time) * 1000
    
    if proc.returncode == 0:
        print(f"✓ ({duration:.0f}ms)")
    else:
        print(f"✗ FAILED: {proc.stderr.decode()}")
    
    return start_time


def record_audio(duration: float) -> np.ndarray:
    """Record audio for the specified duration."""
    print(f"\nRecording {duration:.1f}s of audio...")
    
    with tempfile.NamedTemporaryFile(suffix='.wav', delete=False) as f:
        record_path = f.name
    
    try:
        proc = subprocess.run(
            ["arecord", "-q", "-f", "S16_LE", "-r", "16000", "-c", "1", 
             "-d", str(int(duration) + 1), record_path],
            capture_output=True,
            timeout=duration + 10
        )
        
        if proc.returncode != 0:
            print(f"Recording failed: {proc.stderr.decode()}")
            return np.array([], dtype=np.int16)
        
        with wave.open(record_path, 'rb') as wf:
            audio = np.frombuffer(wf.readframes(wf.getnframes()), dtype=np.int16)
        
        print(f"Recorded {len(audio)} samples ({len(audio)/16000:.2f}s)")
        return audio
        
    finally:
        try:
            os.unlink(record_path)
        except:
            pass


def main():
    print("=" * 60)
    print("AEC Delay Calibration - Simple Version")
    print("=" * 60)
    print(f"\nWill play {NUM_BEEPS} beeps with {PAUSE_BETWEEN}s pause between each.")
    print("Watch for the beeps and your mic light.\n")
    
    input("Press Enter to start...")
    
    # Generate beep
    beep_wav = generate_beep_wav()
    print(f"\nGenerated beep: {len(beep_wav)} bytes, {BEEP_DURATION_S*1000:.0f}ms @ {BEEP_FREQ}Hz")
    
    # Start recording in background
    total_duration = NUM_BEEPS * PAUSE_BETWEEN + 2
    
    print(f"\n--- Starting {NUM_BEEPS} beeps ---")
    
    beep_times = []
    for i in range(NUM_BEEPS):
        t = play_beep(beep_wav, i + 1)
        beep_times.append(t)
        
        if i < NUM_BEEPS - 1:
            time.sleep(PAUSE_BETWEEN)
    
    print("\n--- All beeps complete ---")
    
    # Multiple delay measurements
    print("\n" + "=" * 60)
    print("Now measuring delay (5 trials)...")
    print("=" * 60)
    
    input("\nPress Enter to start measurement trials...")
    
    # Generate reference beep at 16kHz for correlation
    t = np.linspace(0, BEEP_DURATION_S, int(16000 * BEEP_DURATION_S))
    ref_beep = np.sin(2 * np.pi * BEEP_FREQ * t)
    fade = int(16000 * 0.01)
    ref_beep[:fade] *= np.linspace(0, 1, fade)
    ref_beep[-fade:] *= np.linspace(1, 0, fade)
    ref_beep = (ref_beep * 32767).astype(np.int16)
    
    delays = []
    
    for trial in range(5):
        print(f"\n--- Trial {trial + 1}/5 ---")
        
        with tempfile.NamedTemporaryFile(suffix='.wav', delete=False) as f:
            record_path = f.name
        
        try:
            # Start recording
            record_proc = subprocess.Popen(
                ["arecord", "-q", "-f", "S16_LE", "-r", "16000", "-c", "1", 
                 "-d", "3", record_path],
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL
            )
            
            # Wait then play beep
            time.sleep(0.5)
            expected_beep_sample = 8000  # 0.5 seconds into recording
            
            print("  Playing beep...", end=" ", flush=True)
            subprocess.run(["aplay", "-q", "--buffer-size=16384", "-"], input=beep_wav, capture_output=True)
            
            # Wait for recording to finish
            record_proc.wait(timeout=10)
            
            # Analyze
            with wave.open(record_path, 'rb') as wf:
                recorded = np.frombuffer(wf.readframes(wf.getnframes()), dtype=np.int16)
            
            if np.max(np.abs(recorded)) < 500:
                print("⚠️  Low level, skipping")
                continue
            
            # Cross-correlate to find beep
            correlation = signal.correlate(recorded.astype(np.float64), ref_beep.astype(np.float64), mode='valid')
            peak_idx = np.argmax(np.abs(correlation))
            
            # Delay from expected position
            delay_samples = peak_idx - expected_beep_sample
            delay_ms = delay_samples * 1000 / 16000
            
            # Confidence check
            peak_val = np.abs(correlation[peak_idx])
            mean_val = np.mean(np.abs(correlation))
            confidence = peak_val / mean_val
            
            if confidence > 3 and abs(delay_ms) < 500:
                delays.append(delay_ms)
                print(f"✓ {delay_ms:.1f}ms (confidence: {confidence:.1f})")
            else:
                print(f"⚠️  Bad reading: {delay_ms:.1f}ms, confidence {confidence:.1f}")
            
            time.sleep(1.0)  # Pause between trials
            
        finally:
            try:
                os.unlink(record_path)
            except:
                pass
    
    # Results
    print("\n" + "=" * 60)
    print("RESULTS")
    print("=" * 60)
    
    if len(delays) < 3:
        print(f"\n⚠️  Only {len(delays)} valid measurements - need at least 3")
        return
    
    avg_delay = np.mean(delays)
    std_delay = np.std(delays)
    min_delay = np.min(delays)
    max_delay = np.max(delays)
    
    print(f"\nValid measurements: {len(delays)}")
    print(f"  Min:    {min_delay:.1f}ms")
    print(f"  Max:    {max_delay:.1f}ms")
    print(f"  Avg:    {avg_delay:.1f}ms")
    print(f"  StdDev: {std_delay:.1f}ms")
    
    if std_delay > 30:
        print(f"\n⚠️  High variance ({std_delay:.1f}ms) - measurements may be unreliable")
    
    recommended = int(round(avg_delay))
    print(f"\n✅ Recommended speaker_to_mic_delay_ms: {recommended}")
    print(f"\nUpdate config.yaml:")
    print(f"  speaker_to_mic_delay_ms: {recommended}")


if __name__ == "__main__":
    main()
