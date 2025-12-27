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
    /// Generate speech from text.
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
    /// Generate speech with voice cloning from reference audio.
    /// </summary>
    /// <param name="text">Text to synthesize.</param>
    /// <param name="voiceReferencePath">Path to reference audio (on TTS server).</param>
    /// <param name="exaggeration">Emotion exaggeration (0-1). Default 0.5.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>WAV audio bytes.</returns>
    Task<byte[]> GenerateSpeechWithVoiceAsync(
        string text,
        string voiceReferencePath,
        float exaggeration = 0.5f,
        CancellationToken cancellationToken = default);
}

