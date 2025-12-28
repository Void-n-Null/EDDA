namespace EDDA.Server.Services;

/// <summary>
/// Parsed PCM WAV data.
/// </summary>
public sealed record WavPcm(byte[] Pcm, int SampleRate, int Channels, int BitsPerSample);

/// <summary>
/// Service for WAV audio file processing operations.
/// </summary>
public interface IAudioProcessor
{
    /// <summary>
    /// Attempt to parse a WAV file into its PCM components.
    /// Only supports 16-bit PCM format.
    /// </summary>
    bool TryParsePcmWav(byte[] wav, out WavPcm? parsed);
    
    /// <summary>
    /// Add silence padding to the beginning of a WAV file.
    /// Used to prevent audio device startup clipping.
    /// </summary>
    byte[] AddSilencePadding(byte[] wavBytes, int paddingMs = 150);
    
    /// <summary>
    /// Build a complete WAV file from raw PCM data.
    /// </summary>
    byte[] BuildWavFile(byte[] pcmData, int sampleRate, int channels, int bitsPerSample);
    
    /// <summary>
    /// Apply tempo adjustment to a WAV file using ffmpeg's atempo filter.
    /// Returns the original bytes if tempo is approximately 1.0.
    /// </summary>
    Task<byte[]> AdjustTempoAsync(byte[] wavBytes, float tempo, CancellationToken ct = default);
}
