using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace EDDA.Server.Services.Llm;

/// <summary>
/// Represents a tool call request from the LLM.
/// </summary>
public record ToolCall
{
    /// <summary>Unique ID for this tool call.</summary>
    public required string Id { get; init; }

    /// <summary>Name of the tool to call.</summary>
    public required string Name { get; init; }

    /// <summary>Arguments as JSON.</summary>
    public required JsonElement Arguments { get; init; }
}

/// <summary>
/// Result of executing a tool call, ready for LLM response.
/// </summary>
public record ToolCallResult
{
    /// <summary>The original tool call ID.</summary>
    public required string ToolCallId { get; init; }

    /// <summary>The tool name.</summary>
    public required string ToolName { get; init; }

    /// <summary>The full result with metadata.</summary>
    public required ToolResult Result { get; init; }

    /// <summary>String content for LLM.</summary>
    public string Content => Result.ForLlm();
}

/// <summary>
/// Executes tool calls from LLM responses.
/// </summary>
public class ToolExecutor
{
    private readonly ToolDiscovery _discovery;
    private readonly ILogger<ToolExecutor>? _logger;
    private readonly TimeSpan _defaultTimeout = TimeSpan.FromSeconds(30);

    public ToolExecutor(ToolDiscovery discovery, ILogger<ToolExecutor>? logger = null)
    {
        _discovery = discovery;
        _logger = logger;
    }

    /// <summary>
    /// Execute a single tool call.
    /// </summary>
    public async Task<ToolCallResult> ExecuteAsync(ToolCall toolCall, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        _logger?.LogDebug("Executing tool {Name} (id: {Id})", toolCall.Name, toolCall.Id);

        ToolResult result;

        if (!_discovery.TryGetTool(toolCall.Name, out var descriptor) || descriptor is null)
        {
            _logger?.LogWarning("Unknown tool requested: {Name}", toolCall.Name);
            result = ToolResult.Error($"Unknown tool: {toolCall.Name}");
        }
        else
        {
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(_defaultTimeout);

                result = await descriptor.Instance.ExecuteAsync(toolCall.Arguments, timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _logger?.LogWarning("Tool {Name} timed out after {Timeout}s", toolCall.Name, _defaultTimeout.TotalSeconds);
                result = ToolResult.Timeout($"Exceeded {_defaultTimeout.TotalSeconds}s timeout");
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Tool {Name} threw exception", toolCall.Name);
                result = ToolResult.Error(ex.Message);
            }
        }

        sw.Stop();

        // Enrich result with metadata
        var enrichedResult = result with
        {
            ExecutionDuration = sw.Elapsed,
            ToolName = toolCall.Name,
            InputParameters = toolCall.Arguments.ValueKind == JsonValueKind.Object
                ? toolCall.Arguments.Clone()
                : null
        };

        _logger?.LogInformation(
            "Tool {Name} completed: {Status} in {Duration:F1}ms",
            toolCall.Name,
            enrichedResult.Status,
            enrichedResult.ExecutionDuration?.TotalMilliseconds ?? 0);

        return new ToolCallResult
        {
            ToolCallId = toolCall.Id,
            ToolName = toolCall.Name,
            Result = enrichedResult
        };
    }

    /// <summary>
    /// Execute multiple tool calls (potentially in parallel).
    /// </summary>
    public async Task<IReadOnlyList<ToolCallResult>> ExecuteAsync(
        IEnumerable<ToolCall> toolCalls,
        bool parallel = true,
        CancellationToken ct = default)
    {
        var calls = toolCalls.ToList();

        if (calls.Count == 0)
            return [];

        if (calls.Count == 1 || !parallel)
        {
            var results = new List<ToolCallResult>();
            foreach (var call in calls)
            {
                results.Add(await ExecuteAsync(call, ct));
            }
            return results;
        }

        // Execute in parallel
        var tasks = calls.Select(call => ExecuteAsync(call, ct));
        return await Task.WhenAll(tasks);
    }
}
