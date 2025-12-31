using System.ComponentModel;
using EDDA.Server.Services.Llm;
using EDDA.Server.Services.WebSearch;
using Microsoft.Extensions.Logging;

namespace EDDA.Server.Tools;

/// <summary>
/// Tool for extracting content from web pages.
/// </summary>
public sealed class WebExtractTool : LlmTool<WebExtractTool.Parameters>
{
    private readonly IWebSearchService _searchService;
    private readonly ILogger<WebExtractTool>? _logger;

    public WebExtractTool(IWebSearchService searchService, ILogger<WebExtractTool>? logger = null)
    {
        _searchService = searchService;
        _logger = logger;
    }

    public override string Name => "extract_webpage";

    public override string Description =>
        "Extract the full text content from one or more web pages. Use this when you have a specific URL " +
        "and need to read its contents in detail. Good for reading articles, documentation, or any page " +
        "where you need more than just a snippet.";

    protected override async Task<ToolResult> ExecuteAsync(Parameters p, CancellationToken ct)
    {
        if (p.Urls is null || p.Urls.Length == 0)
        {
            return ToolResult.InvalidInput("At least one URL is required");
        }

        // Validate URLs
        var validUrls = p.Urls
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Where(u => Uri.TryCreate(u, UriKind.Absolute, out var uri) && 
                        (uri.Scheme == "http" || uri.Scheme == "https"))
            .ToArray();

        if (validUrls.Length == 0)
        {
            return ToolResult.InvalidInput("No valid HTTP/HTTPS URLs provided");
        }

        var request = new WebExtractRequest
        {
            Urls = validUrls,
            UseAdvancedExtraction = p.DeepExtract ?? false
        };

        var result = await _searchService.ExtractAsync(request, ct);

        if (!result.Success)
        {
            _logger?.LogWarning("Web extraction failed: {Error}", result.Error);
            return ToolResult.Error(result.Error ?? "Extraction failed");
        }

        // Format results for the LLM
        var extractedPages = result.Results.Select(r => new
        {
            url = r.Url,
            content = TruncateContent(r.Content, p.MaxContentLength ?? 10000)
        }).ToArray();

        if (result.FailedUrls.Length > 0 && extractedPages.Length > 0)
        {
            return ToolResult.PartialSuccess(new
            {
                pages = extractedPages,
                extracted_count = extractedPages.Length
            }, $"Failed to extract: {string.Join(", ", result.FailedUrls)}");
        }

        if (extractedPages.Length == 0)
        {
            return ToolResult.Error("Could not extract content from any of the provided URLs");
        }

        return ToolResult.Success(new
        {
            pages = extractedPages,
            extracted_count = extractedPages.Length
        });
    }

    private static string TruncateContent(string content, int maxLength)
    {
        if (string.IsNullOrEmpty(content) || content.Length <= maxLength)
            return content;

        // Truncate at a word boundary
        var truncated = content[..maxLength];
        var lastSpace = truncated.LastIndexOf(' ');
        if (lastSpace > maxLength - 100)
            truncated = truncated[..lastSpace];

        return truncated + "... [content truncated]";
    }

    public sealed class Parameters
    {
        [Description("The URL(s) to extract content from. Can be a single URL or multiple.")]
        public string[]? Urls { get; init; }

        [Description("Use advanced extraction for complex pages (LinkedIn, dynamic sites). Slower but more thorough.")]
        public bool? DeepExtract { get; init; }

        [Description("Maximum characters of content to return per page. Default is 10000.")]
        public int? MaxContentLength { get; init; }
    }
}
