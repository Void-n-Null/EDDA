using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace EDDA.Server.Agent.Context;

/// <summary>
/// Builds the final prompt by combining a template with all registered context providers.
/// 
/// Template placeholders use double-brace syntax: {{key}}
/// Each provider fills its corresponding placeholder.
/// Unfilled placeholders are removed from the final output.
/// </summary>
public partial class ContextBuilder
{
    private readonly List<IContextProvider> _providers;
    private readonly ILogger<ContextBuilder>? _logger;

    public ContextBuilder(
        IEnumerable<IContextProvider> providers,
        ILogger<ContextBuilder>? logger = null)
    {
        _providers = providers.OrderBy(p => p.Priority).ToList();
        _logger = logger;

        _logger?.LogDebug(
            "ContextBuilder initialized with {Count} providers: {Keys}",
            _providers.Count,
            string.Join(", ", _providers.Select(p => p.Key)));
    }

    /// <summary>
    /// Build a prompt by filling template placeholders with provider context.
    /// </summary>
    /// <param name="template">Template string with {{key}} placeholders.</param>
    /// <param name="request">Context request passed to all providers.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Filled template with all placeholders resolved.</returns>
    public async Task<string> BuildAsync(
        string template,
        ContextRequest request,
        CancellationToken ct = default)
    {
        var result = template;

        foreach (var provider in _providers)
        {
            var placeholder = $"{{{{{provider.Key}}}}}";

            // Skip if placeholder not in template (optimization)
            if (!result.Contains(placeholder))
                continue;

            try
            {
                var context = await provider.GetContextAsync(request, ct);

                if (!string.IsNullOrWhiteSpace(context))
                {
                    result = result.Replace(placeholder, context);
                    _logger?.LogDebug("Context {Key}: {Length} chars", provider.Key, context.Length);
                }
                else
                {
                    // Remove placeholder and any surrounding blank lines
                    result = result.Replace(placeholder, "");
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Context provider {Key} failed, continuing without it", provider.Key);
                result = result.Replace(placeholder, "");
            }
        }

        // Clean up any unfilled placeholders (from missing providers)
        result = UnfilledPlaceholderRegex().Replace(result, "");

        // Clean up excessive blank lines
        result = ExcessiveNewlinesRegex().Replace(result, "\n\n");

        return result.Trim();
    }

    /// <summary>
    /// Get all registered provider keys (for debugging/testing).
    /// </summary>
    public IReadOnlyList<string> RegisteredKeys =>
        _providers.Select(p => p.Key).ToList();

    [GeneratedRegex(@"\{\{[^}]+\}\}")]
    private static partial Regex UnfilledPlaceholderRegex();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex ExcessiveNewlinesRegex();
}
