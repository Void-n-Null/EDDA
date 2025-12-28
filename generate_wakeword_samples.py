#!/usr/bin/env python3
"""
Generate wake word training samples using Chatterbox TTS.

Creates thousands of variations with different voice parameters,
phrase variations, and silence padding to provide diverse training
data for OpenWakeWord.
"""

import json
import os
import sys
import time
import random
from pathlib import Path

import requests
import numpy as np
from scipy.io import wavfile


def add_silence_padding(audio_data: np.ndarray, sample_rate: int = 24000) -> np.ndarray:
    """Add random silence padding before and after the audio."""
    # Random silence: 0-500ms before, 0-500ms after
    silence_before = random.randint(0, int(0.5 * sample_rate))
    silence_after = random.randint(0, int(0.5 * sample_rate))
    
    silence_before_arr = np.zeros(silence_before, dtype=audio_data.dtype)
    silence_after_arr = np.zeros(silence_after, dtype=audio_data.dtype)
    
    return np.concatenate([silence_before_arr, audio_data, silence_after_arr])


def generate_samples(
    phrases: list[str],
    output_dir: Path,
    num_samples: int = 10000,
    tts_url: str = "http://localhost:5000/tts",
):
    """Generate TTS samples with varying parameters and phrase variations."""
    
    # Create output directory
    output_dir.mkdir(parents=True, exist_ok=True)
    print(f"Generating {num_samples} samples across {len(phrases)} phrase variations")
    print(f"Output: {output_dir}/")
    print()
    
    # Check TTS service health
    try:
        health = requests.get(f"http://localhost:5000/health", timeout=5)
        health.raise_for_status()
        health_data = health.json()
        if not health_data.get("model_loaded"):
            print(f"ERROR: TTS model not loaded. Health: {health_data}")
            return False
        print(f"✓ TTS service healthy (device: {health_data.get('device')})")
    except Exception as e:
        print(f"ERROR: Cannot connect to TTS service at {tts_url}: {e}")
        print("Make sure Docker container is running: docker-compose up tts")
        return False
    
    success_count = 0
    fail_count = 0
    total_time = 0
    start_overall = time.time()
    
    # Distribute samples evenly across phrases
    samples_per_phrase = num_samples // len(phrases)
    
    for phrase_idx, phrase in enumerate(phrases):
        print(f"\n--- Generating '{phrase}' (phrase {phrase_idx+1}/{len(phrases)}) ---")
        
        for i in range(1, samples_per_phrase + 1):
            global_idx = phrase_idx * samples_per_phrase + i
            
            # Aggressive parameter variation for diversity
            # Exaggeration: Full range 0.0 to 1.0
            exaggeration = random.uniform(0.0, 1.0)
            
            # CFG weight: 0.2 to 0.8 range
            cfg_weight = random.uniform(0.2, 0.8)
            
            request_data = {
                "text": phrase,
                "exaggeration": round(exaggeration, 2),
                "cfg_weight": round(cfg_weight, 2),
            }
            
            try:
                start = time.perf_counter()
                response = requests.post(
                    tts_url,
                    json=request_data,
                    timeout=30,
                    stream=True,
                )
                response.raise_for_status()
                elapsed = time.perf_counter() - start
                total_time += elapsed
                
                # Read WAV data and add silence padding
                wav_bytes = response.content
                
                # Parse WAV to add silence padding
                import io
                wav_io = io.BytesIO(wav_bytes)
                sample_rate, audio_data = wavfile.read(wav_io)
                
                # Add random silence padding
                padded_audio = add_silence_padding(audio_data, sample_rate)
                
                # Save with padding
                output_path = output_dir / f"sample_{global_idx:05d}.wav"
                wavfile.write(output_path, sample_rate, padded_audio)
                
                success_count += 1
                
                # Progress reporting (every 100 samples)
                if i % 100 == 0 or i == 1:
                    elapsed_min = (time.time() - start_overall) / 60
                    rate = success_count / elapsed_min if elapsed_min > 0 else 0
                    eta_min = (num_samples - global_idx) / rate if rate > 0 else 0
                    print(
                        f"  [{global_idx:5d}/{num_samples}] "
                        f"exag={exaggeration:.2f}, cfg={cfg_weight:.2f} | "
                        f"{rate:.0f} samples/min | ETA: {eta_min:.1f}min"
                    )
                
            except Exception as e:
                print(f"  [{global_idx:5d}/{num_samples}] FAILED: {e}")
                fail_count += 1
                continue
            
            # Very short delay to avoid hammering (but keep it fast)
            time.sleep(0.05)
    
    total_elapsed = time.time() - start_overall
    print()
    print("=" * 60)
    print(f"Generation complete!")
    print(f"  Success: {success_count}/{num_samples} ({success_count/num_samples*100:.1f}%)")
    print(f"  Failed: {fail_count}/{num_samples}")
    print(f"  Total time: {total_elapsed/60:.1f} minutes")
    print(f"  Avg time per sample: {total_elapsed/num_samples:.2f}s")
    print(f"  Rate: {success_count/(total_elapsed/60):.0f} samples/min")
    
    # Calculate total size
    total_size_mb = sum(f.stat().st_size for f in output_dir.glob("*.wav")) / (1024 * 1024)
    print(f"  Total size: {total_size_mb:.1f} MB")
    print(f"  Output directory: {output_dir.absolute()}")
    print("=" * 60)
    
    return success_count > 0


def main():
    # Configuration
    num_samples = 10000
    output_dir = Path(__file__).parent / "wakeword_examples"
    
    # Multiple phrase variations for robustness
    # OpenWakeWord will train a binary detector that activates on any of these
    phrases = [
        "nixie",
        "hey nixie",
        "okay nixie",
    ]
    
    print("=" * 60)
    print("OpenWakeWord Training Data Generator")
    print("=" * 60)
    print(f"Target samples: {num_samples}")
    print(f"Phrase variations: {phrases}")
    print(f"Estimated time: ~{num_samples * 0.2 / 60:.0f} minutes")
    print("=" * 60)
    print()
    
    # Generate samples
    success = generate_samples(phrases, output_dir, num_samples)
    
    sys.exit(0 if success else 1)


if __name__ == "__main__":
    main()
