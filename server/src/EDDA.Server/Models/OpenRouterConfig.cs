namespace EDDA.Server.Models;

/// <summary>
/// Configuration for the OpenRouter LLM service.
/// </summary>
public class OpenRouterConfig
{
    /// <summary>
    /// OpenRouter API key.
    /// </summary>
    public required string ApiKey { get; init; }

    /// <summary>
    /// Base URL for OpenRouter API. Must end with trailing slash for HttpClient path resolution.
    /// </summary>
    public string BaseUrl { get; init; } = "https://openrouter.ai/api/v1/";

    /// <summary>
    /// Default model to use for chat completions.
    /// Format: "provider/model" (e.g., "anthropic/claude-sonnet-4", "openai/gpt-4o").
    /// </summary>
    /// <summary>
    /// DO NOT CHANGE THIS MODEL WITHOUT EXPLICIT USER APPROVAL.
    /// Gemini 3 Flash is the intentional choice - it's fast, cheap, and good.
    /// </summary>
    public string DefaultModel { get; init; } = "google/gemini-3-flash-preview";

    /// <summary>
    /// Preferred provider for the default model (OpenRouter routing hint).
    /// Example: "Google AI Studio".
    /// Set to empty to allow OpenRouter to choose automatically.
    /// </summary>
    public string DefaultProvider { get; init; } = "Google AI Studio";

    /// <summary>
    /// Model to use for fast, cheap operations (e.g., wake word detection).
    /// Gemini 2.5 Flash Lite is extremely fast and cheap for simple YES/NO tasks.
    /// </summary>
    public string FastModel { get; init; } = "google/gemini-2.5-flash-lite-preview-09-2025";

    /// <summary>
    /// Preferred provider for the fast model (OpenRouter routing hint).
    /// </summary>
    public string FastProvider { get; init; } = "Google AI Studio";

    /// <summary>
    /// Maximum tokens for response generation.
    /// </summary>
    public int MaxTokens { get; init; } = 4096;

    /// <summary>
    /// Temperature for response generation (0-2).
    /// </summary>
    public float Temperature { get; init; } = 0.7f;

    /// <summary>
    /// HTTP request timeout in seconds.
    /// </summary>
    public int TimeoutSeconds { get; init; } = 120;

    /// <summary>
    /// Optional HTTP-Referer header for OpenRouter tracking.
    /// </summary>
    public string? HttpReferer { get; init; }

    /// <summary>
    /// Optional X-Title header for OpenRouter dashboard display.
    /// </summary>
    public string? AppTitle { get; init; } = "EDDA";

    /// <summary>
    /// Whether to enable streaming responses.
    /// </summary>
    public bool EnableStreaming { get; init; } = true;

    /// <summary>
    /// Number of retries for failed requests.
    /// </summary>
    public int RetryCount { get; init; } = 3;

    /// <summary>
    /// Initial retry delay in milliseconds.
    /// </summary>
    public int RetryDelayMs { get; init; } = 500;

    /// <summary>
    /// Create configuration from environment variables.
    /// </summary>
    public static OpenRouterConfig FromEnvironment()
    {
        var apiKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                "OPENROUTER_API_KEY environment variable is required. " +
                "Get your API key from https://openrouter.ai/keys");
        }

        return new OpenRouterConfig
        {
            ApiKey = apiKey,
            BaseUrl = ParseStringEnv("OPENROUTER_BASE_URL", "https://openrouter.ai/api/v1/"),
            DefaultModel = ParseStringEnv("OPENROUTER_DEFAULT_MODEL", "google/gemini-3-flash-preview"),
            DefaultProvider = ParseStringEnv("OPENROUTER_DEFAULT_PROVIDER", "Google AI Studio"),
            FastModel = ParseStringEnv("OPENROUTER_FAST_MODEL", "google/gemini-2.5-flash-lite-preview-09-2025"),
            FastProvider = ParseStringEnv("OPENROUTER_FAST_PROVIDER", "Google AI Studio"),
            MaxTokens = ParseIntEnv("OPENROUTER_MAX_TOKENS", 4096),
            Temperature = ParseFloatEnv("OPENROUTER_TEMPERATURE", 0.7f),
            TimeoutSeconds = ParseIntEnv("OPENROUTER_TIMEOUT_SECONDS", 120),
            HttpReferer = Environment.GetEnvironmentVariable("OPENROUTER_HTTP_REFERER"),
            AppTitle = ParseStringEnv("OPENROUTER_APP_TITLE", "EDDA"),
            EnableStreaming = ParseBoolEnv("OPENROUTER_ENABLE_STREAMING", true),
            RetryCount = ParseIntEnv("OPENROUTER_RETRY_COUNT", 3),
            RetryDelayMs = ParseIntEnv("OPENROUTER_RETRY_DELAY_MS", 500)
        };
    }

    private static string ParseStringEnv(string key, string defaultValue)
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
