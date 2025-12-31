namespace EDDA.Server.Agent.Context.Providers;

/// <summary>
/// Provides context about the current conversation session.
/// Helps the agent understand where it is in a multi-turn conversation.
/// Only activates after a few turns to avoid clutter on first messages.
/// </summary>
public class ConversationContextProvider : IContextProvider
{
    public string Key => "conversation_context";
    public int Priority => 50; // After core context, before memory

    /// <summary>
    /// Minimum turns before including conversation context.
    /// First few turns don't need this overhead.
    /// </summary>
    private const int MinTurnsForContext = 3;

    public Task<string?> GetContextAsync(ContextRequest request, CancellationToken ct = default)
    {
        var conv = request.Conversation;

        if (conv == null || conv.TurnCount < MinTurnsForContext)
            return Task.FromResult<string?>(null);

        var durationMinutes = (int)conv.Duration.TotalMinutes;
        var durationText = durationMinutes switch
        {
            0 => "just started",
            1 => "1 minute ago",
            _ => $"{durationMinutes} minutes ago"
        };

        var context = $"""
            - **Session**: Turn {conv.TurnCount} (started {durationText})
            """;

        return Task.FromResult<string?>(context);
    }
}
