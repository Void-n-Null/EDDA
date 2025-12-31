using System.ComponentModel;
using EDDA.Server.Services.Llm;
using EDDA.Server.Services.WebSearch;
using Microsoft.Extensions.Logging;

namespace EDDA.Server.Tools;

/// <summary>
/// Tool for searching recent news articles.
/// </summary>
public sealed class NewsSearchTool : LlmTool<NewsSearchTool.Parameters>
{
    private readonly IWebSearchService _searchService;
    private readonly ILogger<NewsSearchTool>? _logger;

    public NewsSearchTool(IWebSearchService searchService, ILogger<NewsSearchTool>? logger = null)
    {
        _searchService = searchService;
        _logger = logger;
    }

    public override string Name => "search_news";

    public override string Description =>
        "Search for recent news articles on any topic. Use this for current events, breaking news, " +
        "or when you need the latest information about something happening in the world. " +
        "Optimized for news sources and recent publications.";

    protected override async Task<ToolResult> ExecuteAsync(Parameters p, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(p.Topic))
        {
            return ToolResult.InvalidInput("Topic cannot be empty");
        }

        var request = new NewsSearchRequest
        {
            Query = p.Topic,
            Days = p.DaysBack ?? 7,
            MaxResults = p.MaxResults ?? 5,
            IncludeAnswer = true
        };

        var result = await _searchService.SearchNewsAsync(request, ct);

        if (!result.Success)
        {
            _logger?.LogWarning("News search failed: {Error}", result.Error);
            return ToolResult.Error(result.Error ?? "News search failed");
        }

        // Format results for the LLM
        var formattedResults = result.Results.Select(r => new
        {
            headline = r.Title,
            source_url = r.Url,
            summary = r.Content,
            published = r.PublishedDate
        }).ToArray();

        return ToolResult.Success(new
        {
            summary = result.Answer,
            articles = formattedResults,
            article_count = result.Results.Length
        });
    }

    public sealed class Parameters
    {
        [Description("The news topic to search for (e.g., 'AI developments', 'stock market', 'weather in Seattle').")]
        public string? Topic { get; init; }

        [Description("How many days back to search. Default is 7 (one week). Use 1 for today's news only.")]
        public int? DaysBack { get; init; }

        [Description("Maximum number of articles (5-20). Default is 5.")]
        public int? MaxResults { get; init; }
    }
}
