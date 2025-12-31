namespace EDDA.Server.Models;

/// <summary>
/// Available TTS backends.
/// </summary>
public enum TtsBackend
{
    /// <summary>High quality, GPU-accelerated, ~1.5x realtime</summary>
    Chatterbox,

    /// <summary>Good quality, CPU-only, ~20-50x realtime (much faster)</summary>
    Piper
}

/// <summary>
/// A Chatterbox TTS endpoint with priority for failover.
/// Lower priority number = higher preference (used first when available).
/// </summary>
public class ChatterboxEndpoint
{
    public string Name { get; init; } = "";
    public string Url { get; init; } = "";
    public int Priority { get; init; }

    public override string ToString() => $"{Name} ({Url})";
}

/// <summary>
/// Configuration for the TTS microservice client.
/// </summary>
public class TtsConfig
{
    // ====================================================================
    // CHANGE THIS TO SWITCH TTS BACKENDS
    // ====================================================================
    //Chatterbox is slower, but much higher quality. Piper is much faster, but sounds like traditional robotic TTS
    private const TtsBackend DefaultBackend = TtsBackend.Chatterbox;
    // ====================================================================

    /// <summary>
    /// Chatterbox endpoints in priority order. Lower priority = preferred.
    /// The service will use the highest-priority available endpoint.
    /// </summary>
    public List<ChatterboxEndpoint> ChatterboxEndpoints { get; } =
    [
        new() { Name = "Dev-5070Ti", Url = "http://10.0.0.210:5000", Priority = 1 },
        // Priority 2: Basement server with RTX 2070 Super (~1.3x realtime)
        new() { Name = "Basement-2070S", Url = "http://localhost:5000", Priority = 2 }
    ];

    /// <summary>
    /// Health check timeout for endpoint selection (ms).
    /// Keep this fast since we check before every TTS request.
    /// </summary>
    public static int EndpointHealthTimeoutMs => 150;

    private static string PiperUrl => "http://localhost:5001";

    public TtsBackend ActiveBackend { get; set; } = DefaultBackend;

    /// <summary>
    /// Currently active Chatterbox endpoint (selected dynamically).
    /// </summary>
    public ChatterboxEndpoint? ActiveChatterboxEndpoint { get; set; }

    public string ActiveUrl => ActiveBackend == TtsBackend.Piper
        ? PiperUrl
        : ActiveChatterboxEndpoint?.Url ?? ChatterboxEndpoints.FirstOrDefault()?.Url ?? "http://localhost:5000";

    public string BackendName => ActiveBackend == TtsBackend.Piper
        ? "Piper"
        : $"Chatterbox/{ActiveChatterboxEndpoint?.Name ?? "Unknown"}";

    /// <summary>
    /// Request timeout in seconds.
    /// </summary>
    public int TimeoutSeconds { get; init; } = 30;

    /// <summary>
    /// Health check interval in seconds.
    /// </summary>
    public int HealthCheckIntervalSeconds { get; init; } = 30;

    /// <summary>
    /// Number of retries for failed requests.
    /// </summary>
    public int RetryCount { get; init; } = 3;

    /// <summary>
    /// Initial retry delay in milliseconds.
    /// </summary>
    public int RetryDelayMs { get; init; } = 200;

    /// <summary>
    /// Circuit breaker failure threshold.
    /// </summary>
    public int CircuitBreakerThreshold { get; init; } = 5;

    /// <summary>
    /// Circuit breaker recovery timeout in seconds.
    /// </summary>
    public int CircuitBreakerTimeoutSeconds { get; init; } = 30;

    /// <summary>
    /// Default emotion exaggeration (0-1).
    /// </summary>
    public float DefaultExaggeration { get; init; } = 0.5f;

    /// <summary>
    /// Default CFG weight (0-1).
    /// </summary>
    public float DefaultCfgWeight { get; init; } = 0.5f;

    /// <summary>
    /// Voice to use for TTS. This should match the name of an embedded voice resource
    /// (without .wav extension).
    ///
    /// Examples:
    ///   - "blondie" -> uses embedded resource EDDA.Server.Resources.Voices.blondie.wav
    ///   - "asuka" -> uses embedded resource EDDA.Server.Resources.Voices.asuka.wav
    ///   - null or empty -> uses Chatterbox's built-in default voice
    ///
    /// The voice file is automatically uploaded to the TTS service and cached there.
    /// </summary>
    public string? VoiceName { get; init; }

    /// <summary>
    /// Whether voice cloning is enabled (VoiceName is set).
    /// </summary>
    public bool UseVoiceCloning => !string.IsNullOrWhiteSpace(VoiceName);

    /// <summary>
    /// Enable adaptive tempo slowdown to mask generation gaps between sentences.
    /// </summary>
    public bool TempoAdjustmentEnabled { get; init; } = true;

    /// <summary>
    /// Minimum tempo (playback speed).
    /// Going below ~0.85 starts to sound noticeably slow. This is just to buy a little
    /// time if the sentence after this one is going to take a while to generate.
    /// It doesn't help much but it doesn't hurt either.
    /// </summary>
    public float MinTempo { get; init; } = 0.92f;

    /// <summary>
    /// Maximum tempo (playback speed).
    /// Going above ~1.08 starts to sound noticeably rushed.
    /// Not a performance thing lol, just for parity with the min tempo. We just set this to 1.0 for now.
    /// </summary>
    public float MaxTempo { get; init; } = 1f;

    /// <summary>
    /// Average milliseconds per character for TTS generation (used to estimate next sentence timing).
    /// Tune based on your TTS backend performance. Chatterbox: ~60-70ms/char, Piper: ~5-10ms/char.
    /// Intentionally conservative (high) to ensure tempo adjustment kicks in when needed.
    /// </summary>
    public float AvgMsPerChar { get; init; } = 30.0f;

    /// <summary>
    /// Create config. Uses DEFAULT_BACKEND const above.
    /// </summary>
    public static TtsConfig FromEnvironment()
    {
        return new TtsConfig
        {
            ActiveBackend = DefaultBackend,
            TimeoutSeconds = ParseIntEnv("TTS_TIMEOUT_SECONDS", 30),
            HealthCheckIntervalSeconds = ParseIntEnv("TTS_HEALTH_CHECK_INTERVAL", 30),
            RetryCount = ParseIntEnv("TTS_RETRY_COUNT", 3),
            RetryDelayMs = ParseIntEnv("TTS_RETRY_DELAY_MS", 200),
            CircuitBreakerThreshold = ParseIntEnv("TTS_CIRCUIT_BREAKER_THRESHOLD", 5),
            CircuitBreakerTimeoutSeconds = ParseIntEnv("TTS_CIRCUIT_BREAKER_TIMEOUT", 30),
            DefaultExaggeration = ParseFloatEnv("TTS_DEFAULT_EXAGGERATION", 0.5f),
            DefaultCfgWeight = ParseFloatEnv("TTS_DEFAULT_CFG_WEIGHT", 0.5f),
            TempoAdjustmentEnabled = ParseBoolEnv("TTS_TEMPO_ADJUSTMENT_ENABLED", true),
            MinTempo = ParseFloatEnv("TTS_MIN_TEMPO", 0.92f),
            MaxTempo = ParseFloatEnv("TTS_MAX_TEMPO", 1f),
            AvgMsPerChar = ParseFloatEnv("TTS_AVG_MS_PER_CHAR", 65.0f),
            // Voice name (embedded resource name without .wav extension), or null for default voice
            VoiceName = "blondie",
        };
    }

    private static string? ParseStringEnv(string key, string? defaultValue)
    {
        var value = Environment.GetEnvironmentVariable(key);
        return string.IsNullOrWhiteSpace(value) ? defaultValue : value;
    }

    private static int ParseIntEnv(string key, int defaultValue)
    {
        var value = Environment.GetEnvironmentVariable(key);
        return int.TryParse(value, out var result) ? result : defaultValue;
    }

    private static float ParseFloatEnv(string key, float defaultValue)
    {
        var value = Environment.GetEnvironmentVariable(key);
        return float.TryParse(value, out var result) ? result : defaultValue;
    }

    private static bool ParseBoolEnv(string key, bool defaultValue)
    {
        var value = Environment.GetEnvironmentVariable(key);
        return bool.TryParse(value, out var result) ? result : defaultValue;
    }
}
