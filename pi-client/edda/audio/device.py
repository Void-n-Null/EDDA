"""Audio device enumeration and stream management."""

from dataclasses import dataclass
from typing import Optional, Any
import pyaudio


@dataclass
class AudioConfig:
    """Audio capture/playback configuration."""
    capture_rate: int = 48000
    target_rate: int = 16000
    chunk_size: int = 1440
    channels: int = 1
    input_device_name: str = "default"  # "default" uses system default, otherwise substring match
    echo_cancellation: bool = True  # If True, use EC + elevated threshold during playback
    
    # VAD thresholds - elevated threshold during playback helps filter residual echo
    vad_threshold_normal: float = 0.5  # Normal threshold when not playing
    vad_threshold_playback: float = 0.99  # Very high threshold during playback (AEC not effective)


class AudioStallError(Exception):
    """Raised when audio device stops providing data."""
    pass


class AudioDevice:
    """
    Manages PyAudio device enumeration and stream lifecycle.
    
    Usage:
        device = AudioDevice(config)
        if not device.initialize():
            print("No audio device found")
            return
        
        stream = device.open_input_stream()
        # ... use stream ...
        device.close()
    """
    
    def __init__(self, config: AudioConfig):
        self.config = config
        self._pyaudio: Optional[pyaudio.PyAudio] = None
        self._input_device_index: Optional[int] = None
        self._output_device_index: Optional[int] = None
        self._stream: Any = None  # pyaudio.Stream (not exposed as a type)
    
    @property
    def pyaudio(self) -> Optional[pyaudio.PyAudio]:
        """Get the PyAudio instance."""
        return self._pyaudio
    
    @property
    def input_device_index(self) -> Optional[int]:
        """Get the input device index."""
        return self._input_device_index
    
    @property
    def output_device_index(self) -> Optional[int]:
        """Get the output device index."""
        return self._output_device_index
    
    def initialize(self) -> bool:
        """
        Initialize PyAudio and find input/output devices.
        Returns True if successful, False if required devices not found.
        """
        self._pyaudio = pyaudio.PyAudio()
        
        # Find input device
        self._input_device_index = self._find_input_device()
        if self._input_device_index is None:
            self._pyaudio.terminate()
            self._pyaudio = None
            return False
        
        # Find output device
        self._output_device_index = self._find_output_device()
        
        return True
    
    def _find_input_device(self) -> Optional[int]:
        """
        Find the configured input device.
        
        Special values:
        - "default": Use system default input device (recommended for PipeWire/echo cancellation)
        - "pulse": Explicitly use PulseAudio/PipeWire
        - Any other string: Substring match against device names
        """
        if self._pyaudio is None:
            return None
        
        # Handle special device names
        if self.config.input_device_name.lower() in ("default", "pulse"):
            # Find the device named "default" or "pulse" for PipeWire routing
            for i in range(self._pyaudio.get_device_count()):
                dev = self._pyaudio.get_device_info_by_index(i)
                if dev['name'].lower() == self.config.input_device_name.lower():
                    if dev['maxInputChannels'] > 0:
                        dev_info = dev
                        print(f"Using input device {i}: {dev_info['name']} (PipeWire)")
                        print(f"  Native rate: {dev_info['defaultSampleRate']} Hz")
                        print(f"  Capture at: {self.config.capture_rate} Hz -> Resample to: {self.config.target_rate} Hz")
                        if self.config.echo_cancellation:
                            print(f"  Echo cancellation: enabled (via PipeWire)")
                        return i
            
            # Fallback: try to get system default
            try:
                default = self._pyaudio.get_default_input_device_info()
                print(f"Using system default input device: {default['name']}")
                print(f"  Native rate: {default['defaultSampleRate']} Hz")
                print(f"  Capture at: {self.config.capture_rate} Hz -> Resample to: {self.config.target_rate} Hz")
                if self.config.echo_cancellation:
                    print(f"  Echo cancellation: enabled (via PipeWire)")
                return default['index']
            except IOError:
                print("Error: No default input device available")
                return None
        
        # Standard substring match for explicit device selection
        device_index = None
        for i in range(self._pyaudio.get_device_count()):
            dev = self._pyaudio.get_device_info_by_index(i)
            if self.config.input_device_name in dev['name']:
                device_index = i
                break
        
        if device_index is None:
            print(f"Error: {self.config.input_device_name} not found!")
            print("Available input devices:")
            for i in range(self._pyaudio.get_device_count()):
                dev = self._pyaudio.get_device_info_by_index(i)
                if dev['maxInputChannels'] > 0:
                    print(f"  [{i}] {dev['name']} (rate: {dev['defaultSampleRate']})")
            return None
        
        dev_info = self._pyaudio.get_device_info_by_index(device_index)
        print(f"Using input device {device_index}: {dev_info['name']}")
        print(f"  Native rate: {dev_info['defaultSampleRate']} Hz")
        print(f"  Capture at: {self.config.capture_rate} Hz -> Resample to: {self.config.target_rate} Hz")
        
        return device_index
    
    def _find_output_device(self) -> Optional[int]:
        """Find the default output device."""
        if self._pyaudio is None:
            return None
        
        try:
            default_output = self._pyaudio.get_default_output_device_info()
            print(f"Using output device: {default_output['name']}")
            return default_output['index']
        except IOError:
            print("Warning: No default output device found")
            return None
    
    def open_input_stream(self) -> Any:
        """Open an input stream for audio capture."""
        if self._pyaudio is None or self._input_device_index is None:
            return None
        
        self._stream = self._pyaudio.open(
            format=pyaudio.paInt16,
            channels=self.config.channels,
            rate=self.config.capture_rate,
            input=True,
            input_device_index=self._input_device_index,
            frames_per_buffer=self.config.chunk_size
        )
        return self._stream
    
    def close_stream(self):
        """Close the current input stream if open."""
        if self._stream is not None:
            try:
                self._stream.stop_stream()
                self._stream.close()
            except Exception:
                pass
            self._stream = None
    
    def close(self):
        """Clean up all resources."""
        self.close_stream()
        if self._pyaudio is not None:
            self._pyaudio.terminate()
            self._pyaudio = None
