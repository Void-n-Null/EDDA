namespace EDDA.Server.Models;

/// <summary>
/// Configuration for the web search service.
/// </summary>
public class WebSearchConfig
{
    /// <summary>
    /// API key for the web search provider.
    /// </summary>
    public required string ApiKey { get; init; }

    /// <summary>
    /// Base URL for the search API.
    /// </summary>
    public string BaseUrl { get; init; } = "https://api.tavily.com";

    /// <summary>
    /// HTTP request timeout in seconds.
    /// </summary>
    public int TimeoutSeconds { get; init; } = 30;

    /// <summary>
    /// Default maximum results to return.
    /// </summary>
    public int DefaultMaxResults { get; init; } = 5;

    /// <summary>
    /// Whether to include AI-generated answer summaries by default.
    /// </summary>
    public bool IncludeAnswerByDefault { get; init; } = true;

    /// <summary>
    /// Number of retries for failed requests.
    /// </summary>
    public int RetryCount { get; init; } = 2;

    /// <summary>
    /// Initial retry delay in milliseconds.
    /// </summary>
    public int RetryDelayMs { get; init; } = 500;

    /// <summary>
    /// Create configuration from environment variables.
    /// </summary>
    public static WebSearchConfig FromEnvironment()
    {
        var apiKey = Environment.GetEnvironmentVariable("WEBSEARCH_API_KEY");

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                "WEBSEARCH_API_KEY environment variable is required for web search functionality.");
        }

        return new WebSearchConfig
        {
            ApiKey = apiKey,
            BaseUrl = ParseStringEnv("WEBSEARCH_BASE_URL", "https://api.tavily.com"),
            TimeoutSeconds = ParseIntEnv("WEBSEARCH_TIMEOUT_SECONDS", 30),
            DefaultMaxResults = ParseIntEnv("WEBSEARCH_DEFAULT_MAX_RESULTS", 5),
            IncludeAnswerByDefault = ParseBoolEnv("WEBSEARCH_INCLUDE_ANSWER", true),
            RetryCount = ParseIntEnv("WEBSEARCH_RETRY_COUNT", 2),
            RetryDelayMs = ParseIntEnv("WEBSEARCH_RETRY_DELAY_MS", 500)
        };
    }

    /// <summary>
    /// Try to create configuration from environment variables.
    /// Returns null if the API key is not set (optional service).
    /// </summary>
    public static WebSearchConfig? TryFromEnvironment()
    {
        var apiKey = Environment.GetEnvironmentVariable("WEBSEARCH_API_KEY");
        
        if (string.IsNullOrWhiteSpace(apiKey))
            return null;

        return new WebSearchConfig
        {
            ApiKey = apiKey,
            BaseUrl = ParseStringEnv("WEBSEARCH_BASE_URL", "https://api.tavily.com"),
            TimeoutSeconds = ParseIntEnv("WEBSEARCH_TIMEOUT_SECONDS", 30),
            DefaultMaxResults = ParseIntEnv("WEBSEARCH_DEFAULT_MAX_RESULTS", 5),
            IncludeAnswerByDefault = ParseBoolEnv("WEBSEARCH_INCLUDE_ANSWER", true),
            RetryCount = ParseIntEnv("WEBSEARCH_RETRY_COUNT", 2),
            RetryDelayMs = ParseIntEnv("WEBSEARCH_RETRY_DELAY_MS", 500)
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

    private static bool ParseBoolEnv(string key, bool defaultValue)
    {
        var value = Environment.GetEnvironmentVariable(key);
        return bool.TryParse(value, out var result) ? result : defaultValue;
    }
}
