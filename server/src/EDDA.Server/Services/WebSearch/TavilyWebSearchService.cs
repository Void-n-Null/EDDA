using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EDDA.Server.Models;
using Microsoft.Extensions.Logging;

namespace EDDA.Server.Services.WebSearch;

/// <summary>
/// Web search service implementation using Tavily API.
/// </summary>
public sealed class TavilyWebSearchService : IWebSearchService, IDisposable
{
    private readonly WebSearchConfig _config;
    private readonly HttpClient _httpClient;
    private readonly ILogger<TavilyWebSearchService>? _logger;
    private bool _isInitialized;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public TavilyWebSearchService(WebSearchConfig config, ILogger<TavilyWebSearchService>? logger = null)
    {
        _config = config;
        _logger = logger;

        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(_config.BaseUrl),
            Timeout = TimeSpan.FromSeconds(_config.TimeoutSeconds)
        };
        _httpClient.DefaultRequestHeaders.Authorization = 
            new AuthenticationHeaderValue("Bearer", _config.ApiKey);
        _httpClient.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public bool IsInitialized => _isInitialized;

    public Task InitializeAsync(CancellationToken ct = default)
    {
        _logger?.LogInformation("Web search service initialized (provider: Tavily)");
        _isInitialized = true;
        return Task.CompletedTask;
    }

    public async Task<WebSearchResult> SearchAsync(WebSearchRequest request, CancellationToken ct = default)
    {
        var payload = new TavilySearchRequest
        {
            Query = request.Query,
            SearchDepth = request.UseAdvancedSearch ? "advanced" : "basic",
            Topic = "general",
            TimeRange = request.TimeRange,
            MaxResults = Math.Clamp(request.MaxResults, 5, 20),
            IncludeAnswer = request.IncludeAnswer,
            IncludeImages = request.IncludeImages,
            IncludeDomains = request.IncludeDomains,
            ExcludeDomains = request.ExcludeDomains
        };

        return await ExecuteSearchAsync(payload, ct);
    }

    public async Task<WebSearchResult> SearchNewsAsync(NewsSearchRequest request, CancellationToken ct = default)
    {
        var payload = new TavilySearchRequest
        {
            Query = request.Query,
            SearchDepth = "basic",
            Topic = "news",
            Days = request.Days,
            MaxResults = Math.Clamp(request.MaxResults, 5, 20),
            IncludeAnswer = request.IncludeAnswer
        };

        return await ExecuteSearchAsync(payload, ct);
    }

    public async Task<WebExtractResult> ExtractAsync(WebExtractRequest request, CancellationToken ct = default)
    {
        if (request.Urls.Length == 0)
        {
            return new WebExtractResult
            {
                Success = false,
                Error = "No URLs provided for extraction"
            };
        }

        var payload = new TavilyExtractRequest
        {
            Urls = request.Urls,
            ExtractDepth = request.UseAdvancedExtraction ? "advanced" : "basic",
            IncludeImages = request.IncludeImages
        };

        try
        {
            _logger?.LogDebug("Extracting content from {Count} URL(s)", request.Urls.Length);

            var json = JsonSerializer.Serialize(payload, JsonOptions);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync("/extract", content, ct);
            var responseText = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger?.LogWarning("Extract failed: {Status} - {Response}", 
                    response.StatusCode, responseText);
                return new WebExtractResult
                {
                    Success = false,
                    Error = $"Extract failed: {response.StatusCode}"
                };
            }

            var result = JsonSerializer.Deserialize<TavilyExtractResponse>(responseText, JsonOptions);
            
            if (result is null)
            {
                return new WebExtractResult
                {
                    Success = false,
                    Error = "Failed to parse extract response"
                };
            }

            var extractedResults = result.Results?.Select(r => new ExtractedContent
            {
                Url = r.Url ?? "",
                Content = r.RawContent ?? "",
                Images = r.Images
            }).ToArray() ?? [];

            _logger?.LogDebug("Extracted {Count} page(s), {Failed} failed",
                extractedResults.Length, result.FailedResults?.Length ?? 0);

            return new WebExtractResult
            {
                Success = true,
                Results = extractedResults,
                FailedUrls = result.FailedResults ?? [],
                ResponseTimeSeconds = result.ResponseTime ?? 0
            };
        }
        catch (TaskCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Extract request failed");
            return new WebExtractResult
            {
                Success = false,
                Error = $"Extract failed: {ex.Message}"
            };
        }
    }

    private async Task<WebSearchResult> ExecuteSearchAsync(TavilySearchRequest payload, CancellationToken ct)
    {
        try
        {
            _logger?.LogDebug("Searching: {Query}", payload.Query);

            var json = JsonSerializer.Serialize(payload, JsonOptions);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync("/search", content, ct);
            var responseText = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger?.LogWarning("Search failed: {Status} - {Response}", 
                    response.StatusCode, responseText);
                return new WebSearchResult
                {
                    Success = false,
                    Error = $"Search failed: {response.StatusCode}"
                };
            }

            var result = JsonSerializer.Deserialize<TavilySearchResponse>(responseText, JsonOptions);
            
            if (result is null)
            {
                return new WebSearchResult
                {
                    Success = false,
                    Error = "Failed to parse search response"
                };
            }

            var searchResults = result.Results?.Select(r => new SearchResultItem
            {
                Url = r.Url ?? "",
                Title = r.Title ?? "",
                Content = r.Content ?? "",
                Score = r.Score ?? 0,
                PublishedDate = r.PublishedDate
            }).ToArray() ?? [];

            _logger?.LogDebug("Search returned {Count} results", searchResults.Length);

            return new WebSearchResult
            {
                Success = true,
                Answer = result.Answer,
                Results = searchResults,
                FollowUpQuestions = result.FollowUpQuestions,
                ResponseTimeSeconds = result.ResponseTime ?? 0
            };
        }
        catch (TaskCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Search request failed");
            return new WebSearchResult
            {
                Success = false,
                Error = $"Search failed: {ex.Message}"
            };
        }
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    // ────────────────────────────────────────────────────────────────────────
    // Tavily API DTOs (internal)
    // ────────────────────────────────────────────────────────────────────────

    private sealed class TavilySearchRequest
    {
        public required string Query { get; init; }
        public string? SearchDepth { get; init; }
        public string? Topic { get; init; }
        public int? Days { get; init; }
        public string? TimeRange { get; init; }
        public int? MaxResults { get; init; }
        public bool? IncludeAnswer { get; init; }
        public bool? IncludeImages { get; init; }
        public bool? IncludeImageDescriptions { get; init; }
        public bool? IncludeRawContent { get; init; }
        public string[]? IncludeDomains { get; init; }
        public string[]? ExcludeDomains { get; init; }
    }

    private sealed class TavilySearchResponse
    {
        public string? Answer { get; init; }
        public TavilySearchResultItem[]? Results { get; init; }
        public string[]? FollowUpQuestions { get; init; }
        public string? Query { get; init; }
        public double? ResponseTime { get; init; }
    }

    private sealed class TavilySearchResultItem
    {
        public string? Url { get; init; }
        public string? Title { get; init; }
        public string? Content { get; init; }
        public double? Score { get; init; }
        public string? PublishedDate { get; init; }
        public string? RawContent { get; init; }
    }

    private sealed class TavilyExtractRequest
    {
        public required string[] Urls { get; init; }
        public string? ExtractDepth { get; init; }
        public bool? IncludeImages { get; init; }
    }

    private sealed class TavilyExtractResponse
    {
        public TavilyExtractResultItem[]? Results { get; init; }
        public string[]? FailedResults { get; init; }
        public double? ResponseTime { get; init; }
    }

    private sealed class TavilyExtractResultItem
    {
        public string? Url { get; init; }
        public string? RawContent { get; init; }
        public string[]? Images { get; init; }
    }
}
