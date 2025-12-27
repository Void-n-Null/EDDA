namespace EDDA.Server.Models;

/// <summary>
/// Configuration for audio processing and Whisper transcription.
/// </summary>
public record AudioConfig
{
    public int SampleRate { get; init; } = 16000;
    public int WhisperThreads { get; init; } = Environment.ProcessorCount;
    public double WaitingForMoreTimeoutMs { get; init; } = 200;
    public string? ModelPath { get; init; }
    
    /// <summary>Bytes per second for 16-bit mono PCM at configured sample rate.</summary>
    public int BytesPerSecond => SampleRate * 2;
    
    /// <summary>
    /// Create AudioConfig from environment variables with sensible defaults.
    /// </summary>
    public static AudioConfig FromEnvironment()
    {
        return new AudioConfig
        {
            SampleRate = ParseIntEnv("WHISPER_SAMPLE_RATE", 16000),
            WhisperThreads = ParseIntEnv("WHISPER_THREADS", Math.Max(1, Environment.ProcessorCount)),
            WaitingForMoreTimeoutMs = ParseDoubleEnv("WHISPER_WAITING_TIMEOUT_MS", 200),
            ModelPath = Environment.GetEnvironmentVariable("WHISPER_MODEL_PATH")
        };
    }
    
    private static int ParseIntEnv(string key, int defaultValue)
    {
        var v = Environment.GetEnvironmentVariable(key);
        return int.TryParse(v, out var parsed) && parsed > 0 ? parsed : defaultValue;
    }
    
    private static double ParseDoubleEnv(string key, double defaultValue)
    {
        var v = Environment.GetEnvironmentVariable(key);
        return double.TryParse(v, out var parsed) && parsed > 0 ? parsed : defaultValue;
    }
}

