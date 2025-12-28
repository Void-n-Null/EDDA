"""Speech detection and processing modules."""

from .detector import SpeechDetector, SpeechEvent, SpeechConfig
from .pipeline import InputPipeline

__all__ = ["SpeechDetector", "SpeechEvent", "SpeechConfig", "InputPipeline"]
