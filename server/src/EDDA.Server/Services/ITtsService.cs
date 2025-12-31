using EDDA.Server.Models;

namespace EDDA.Server.Services;

/// <summary>
/// Service for text-to-speech synthesis using the TTS microservice.
/// </summary>
public interface ITtsService
{
    /// <summary>
    /// Whether the TTS service is available and healthy.
    /// </summary>
    bool IsHealthy { get; }

    /// <summary>
    /// Last health check result message.
    /// </summary>
    string? LastHealthStatus { get; }

    /// <summary>
    /// Currently active TTS backend.
    /// </summary>
    TtsBackend ActiveBackend { get; }

    /// <summary>
    /// Switch to a different TTS backend at runtime.
    /// </summary>
    Task SwitchBackendAsync(TtsBackend backend, CancellationToken cancellationToken = default);

    /// <summary>
    /// Initialize the service and start health monitoring.
    /// </summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Generate speech from text using the configured default voice.
    /// </summary>
    /// <param name="text">Text to synthesize.</param>
    /// <param name="exaggeration">Emotion exaggeration (0-1). Default 0.5.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>WAV audio bytes.</returns>
    Task<byte[]> GenerateSpeechAsync(
        string text,
        float exaggeration = 0.5f,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Generate speech with a specific voice.
    /// </summary>
    /// <param name="text">Text to synthesize.</param>
    /// <param name="voiceName">Voice name (matches filename in voices directory, without .wav). Null for default voice.</param>
    /// <param name="exaggeration">Emotion exaggeration (0-1). Default 0.5.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>WAV audio bytes.</returns>
    Task<byte[]> GenerateSpeechWithVoiceAsync(
        string text,
        string? voiceName,
        float exaggeration = 0.5f,
        CancellationToken cancellationToken = default);
}
