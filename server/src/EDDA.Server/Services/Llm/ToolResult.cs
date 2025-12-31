using System.Diagnostics;
using System.Text.Json;

namespace EDDA.Server.Services.Llm;

/// <summary>
/// Status of a tool execution.
/// </summary>
public enum ToolResultStatus
{
    /// <summary>Tool executed successfully.</summary>
    Success,

    /// <summary>Tool encountered an error during execution.</summary>
    Error,

    /// <summary>User or system denied permission to execute.</summary>
    Denied,

    /// <summary>Tool execution timed out.</summary>
    Timeout,

    /// <summary>Tool was rate limited by an external service.</summary>
    RateLimited,

    /// <summary>Tool returned partial results (some operations succeeded, some failed).</summary>
    PartialSuccess,

    /// <summary>Input parameters were invalid or malformed.</summary>
    InvalidInput
}

/// <summary>
/// Result of a tool execution. Contains both the data for the LLM
/// and metadata for logging/debugging.
/// </summary>
public record ToolResult
{
    /// <summary>
    /// Execution status.
    /// </summary>
    public required ToolResultStatus Status { get; init; }

    /// <summary>
    /// Result data (for Success/PartialSuccess) or error details.
    /// </summary>
    public object? Data { get; init; }

    /// <summary>
    /// Human-readable error message (for error states).
    /// </summary>
    public string? ErrorMessage { get; init; }

    // ──────────────────────────────────────────────────────────────────
    // Metadata for logging — NOT sent to LLM
    // ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// How long the tool took to execute.
    /// </summary>
    public TimeSpan? ExecutionDuration { get; init; }

    /// <summary>
    /// The input parameters passed to the tool.
    /// </summary>
    public object? InputParameters { get; init; }

    /// <summary>
    /// Name of the tool that was executed.
    /// </summary>
    public string? ToolName { get; init; }

    /// <summary>
    /// Additional metadata for logging/debugging.
    /// </summary>
    public Dictionary<string, object>? Extra { get; init; }

    // ──────────────────────────────────────────────────────────────────
    // LLM-facing output
    // ──────────────────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false
    };

    /// <summary>
    /// Returns the string representation to send to the LLM.
    /// Only includes the essential result/error, not metadata.
    /// </summary>
    public string ForLlm() => Status switch
    {
        ToolResultStatus.Success => $"[Success]: {(Data is string s ? s : JsonSerializer.Serialize(Data, JsonOptions))}",
        ToolResultStatus.PartialSuccess => $"[Partial success]: {JsonSerializer.Serialize(new { partial = true, data = Data, error = ErrorMessage }, JsonOptions)}",
        ToolResultStatus.Error => $"[Error]: {ErrorMessage ?? "Unknown error"}",
        ToolResultStatus.Denied => "[Permission denied]",
        ToolResultStatus.Timeout => "[Tool execution timed out]",
        ToolResultStatus.RateLimited => $"[Rate limited]{(ErrorMessage is not null ? $": {ErrorMessage}" : "")}",
        ToolResultStatus.InvalidInput => $"[Invalid input]: {ErrorMessage ?? "Unknown validation error"}",
        _ => throw new UnreachableException($"Unknown status: {Status}")
    };

    // ──────────────────────────────────────────────────────────────────
    // Factory methods
    // ──────────────────────────────────────────────────────────────────

    public static ToolResult Success(object data) =>
        new() { Status = ToolResultStatus.Success, Data = data };

    public static ToolResult Error(string message) =>
        new() { Status = ToolResultStatus.Error, ErrorMessage = message };

    public static ToolResult Denied(string? reason = null) =>
        new() { Status = ToolResultStatus.Denied, ErrorMessage = reason };

    public static ToolResult Timeout(string? details = null) =>
        new() { Status = ToolResultStatus.Timeout, ErrorMessage = details };

    public static ToolResult RateLimited(string? retryAfter = null) =>
        new() { Status = ToolResultStatus.RateLimited, ErrorMessage = retryAfter };

    public static ToolResult PartialSuccess(object data, string errorDetails) =>
        new() { Status = ToolResultStatus.PartialSuccess, Data = data, ErrorMessage = errorDetails };

    public static ToolResult InvalidInput(string message) =>
        new() { Status = ToolResultStatus.InvalidInput, ErrorMessage = message };
}
