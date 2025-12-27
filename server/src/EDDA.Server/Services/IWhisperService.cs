namespace EDDA.Server.Services;

/// <summary>
/// Service for transcribing audio using Whisper.
/// </summary>
public interface IWhisperService
{
    /// <summary>
    /// Whether the Whisper model is loaded and ready for transcription.
    /// </summary>
    bool IsInitialized { get; }
    
    /// <summary>
    /// Initialize the Whisper model. Called at startup.
    /// </summary>
    Task InitializeAsync();
    
    /// <summary>
    /// Transcribe PCM audio data to text.
    /// </summary>
    /// <param name="pcmAudio">Raw 16-bit mono PCM audio at configured sample rate.</param>
    /// <returns>Transcribed text, or empty string on failure.</returns>
    Task<string> TranscribeAsync(byte[] pcmAudio);
}

