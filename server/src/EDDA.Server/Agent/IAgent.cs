namespace EDDA.Server.Agent;

/// <summary>
/// Core agent interface for processing user messages.
/// </summary>
public interface IAgent
{
    /// <summary>
    /// Process a user message and stream sentences as they become ready.
    /// Handles tool calling internally — may pause output while tools execute.
    /// </summary>
    /// <param name="conversation">The current conversation state.</param>
    /// <param name="userMessage">The user's transcribed speech.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Stream of agent output chunks (sentences, tool status, completion).</returns>
    IAsyncEnumerable<AgentChunk> ProcessStreamAsync(
        Conversation conversation,
        string userMessage,
        CancellationToken ct = default);
}

/// <summary>
/// A chunk of agent output — either text ready for TTS, or a status update.
/// </summary>
public record AgentChunk
{
    public required AgentChunkType Type { get; init; }

    /// <summary>Text content (for Sentence type).</summary>
    public string? Text { get; init; }

    /// <summary>Tool that's executing (for ToolExecuting type).</summary>
    public string? ToolName { get; init; }

    public static AgentChunk Sentence(string text) =>
        new() { Type = AgentChunkType.Sentence, Text = text };

    public static AgentChunk ToolExecuting(string toolName) =>
        new() { Type = AgentChunkType.ToolExecuting, ToolName = toolName };

    public static AgentChunk Complete() =>
        new() { Type = AgentChunkType.Complete };
}

/// <summary>
/// Types of chunks the agent can emit during streaming.
/// </summary>
public enum AgentChunkType
{
    /// <summary>A complete sentence ready for TTS.</summary>
    Sentence,

    /// <summary>Tool is being executed (might want to play a "thinking" sound).</summary>
    ToolExecuting,

    /// <summary>Response complete.</summary>
    Complete
}
