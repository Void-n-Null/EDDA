using System.Text.Json;

namespace EDDA.Server.Services.Llm;

/// <summary>
/// A message in a conversation.
/// </summary>
public record ChatMessage
{
    /// <summary>Role: "system", "user", "assistant", or "tool".</summary>
    public required string Role { get; init; }

    /// <summary>Message content.</summary>
    public string? Content { get; init; }

    /// <summary>Tool calls made by assistant (when role is "assistant").</summary>
    public IReadOnlyList<ToolCall>? ToolCalls { get; init; }

    /// <summary>Tool call ID this message is responding to (when role is "tool").</summary>
    public string? ToolCallId { get; init; }

    public static ChatMessage System(string content) =>
        new() { Role = "system", Content = content };

    public static ChatMessage User(string content) =>
        new() { Role = "user", Content = content };

    public static ChatMessage Assistant(string content) =>
        new() { Role = "assistant", Content = content };

    public static ChatMessage AssistantWithToolCalls(IReadOnlyList<ToolCall> toolCalls) =>
        new() { Role = "assistant", ToolCalls = toolCalls };

    public static ChatMessage Tool(string toolCallId, string content) =>
        new() { Role = "tool", ToolCallId = toolCallId, Content = content };
}

/// <summary>
/// Options for a chat completion request.
/// </summary>
public record ChatOptions
{
    /// <summary>Model to use (overrides default).</summary>
    public string? Model { get; init; }

    /// <summary>Maximum tokens to generate.</summary>
    public int? MaxTokens { get; init; }

    /// <summary>Temperature (0-2).</summary>
    public float? Temperature { get; init; }

    /// <summary>Whether to stream the response.</summary>
    public bool? Stream { get; init; }

    /// <summary>Tool definitions to make available.</summary>
    public IEnumerable<object>? Tools { get; init; }

    /// <summary>
    /// Tool choice: "auto", "none", or specific tool.
    /// </summary>
    public object? ToolChoice { get; init; }
}

/// <summary>
/// Result of a chat completion.
/// </summary>
public record ChatResult
{
    /// <summary>Generated content (may be null if tool calls were made).</summary>
    public string? Content { get; init; }

    /// <summary>Tool calls requested by the model.</summary>
    public IReadOnlyList<ToolCall>? ToolCalls { get; init; }

    /// <summary>Finish reason: "stop", "tool_calls", "length", etc.</summary>
    public string? FinishReason { get; init; }

    /// <summary>Model that was used.</summary>
    public string? Model { get; init; }

    /// <summary>Token usage statistics.</summary>
    public TokenUsage? Usage { get; init; }

    /// <summary>Whether the model requested tool calls.</summary>
    public bool HasToolCalls => ToolCalls is { Count: > 0 };
}

/// <summary>
/// Token usage statistics.
/// </summary>
public record TokenUsage
{
    public int PromptTokens { get; init; }
    public int CompletionTokens { get; init; }
    public int TotalTokens { get; init; }
}

/// <summary>
/// A chunk from a streaming response.
/// </summary>
public record StreamChunk
{
    /// <summary>Content delta (partial content).</summary>
    public string? ContentDelta { get; init; }

    /// <summary>Tool call deltas.</summary>
    public IReadOnlyList<ToolCallDelta>? ToolCallDeltas { get; init; }

    /// <summary>Finish reason (only on final chunk).</summary>
    public string? FinishReason { get; init; }

    /// <summary>Whether this is the final chunk.</summary>
    public bool IsFinal => FinishReason is not null;
}

/// <summary>
/// Partial tool call information from streaming.
/// </summary>
public record ToolCallDelta
{
    public int Index { get; init; }
    public string? Id { get; init; }
    public string? Name { get; init; }
    public string? ArgumentsDelta { get; init; }
}

/// <summary>
/// Service for interacting with OpenRouter's LLM API.
/// </summary>
public interface IOpenRouterService
{
    /// <summary>
    /// Whether the service is initialized and ready.
    /// </summary>
    bool IsInitialized { get; }

    /// <summary>
    /// Initialize the service (validates API key, etc.).
    /// </summary>
    Task InitializeAsync();

    /// <summary>
    /// Send a chat completion request.
    /// </summary>
    Task<ChatResult> ChatAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken ct = default);

    /// <summary>
    /// Send a chat completion request with streaming response.
    /// </summary>
    IAsyncEnumerable<StreamChunk> ChatStreamAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken ct = default);

    /// <summary>
    /// Simple single-turn completion (convenience method).
    /// </summary>
    Task<string> CompleteAsync(
        string prompt,
        string? systemPrompt = null,
        ChatOptions? options = null,
        CancellationToken ct = default);

    /// <summary>
    /// Execute a chat completion with automatic tool calling.
    /// Handles the tool call loop until the model produces a final response.
    /// </summary>
    Task<ChatResult> ChatWithToolsAsync(
        IEnumerable<ChatMessage> messages,
        ToolDiscovery tools,
        ChatOptions? options = null,
        int maxToolRounds = 10,
        CancellationToken ct = default);
}
