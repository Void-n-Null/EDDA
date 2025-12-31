namespace EDDA.Server.Services.WebSearch;

/// <summary>
/// Service for searching the web and extracting content from URLs.
/// </summary>
public interface IWebSearchService
{
    /// <summary>
    /// Whether the service is initialized and ready to use.
    /// </summary>
    bool IsInitialized { get; }

    /// <summary>
    /// Initialize the web search service.
    /// </summary>
    Task InitializeAsync(CancellationToken ct = default);

    /// <summary>
    /// Search the web for information.
    /// </summary>
    Task<WebSearchResult> SearchAsync(WebSearchRequest request, CancellationToken ct = default);

    /// <summary>
    /// Search for recent news articles.
    /// </summary>
    Task<WebSearchResult> SearchNewsAsync(NewsSearchRequest request, CancellationToken ct = default);

    /// <summary>
    /// Extract content from one or more URLs.
    /// </summary>
    Task<WebExtractResult> ExtractAsync(WebExtractRequest request, CancellationToken ct = default);
}

/// <summary>
/// Request for a general web search.
/// </summary>
public record WebSearchRequest
{
    /// <summary>
    /// The search query.
    /// </summary>
    public required string Query { get; init; }

    /// <summary>
    /// Maximum number of results to return (5-20).
    /// </summary>
    public int MaxResults { get; init; } = 5;

    /// <summary>
    /// Whether to use advanced (deeper) search. Costs more but returns better results.
    /// </summary>
    public bool UseAdvancedSearch { get; init; } = false;

    /// <summary>
    /// Time range filter: "day", "week", "month", "year", or null for all time.
    /// </summary>
    public string? TimeRange { get; init; }

    /// <summary>
    /// Domains to specifically include in results.
    /// </summary>
    public string[]? IncludeDomains { get; init; }

    /// <summary>
    /// Domains to exclude from results.
    /// </summary>
    public string[]? ExcludeDomains { get; init; }

    /// <summary>
    /// Whether to include an AI-generated answer summary.
    /// </summary>
    public bool IncludeAnswer { get; init; } = true;

    /// <summary>
    /// Whether to include images in results.
    /// </summary>
    public bool IncludeImages { get; init; } = false;
}

/// <summary>
/// Request for a news-specific search.
/// </summary>
public record NewsSearchRequest
{
    /// <summary>
    /// The search query.
    /// </summary>
    public required string Query { get; init; }

    /// <summary>
    /// Number of days back to search (e.g., 3 for last 3 days).
    /// </summary>
    public int Days { get; init; } = 7;

    /// <summary>
    /// Maximum number of results to return (5-20).
    /// </summary>
    public int MaxResults { get; init; } = 5;

    /// <summary>
    /// Whether to include an AI-generated answer summary.
    /// </summary>
    public bool IncludeAnswer { get; init; } = true;
}

/// <summary>
/// Request to extract content from URLs.
/// </summary>
public record WebExtractRequest
{
    /// <summary>
    /// URLs to extract content from.
    /// </summary>
    public required string[] Urls { get; init; }

    /// <summary>
    /// Whether to use advanced extraction (better for complex pages like LinkedIn).
    /// </summary>
    public bool UseAdvancedExtraction { get; init; } = false;

    /// <summary>
    /// Whether to include images from the pages.
    /// </summary>
    public bool IncludeImages { get; init; } = false;
}

/// <summary>
/// Result of a web search.
/// </summary>
public record WebSearchResult
{
    /// <summary>
    /// Whether the search succeeded.
    /// </summary>
    public bool Success { get; init; }

    /// <summary>
    /// Error message if the search failed.
    /// </summary>
    public string? Error { get; init; }

    /// <summary>
    /// AI-generated answer summary (if requested).
    /// </summary>
    public string? Answer { get; init; }

    /// <summary>
    /// The search results.
    /// </summary>
    public SearchResultItem[] Results { get; init; } = [];

    /// <summary>
    /// Suggested follow-up questions.
    /// </summary>
    public string[]? FollowUpQuestions { get; init; }

    /// <summary>
    /// Response time in seconds.
    /// </summary>
    public double ResponseTimeSeconds { get; init; }
}

/// <summary>
/// A single search result item.
/// </summary>
public record SearchResultItem
{
    /// <summary>
    /// URL of the result.
    /// </summary>
    public required string Url { get; init; }

    /// <summary>
    /// Title of the result.
    /// </summary>
    public required string Title { get; init; }

    /// <summary>
    /// Content snippet from the result.
    /// </summary>
    public required string Content { get; init; }

    /// <summary>
    /// Relevance score (0-1).
    /// </summary>
    public double Score { get; init; }

    /// <summary>
    /// Publication date if available.
    /// </summary>
    public string? PublishedDate { get; init; }
}

/// <summary>
/// Result of content extraction.
/// </summary>
public record WebExtractResult
{
    /// <summary>
    /// Whether the extraction succeeded (at least partially).
    /// </summary>
    public bool Success { get; init; }

    /// <summary>
    /// Error message if the extraction completely failed.
    /// </summary>
    public string? Error { get; init; }

    /// <summary>
    /// Successfully extracted content.
    /// </summary>
    public ExtractedContent[] Results { get; init; } = [];

    /// <summary>
    /// URLs that failed to extract.
    /// </summary>
    public string[] FailedUrls { get; init; } = [];

    /// <summary>
    /// Response time in seconds.
    /// </summary>
    public double ResponseTimeSeconds { get; init; }
}

/// <summary>
/// Content extracted from a URL.
/// </summary>
public record ExtractedContent
{
    /// <summary>
    /// The source URL.
    /// </summary>
    public required string Url { get; init; }

    /// <summary>
    /// The extracted text content.
    /// </summary>
    public required string Content { get; init; }

    /// <summary>
    /// Images found on the page (if requested).
    /// </summary>
    public string[]? Images { get; init; }
}
