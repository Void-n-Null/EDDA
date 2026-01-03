"""Audio playback handling."""

import io
import queue
import subprocess
import threading
import wave
from typing import Optional, Literal, TYPE_CHECKING

if TYPE_CHECKING:
    from .aec import EchoCanceller


class PlaybackHandle:
    """Handle to a currently playing audio that can be interrupted."""
    
    def __init__(self, proc: subprocess.Popen, thread: threading.Thread):
        self._proc = proc
        self._thread = thread
        self._stopped = False
    
    def stop(self):
        """Stop playback immediately."""
        if self._stopped:
            return
        self._stopped = True
        try:
            self._proc.kill()
            self._proc.wait(timeout=1.0)
        except Exception:
            pass
    
    def wait(self, timeout: Optional[float] = None):
        """Wait for playback to complete."""
        self._thread.join(timeout=timeout)
    
    @property
    def is_playing(self) -> bool:
        """Check if still playing."""
        return self._thread.is_alive()


class AudioPlayer:
    """
    Handles audio playback using ALSA (aplay).
    
    Uses subprocess with stdin piping (no temp files):
    - ALSA's plug plugin handles sample rate conversion
    - Subprocess isolation prevents PyAudio playback crashes from taking down the process
    - No filesystem I/O overhead
    
    When an EchoCanceller is provided, registers all playback audio as the
    reference signal for acoustic echo cancellation.
    
    Usage:
        player = AudioPlayer()
        player.play_wav(wav_bytes)  # Blocking
        
        # Or for interruptible playback:
        handle = player.play_wav_async(wav_bytes)
        # ... later ...
        handle.stop()  # Cut off playback
        
        # With AEC:
        player = AudioPlayer(echo_canceller=aec)
        player.start_stream(...)  # AEC will track reference signal
    """
    
    def __init__(self, playback_timeout: float = 30.0, echo_canceller: Optional["EchoCanceller"] = None):
        """
        Initialize the audio player.
        
        Args:
            playback_timeout: Maximum time in seconds to wait for playback to complete
            echo_canceller: Optional EchoCanceller to register playback audio for AEC
        """
        self.playback_timeout = playback_timeout
        self._echo_canceller = echo_canceller
        self._current_handle: Optional[PlaybackHandle] = None
        self._handle_lock = threading.Lock()

        # Streaming playback state
        self._stream_proc: Optional[subprocess.Popen] = None
        self._stream_thread: Optional[threading.Thread] = None
        self._stream_q: Optional[queue.Queue] = None
        self._stream_stop = threading.Event()
        self._stream_kind: Optional[str] = None  # "loading" | "tts" (or future)
        
        # Track stream sample rate for AEC registration
        self._stream_sample_rate: int = 16000
        self._stream_first_chunk: bool = True  # Track first chunk for AEC
        
        # Volume ducking state
        self._volume_ducked = False
        self._normal_volume: Optional[int] = None
    
    def duck_volume(self, duck_percent: int = 30) -> None:
        """
        Reduce system volume to make it obvious VAD triggered.
        
        Args:
            duck_percent: Volume level to duck to (0-100)
        """
        if self._volume_ducked:
            return
        
        try:
            # Get current volume first
            result = subprocess.run(
                ["amixer", "get", "Master"],
                capture_output=True,
                text=True,
                timeout=1.0
            )
            # Parse current volume from output like "[75%]"
            import re
            match = re.search(r'\[(\d+)%\]', result.stdout)
            if match:
                self._normal_volume = int(match.group(1))
            else:
                self._normal_volume = 80  # Default fallback
            
            # Duck the volume
            subprocess.run(
                ["amixer", "set", "Master", f"{duck_percent}%"],
                capture_output=True,
                timeout=1.0
            )
            self._volume_ducked = True
            print(f"[VOL] 🔉 Ducked: {self._normal_volume}% → {duck_percent}%")
        except Exception as e:
            print(f"[VOL] Warning: Failed to duck volume: {e}")
    
    def restore_volume(self) -> None:
        """Restore volume to normal level after ducking."""
        if not self._volume_ducked:
            return
        
        try:
            volume = self._normal_volume or 80
            subprocess.run(
                ["amixer", "set", "Master", f"{volume}%"],
                capture_output=True,
                timeout=1.0
            )
            self._volume_ducked = False
            print(f"[VOL] 🔊 Restored: {volume}%")
        except Exception as e:
            print(f"[VOL] Warning: Failed to restore volume: {e}")
    
    @property
    def is_volume_ducked(self) -> bool:
        """Check if volume is currently ducked."""
        return self._volume_ducked
    
    def stop_current(self):
        """Stop any currently playing audio."""
        with self._handle_lock:
            self._reap_stream_locked()
            self._stop_stream_locked()
            if self._current_handle is not None:
                self._current_handle.stop()
                self._current_handle = None
            
            # Signal end of playback to AEC
            if self._echo_canceller is not None:
                self._echo_canceller.end_playback()

    def _stop_stream_locked(self):
        """Stop current streaming playback (lock must be held)."""
        if self._stream_proc is None:
            return

        try:
            self._stream_stop.set()
            if self._stream_q is not None:
                try:
                    self._stream_q.put_nowait(None)  # sentinel
                except Exception:
                    pass
            self._stream_proc.kill()
            self._stream_proc.wait(timeout=1.0)
        except Exception:
            pass
        finally:
            self._stream_proc = None
            self._stream_thread = None
            self._stream_q = None
            self._stream_kind = None
            self._stream_stop.clear()

    def _reap_stream_locked(self):
        """Clean up stream state if the writer thread already exited."""
        if self._stream_thread is not None and not self._stream_thread.is_alive():
            self._stream_proc = None
            self._stream_thread = None
            self._stream_q = None
            self._stream_kind = None
            self._stream_stop.clear()

    def wait_stream_done(self, timeout: Optional[float] = None) -> bool:
        """
        Wait until the current stream's aplay process exits.

        This is the only reliable way to know the audio actually finished playing
        (closing stdin just tells aplay no more bytes are coming).
        """
        with self._handle_lock:
            proc = self._stream_proc

        if proc is None:
            return True

        try:
            proc.wait(timeout=timeout)
            return True
        except Exception:
            return False

    def set_echo_canceller(self, echo_canceller: Optional["EchoCanceller"]) -> None:
        """Set or update the echo canceller reference."""
        self._echo_canceller = echo_canceller

    def start_stream(
        self,
        stream_kind: str,
        sample_rate: int,
        channels: int,
        sample_format: Literal["s16le"] = "s16le",
        tempo: float = 1.0,
    ) -> bool:
        """
        Start a raw PCM audio stream.

        This launches ONE persistent `aplay` process (optionally piped through sox for tempo adjustment)
        and feeds PCM chunks to stdin.
        """
        self.stop_current()

        if sample_format != "s16le":
            print(f"[ERROR] Unsupported sample_format: {sample_format}")
            return False

        if sample_rate <= 0 or channels <= 0:
            print(f"[ERROR] Invalid stream format: rate={sample_rate}, channels={channels}")
            return False
        
        # Store sample rate for AEC reference registration
        self._stream_sample_rate = sample_rate
        self._stream_first_chunk = True  # Reset for new stream

        try:
            # If tempo adjustment is needed, pipe through sox -> aplay
            # Otherwise use aplay directly for lowest latency
            if abs(tempo - 1.0) > 0.01:  # Only use sox if tempo is meaningfully different
                # sox: time-stretch without pitch change
                # tempo=0.95 means play at 95% speed (5% slower)
                cmd = f"sox -t raw -r {sample_rate} -c {channels} -e signed-integer -b 16 - -t raw - tempo {tempo:.3f} | aplay -q -t raw -f S16_LE -c {channels} -r {sample_rate} --buffer-size 8192 -"
                proc = subprocess.Popen(
                    cmd,
                    stdin=subprocess.PIPE,
                    stdout=subprocess.DEVNULL,
                    stderr=subprocess.PIPE,
                    shell=True,
                    bufsize=0,
                )
                print(f"Started stream '{stream_kind}': {sample_rate}Hz, {channels}ch, 16-bit PCM @ {tempo:.3f}x tempo (via sox)")
            else:
                # Direct aplay - no sox overhead
                # -t raw: raw PCM
                # -f S16_LE: 16-bit little-endian signed
                # -c/-r: channels/sample rate
                # --buffer-size: use a large buffer to prevent underruns during network jitter
                proc = subprocess.Popen(
                    ["aplay", "-q", "-t", "raw", "-f", "S16_LE", "-c", str(channels), "-r", str(sample_rate), 
                     "--buffer-size", "8192", "-"],
                    stdin=subprocess.PIPE,
                    stdout=subprocess.DEVNULL,
                    stderr=subprocess.PIPE,
                    bufsize=0,
                )
                print(f"Started stream '{stream_kind}': {sample_rate}Hz, {channels}ch, 16-bit PCM")

            q: queue.Queue = queue.Queue(maxsize=128)  # backpressure
            stop_evt = self._stream_stop
            stop_evt.clear()

            def _writer():
                try:
                    while not stop_evt.is_set():
                        item = q.get()
                        if item is None:
                            break
                        if not item:
                            continue
                        try:
                            if proc.stdin is None:
                                break
                            proc.stdin.write(item)
                        except BrokenPipeError:
                            break
                        except Exception:
                            break
                finally:
                    try:
                        if proc.stdin:
                            proc.stdin.close()
                    except Exception:
                        pass
                    try:
                        proc.wait(timeout=1.0)
                    except Exception:
                        pass

            t = threading.Thread(target=_writer, daemon=True)
            t.start()

            with self._handle_lock:
                self._stream_proc = proc
                self._stream_thread = t
                self._stream_q = q
                self._stream_kind = stream_kind

            return True

        except FileNotFoundError:
            print("[ERROR] aplay not found - is ALSA installed?")
            return False
        except Exception as e:
            print(f"[ERROR] Failed to start stream: {e}")
            return False

    def write_stream(self, pcm_chunk: bytes) -> bool:
        """Write a chunk of PCM to the active stream."""
        with self._handle_lock:
            if self._stream_proc is None or self._stream_q is None:
                return False
            try:
                # Register with AEC as reference signal (what we're playing)
                if self._echo_canceller is not None and pcm_chunk:
                    # On first chunk: capture start position and start timing
                    self._echo_canceller.register_playback(
                        pcm_chunk, 
                        self._stream_sample_rate,
                        auto_start=True,
                        is_first_chunk=self._stream_first_chunk
                    )
                    self._stream_first_chunk = False
                
                # Avoid blocking forever; if we're backed up hard, audio is already doomed.
                self._stream_q.put(pcm_chunk, timeout=0.25)
                return True
            except Exception:
                return False

    def end_stream(self):
        """Gracefully end the active stream."""
        with self._handle_lock:
            if self._stream_q is not None:
                try:
                    self._stream_q.put_nowait(None)  # sentinel
                except Exception:
                    pass
            # Important: DO NOT clear _stream_proc here.
            # We still need to be able to hard-stop it if a new stream starts
            # (e.g., loading stream drains while TTS stream begins).
            self._reap_stream_locked()
            
            # Signal end of playback to AEC
            if self._echo_canceller is not None:
                self._echo_canceller.end_playback()
    
    def play_wav_async(self, audio_data: bytes) -> Optional[PlaybackHandle]:
        """
        Start playing WAV audio data asynchronously.
        
        Returns a handle that can be used to stop playback.
        Automatically stops any currently playing audio first.
        
        Args:
            audio_data: Complete WAV file as bytes (including headers)
            
        Returns:
            PlaybackHandle to control playback, or None on error
        """
        # Stop any current playback first
        self.stop_current()
        
        try:
            # Parse WAV to log what we're playing
            wav_info = self._get_wav_info(audio_data)
            if wav_info:
                sample_rate, channels, sample_width = wav_info
                print(f"Playing audio (async): {sample_rate}Hz, {channels}ch, {sample_width * 8}-bit")
            
            # Register PCM with AEC BEFORE playback starts (but don't start timing yet)
            self._register_wav_with_aec(audio_data, start_timing=False)
            
            # Start aplay subprocess
            proc = subprocess.Popen(
                ['aplay', '-q', '-t', 'wav', '-'],
                stdin=subprocess.PIPE,
                stdout=subprocess.DEVNULL,
                stderr=subprocess.PIPE,
            )
            
            # NOW start AEC timing - audio is about to play
            if self._echo_canceller is not None:
                self._echo_canceller.start_playback()
            
            # Capture echo_canceller reference for the thread
            echo_canceller = self._echo_canceller
            
            def _play_thread():
                try:
                    proc.communicate(input=audio_data, timeout=self.playback_timeout)
                except subprocess.TimeoutExpired:
                    proc.kill()
                    proc.wait()
                except Exception:
                    pass
                finally:
                    # Signal end of playback to AEC
                    if echo_canceller is not None:
                        echo_canceller.end_playback()
            
            thread = threading.Thread(target=_play_thread, daemon=True)
            thread.start()
            
            handle = PlaybackHandle(proc, thread)
            
            with self._handle_lock:
                self._current_handle = handle
            
            return handle
            
        except FileNotFoundError:
            print("[ERROR] aplay not found - is ALSA installed?")
            return None
        except Exception as e:
            print(f"[ERROR] Failed to start audio playback: {e}")
            return None
    
    def _register_wav_with_aec(self, audio_data: bytes, start_timing: bool = False) -> None:
        """
        Extract PCM from WAV and register with AEC as reference signal.
        
        Args:
            audio_data: WAV file bytes
            start_timing: If True, start the playback timer (call when audio starts playing)
        """
        if self._echo_canceller is None:
            return
        
        try:
            wav_buffer = io.BytesIO(audio_data)
            with wave.open(wav_buffer, 'rb') as wf:
                sample_rate = wf.getframerate()
                channels = wf.getnchannels()
                
                # Read all frames as raw PCM
                pcm_data = wf.readframes(wf.getnframes())
                
                # Convert stereo to mono if needed (average channels)
                if channels == 2:
                    import numpy as np
                    samples = np.frombuffer(pcm_data, dtype=np.int16)
                    # Reshape to (n_samples, 2) and average
                    samples = samples.reshape(-1, 2).mean(axis=1).astype(np.int16)
                    pcm_data = samples.tobytes()
                
                # Capture start position BEFORE writing the audio
                self._echo_canceller.begin_playback_registration()
                
                # Register with AEC (don't auto-start timing yet)
                self._echo_canceller.register_playback(pcm_data, sample_rate)
                
                if start_timing:
                    self._echo_canceller.start_playback()
                
        except Exception as e:
            # Don't fail playback if AEC registration fails
            print(f"[AEC] Warning: Failed to register WAV with AEC: {e}")

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
            
            # Register PCM with AEC BEFORE playback starts (but don't start timing yet)
            self._register_wav_with_aec(audio_data, start_timing=False)
            
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
            
            # NOW start AEC timing - audio is about to play
            if self._echo_canceller is not None:
                self._echo_canceller.start_playback()
            
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
            finally:
                # Signal end of this playback to AEC
                if self._echo_canceller is not None:
                    self._echo_canceller.end_playback()
                
        except FileNotFoundError:
            print("[ERROR] aplay not found - is ALSA installed?")
            return False
        except Exception as e:
            print(f"[ERROR] Failed to play audio: {e}")
            import traceback
            traceback.print_exc()
            return False
    
    def play_wav_sentence(self, wav_bytes: bytes, sentence_index: int, total_sentences: int, 
                          duration_ms: int, sample_rate: int, tempo_applied: float) -> bool:
        """
        Play a complete WAV sentence with detailed metadata logging.
        Blocks until playback finishes.
        
        Args:
            wav_bytes: Complete WAV file as bytes
            sentence_index: Index of this sentence (1-based)
            total_sentences: Total number of sentences in response
            duration_ms: Expected duration in milliseconds
            sample_rate: Audio sample rate (Hz)
            tempo_applied: Tempo multiplier that was applied (1.0 = normal)
            
        Returns:
            True if playback succeeded, False otherwise
        """
        print(f"[PLAY] Sentence {sentence_index}/{total_sentences}: "
              f"{duration_ms}ms @ {sample_rate}Hz "
              f"(tempo={tempo_applied:.3f}x, size={len(wav_bytes)}B)")
        
        # Use the existing play_wav method which handles blocking playback
        return self.play_wav(wav_bytes)
    
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
