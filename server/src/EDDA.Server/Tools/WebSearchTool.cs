using System.ComponentModel;
using EDDA.Server.Services.Llm;
using EDDA.Server.Services.WebSearch;
using Microsoft.Extensions.Logging;

namespace EDDA.Server.Tools;

/// <summary>
/// Tool for searching the web for information.
/// </summary>
public sealed class WebSearchTool : LlmTool<WebSearchTool.Parameters>
{
    private readonly IWebSearchService _searchService;
    private readonly ILogger<WebSearchTool>? _logger;

    public WebSearchTool(IWebSearchService searchService, ILogger<WebSearchTool>? logger = null)
    {
        _searchService = searchService;
        _logger = logger;
    }

    public override string Name => "search_web";

    public override string Description =>
        "Search the web for current information on any topic. Use this when you need real-time data, " +
        "recent events, facts you're unsure about, or information that changes over time. " +
        "Returns relevant results with snippets and an AI-generated summary answer.";

    protected override async Task<ToolResult> ExecuteAsync(Parameters p, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(p.Query))
        {
            return ToolResult.InvalidInput("Query cannot be empty");
        }

        var request = new WebSearchRequest
        {
            Query = p.Query,
            MaxResults = p.MaxResults ?? 5,
            UseAdvancedSearch = p.DeepSearch ?? false,
            TimeRange = p.TimeRange,
            IncludeAnswer = true
        };

        var result = await _searchService.SearchAsync(request, ct);

        if (!result.Success)
        {
            _logger?.LogWarning("Web search failed: {Error}", result.Error);
            return ToolResult.Error(result.Error ?? "Search failed");
        }

        // Format results for the LLM
        var formattedResults = result.Results.Select(r => new
        {
            title = r.Title,
            url = r.Url,
            snippet = r.Content,
            date = r.PublishedDate
        }).ToArray();

        return ToolResult.Success(new
        {
            answer = result.Answer,
            results = formattedResults,
            result_count = result.Results.Length,
            follow_up_questions = result.FollowUpQuestions
        });
    }

    public sealed class Parameters
    {
        [Description("The search query. Be specific for better results.")]
        public string? Query { get; init; }

        [Description("Maximum number of results (5-20). Default is 5.")]
        public int? MaxResults { get; init; }

        [Description("Enable deeper search for more comprehensive results. Takes longer but better for research.")]
        public bool? DeepSearch { get; init; }

        [Description("Filter by time: 'day', 'week', 'month', or 'year'. Leave empty for all time.")]
        public string? TimeRange { get; init; }
    }
}
