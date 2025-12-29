namespace EDDA.Server.Services;

/// <summary>
/// Service for detecting wake word attempts in transcriptions.
/// Uses LLM to classify whether a transcription sounds like the target wake word.
/// </summary>
public interface IWakeWordService
{
    /// <summary>
    /// Check if the transcription sounds like an attempt to say the wake word.
    /// </summary>
    /// <param name="transcription">The transcribed text to check.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the transcription is likely a wake word attempt.</returns>
    Task<bool> IsWakeWordAsync(string transcription, CancellationToken ct = default);
}
