namespace EDDA.Server.Agent.Context;

/// <summary>
/// A source of context that can be injected into agent prompts.
/// 
/// Implement this interface to add new context sources (calendar, weather, 
/// home automation, user preferences, etc.). Context providers are automatically
/// discovered and injected into prompts.
/// 
/// Example usage in system.md template:
///   ## Current Context
///   {{time_context}}
///   {{memory_context}}
///   {{calendar_context}}
/// 
/// Providers that return null/empty are silently omitted.
/// </summary>
public interface IContextProvider
{
    /// <summary>
    /// Unique key for this context (used in templates as {{key}}).
    /// </summary>
    string Key { get; }

    /// <summary>
    /// Priority for ordering in prompts (lower = earlier in the prompt).
    /// Suggested ranges:
    /// - 0-49: Core context (time, date, user info)
    /// - 50-99: Session context (current conversation summary)
    /// - 100-149: Memory context (RAG results)
    /// - 150+: External integrations (calendar, weather, etc.)
    /// </summary>
    int Priority { get; }

    /// <summary>
    /// Generate context string for the current request.
    /// Return null or empty to omit this context from the prompt.
    /// 
    /// Implementations should be fast — this runs on every user message.
    /// For expensive operations (API calls, embeddings), consider caching.
    /// </summary>
    /// <param name="request">Request context including user message and conversation.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Markdown-formatted context string, or null to omit.</returns>
    Task<string?> GetContextAsync(ContextRequest request, CancellationToken ct = default);
}

/// <summary>
/// Request context passed to context providers.
/// Contains everything a provider might need to generate relevant context.
/// </summary>
public record ContextRequest
{
    /// <summary>
    /// Current time (for time-based context).
    /// </summary>
    public DateTime Now { get; init; } = DateTime.Now;

    /// <summary>
    /// The user's message that triggered this request.
    /// Useful for semantic search in RAG providers.
    /// </summary>
    public string? UserMessage { get; init; }

    /// <summary>
    /// The current conversation (for session-aware context).
    /// May be null for the first message.
    /// </summary>
    public Conversation? Conversation { get; init; }
}
