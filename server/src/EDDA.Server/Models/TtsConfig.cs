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
/// Configuration for the TTS microservice client.
/// </summary>
public class TtsConfig
{
    // ====================================================================
    // CHANGE THIS TO SWITCH TTS BACKENDS
    // ====================================================================
    public const TtsBackend DEFAULT_BACKEND = TtsBackend.Chatterbox;
    // ====================================================================
    
    public string ChatterboxUrl { get; init; } = "http://localhost:5000";
    public string PiperUrl { get; init; } = "http://localhost:5001";
    
    public TtsBackend ActiveBackend { get; set; } = DEFAULT_BACKEND;
    
    public string ActiveUrl => ActiveBackend == TtsBackend.Piper ? PiperUrl : ChatterboxUrl;
    public string BackendName => ActiveBackend.ToString();
    
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
    /// Create config. Uses DEFAULT_BACKEND const above.
    /// </summary>
    public static TtsConfig FromEnvironment()
    {
        return new TtsConfig
        {
            ActiveBackend = DEFAULT_BACKEND,
            TimeoutSeconds = ParseIntEnv("TTS_TIMEOUT_SECONDS", 30),
            HealthCheckIntervalSeconds = ParseIntEnv("TTS_HEALTH_CHECK_INTERVAL", 30),
            RetryCount = ParseIntEnv("TTS_RETRY_COUNT", 3),
            RetryDelayMs = ParseIntEnv("TTS_RETRY_DELAY_MS", 200),
            CircuitBreakerThreshold = ParseIntEnv("TTS_CIRCUIT_BREAKER_THRESHOLD", 5),
            CircuitBreakerTimeoutSeconds = ParseIntEnv("TTS_CIRCUIT_BREAKER_TIMEOUT", 30),
            DefaultExaggeration = ParseFloatEnv("TTS_DEFAULT_EXAGGERATION", 0.5f),
            DefaultCfgWeight = ParseFloatEnv("TTS_DEFAULT_CFG_WEIGHT", 0.5f),
        };
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
}

