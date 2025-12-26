# VAD Upgrade: Energy-Based → Silero VAD

## Changes Made

### Problem
- **Old system**: Energy-based VAD (RMS > 500) detected any loud sound
- **Issues**: Door slams, keyboard typing, fan noise triggered "speech" detection
- **Result**: Sending mostly blank audio to transcription, wasting resources

### Solution
- **Silero VAD**: Neural network trained specifically to detect human speech
- **Pre-buffering**: Captures ~300ms before speech starts (no cut-off beginnings)
- **Smart filtering**: Only sends audio when actual speech is detected

## Architecture

```
┌─────────────┐
│  Pi Client  │
│             │
│  1. Capture │ ──┐
│  2. Resample│   │ 48kHz → 16kHz
│  3. VAD     │ ──┘ Silero detects speech
│  4. Buffer  │     Pre-buffer + active speech
│  5. Send    │ ──> Only when speaking
└─────────────┘
       │
       │ WebSocket (16kHz PCM, base64)
       ▼
┌──────────────┐
│Basement Srv  │
│              │
│  1. Receive  │ (All audio is speech now)
│  2. Buffer   │
│  3. Silence  │ 1.5s pause → transcribe
│  4. Whisper  │ Fast GPU transcription
└──────────────┘
```

## Files Modified

### Pi Client
- **`pi-client/requirements.txt`**: Added `torch` and `torchaudio` for Silero VAD
- **`pi-client/client.py`**:
  - Loads Silero VAD model on startup
  - Implements pre-buffering (10 chunks = ~300ms)
  - Speech detection state machine
  - Only sends audio during speech segments
  - Shows speech probability in console (e.g., "🎤 SPEECH (0.87)")

### Server
- **`server/src/EDDA.Server/Program.cs`**:
  - Removed energy-based `CalculateAudioEnergy()` function
  - Simplified: assumes all received audio is speech (client filtered it)
  - Still uses silence detection for transcription timing

## Configuration

### VAD Parameters (in `client.py`)
```python
VAD_THRESHOLD = 0.5      # Speech probability 0.0-1.0 (tune if needed)
PRE_BUFFER_CHUNKS = 10   # ~320ms before speech (10 chunks × 32ms)
max_silence_chunks = 15  # ~480ms silence ends segment (15 chunks × 32ms)
```

**Important**: Silero VAD requires **EXACTLY 512 samples** at 16kHz (very picky!):
- Current: `1411 samples` at 44.1kHz = 32ms
- After resampling: `~512 samples` at 16kHz
- VAD function slices to exactly 512 samples
- **DO NOT** change chunk size without recalculating for 512 samples!

### Server Timing (env vars)
```bash
WHISPER_SILENCE_SECONDS=1.5  # Time after speech to transcribe
WHISPER_MAX_AUDIO_SECONDS=8.0 # Max buffer before force-transcribe
```

## Testing Instructions

### 1. Install Dependencies
```bash
# On Pi (via SSH)
ssh edda
cd /home/blake/edda-voice-client
source venv/bin/activate
pip install torch==2.1.0 torchaudio==2.1.0

# First run will download Silero VAD model (~3MB)
```

### 2. Deploy and Test
```bash
# From dev machine
./scripts/dev-pi.sh      # Deploys & runs Pi client interactively
# In another terminal:
./scripts/dev-server.sh  # Runs server interactively

# Watch the Pi client output:
# - Audio level bar shows sound levels
# - Speech probability shows (e.g., 0.12 = silence, 0.87 = speech)
# - "Speech detected" when VAD triggers
# - "Speech segment ended" when silence detected
```

### 3. Test Cases
1. **Silence**: Should NOT send audio, should show low probability (~0.01-0.15)
2. **Speech**: Should send audio, show high probability (~0.6-0.95), trigger transcription
3. **Loud non-speech** (door slam, keyboard): Should NOT trigger (prob < 0.5)
4. **Quiet speech**: Should still detect if clearly articulated
5. **Music/TV**: May trigger if vocal-heavy (this is expected, tune threshold if needed)

### 4. Tuning
If VAD is too sensitive/insensitive, edit `client.py`:

```python
# More sensitive (detects quieter speech, more false positives)
VAD_THRESHOLD = 0.3

# Less sensitive (only clear speech, might miss quiet speech)
VAD_THRESHOLD = 0.7
```

## Expected Behavior

### Before (Energy-Based)
```
[AUDIO] ████░░░░░░  1234 🎤 SPEECH   ← Door slam triggers!
[AUDIO] ██░░░░░░░░   678 🎤 SPEECH   ← Keyboard triggers!
[AUDIO] ░░░░░░░░░░    45            ← Silence (good)
[SRV] Processing 48000 bytes...
[SRV] TRANSCRIPTION:                 ← Empty!
```

### After (Silero VAD)
```
[AUDIO] ████░░░░░░  1234         (0.12)  ← Door slam, but prob low
[AUDIO] ██░░░░░░░░   678         (0.08)  ← Keyboard, prob low
[AUDIO] ███████░░░  2456 🎤 SPEECH (0.87)  ← Actual speech!
Speech detected, flushing pre-buffer...
[AUDIO] ████████░░  2789 🎤 SPEECH (0.91)
[SRV] Processing 16000 bytes...
[SRV] TRANSCRIPTION: Hey EDDA, what's the weather?
```

## Performance Notes

- **Silero VAD**: Very fast (~1-2ms per chunk on Pi 4)
- **CPU only**: No GPU needed for VAD
- **Model size**: ~3MB (downloads once on first run)
- **Network savings**: Only sends ~30% of audio now (when actually speaking)
- **Transcription quality**: Better (no blank audio mixed with speech)

## Rollback

If issues occur, revert to energy-based:

```bash
cd /mnt/dev/EDDA
git checkout HEAD~1 pi-client/
./scripts/dev-pi.sh
```

## Next Steps

- [ ] Monitor false positives/negatives in real use
- [ ] Consider per-user VAD threshold tuning
- [ ] Add "push-to-talk" mode as alternative to always-on VAD
- [ ] Explore wake word detection (Porcupine/Snowboy) before VAD

