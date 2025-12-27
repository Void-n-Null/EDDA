"""Audio playback handling."""

import io
import subprocess
import wave
from typing import Optional


class AudioPlayer:
    """
    Handles audio playback using ALSA (aplay).
    
    Uses subprocess with stdin piping (no temp files):
    - ALSA's plug plugin handles sample rate conversion
    - Subprocess isolation prevents PyAudio playback crashes from taking down the process
    - No filesystem I/O overhead
    
    Usage:
        player = AudioPlayer()
        player.play_wav(wav_bytes)
    """
    
    def __init__(self, playback_timeout: float = 30.0):
        """
        Initialize the audio player.
        
        Args:
            playback_timeout: Maximum time in seconds to wait for playback to complete
        """
        self.playback_timeout = playback_timeout
    
    def play_wav(self, audio_data: bytes) -> bool:
        """
        Play WAV audio data using aplay via stdin pipe.
        
        Args:
            audio_data: Complete WAV file as bytes (including headers)
            
        Returns:
            True if playback succeeded, False otherwise
        """
        try:
            # Parse WAV to log what we're playing
            wav_info = self._get_wav_info(audio_data)
            if wav_info:
                sample_rate, channels, sample_width = wav_info
                print(f"Playing audio: {sample_rate}Hz, {channels}ch, {sample_width * 8}-bit")
            
            # Pipe WAV data directly to aplay via stdin (no temp file)
            # -t wav: Expect WAV format on stdin
            # -q: Quiet mode (no status output)
            # -: Read from stdin
            proc = subprocess.Popen(
                ['aplay', '-q', '-t', 'wav', '-'],
                stdin=subprocess.PIPE,
                stdout=subprocess.DEVNULL,
                stderr=subprocess.PIPE,
            )
            
            try:
                # Send audio data and wait for completion
                _, stderr = proc.communicate(input=audio_data, timeout=self.playback_timeout)
                
                if proc.returncode != 0:
                    stderr_text = stderr.decode('utf-8', errors='replace') if stderr else ''
                    print(f"[WARN] aplay failed (code {proc.returncode}): {stderr_text}")
                    return False
                else:
                    print("Audio playback complete")
                    return True
                    
            except subprocess.TimeoutExpired:
                proc.kill()
                proc.wait()
                print(f"[ERROR] Playback timed out after {self.playback_timeout}s")
                return False
                
        except FileNotFoundError:
            print("[ERROR] aplay not found - is ALSA installed?")
            return False
        except Exception as e:
            print(f"[ERROR] Failed to play audio: {e}")
            import traceback
            traceback.print_exc()
            return False
    
    def _get_wav_info(self, audio_data: bytes) -> Optional[tuple]:
        """
        Extract WAV file info for logging.
        
        Returns:
            Tuple of (sample_rate, channels, sample_width) or None on error
        """
        try:
            wav_buffer = io.BytesIO(audio_data)
            with wave.open(wav_buffer, 'rb') as wf:
                return (
                    wf.getframerate(),
                    wf.getnchannels(),
                    wf.getsampwidth()
                )
        except Exception:
            return None
