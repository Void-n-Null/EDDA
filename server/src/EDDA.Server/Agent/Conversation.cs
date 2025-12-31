using System.Diagnostics;
using EDDA.Server.Models;
using EDDA.Server.Services.Llm;
using EDDA.Server.Services.Memory;
using Microsoft.Extensions.Logging;

namespace EDDA.Server.Agent;

/// <summary>
/// Represents a single conversation with the agent (one activation → deactivation cycle).
/// 
/// Lifecycle:
/// - Created when VoiceSession.Activate() is called
/// - Messages accumulate as user/assistant exchange
/// - Disposed when VoiceSession.Deactivate() is called
/// 
/// RAG Integration:
/// On dispose/deactivate, message pairs (User + Assistant) are stored in Qdrant
/// as combined memory entries. This captures the full context of each exchange.
/// 
/// Storage format:
/// - Each user/assistant pair becomes one memory entry
/// - Content: "User: {question}\nAssistant: {response}"
/// - Type: "exchange"
/// - ConversationId: links all pairs from same conversation
/// </summary>
public sealed class Conversation : IDisposable
{
    private readonly List<ChatMessage> _messages = [];
    private readonly Stopwatch _duration = Stopwatch.StartNew();
    private readonly IConversationMemory? _memory;
    private readonly ILogger? _logger;
    private bool _disposed;

    /// <summary>
    /// Unique identifier for this conversation.
    /// </summary>
    public Guid Id { get; } = Guid.NewGuid();

    /// <summary>
    /// When this conversation started.
    /// </summary>
    public DateTime StartedAt { get; } = DateTime.UtcNow;

    /// <summary>
    /// All messages in this conversation (system, user, assistant, tool).
    /// </summary>
    public IReadOnlyList<ChatMessage> Messages => _messages;

    /// <summary>
    /// How long this conversation has been active.
    /// </summary>
    public TimeSpan Duration => _duration.Elapsed;

    /// <summary>
    /// Number of user turns (exchanges) in this conversation.
    /// </summary>
    public int TurnCount => _messages.Count(m => m.Role == "user");

    /// <summary>
    /// Token usage tracking for this conversation.
    /// </summary>
    public TokenUsage? CumulativeUsage { get; private set; }

    /// <summary>
    /// Whether the system prompt has been set.
    /// </summary>
    public bool HasSystemPrompt => _messages.Count > 0 && _messages[0].Role == "system";

    /// <summary>
    /// Create a conversation with optional memory persistence.
    /// </summary>
    /// <param name="memory">Memory service for persisting exchanges on dispose.</param>
    /// <param name="logger">Optional logger.</param>
    public Conversation(IConversationMemory? memory = null, ILogger? logger = null)
    {
        _memory = memory;
        _logger = logger;
    }

    /// <summary>
    /// Add the system prompt. Should only be called once at conversation start.
    /// </summary>
    public void SetSystemPrompt(string systemPrompt)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (HasSystemPrompt)
            throw new InvalidOperationException("System prompt already set");

        _messages.Insert(0, ChatMessage.System(systemPrompt));
    }

    /// <summary>
    /// Add a user message to the conversation.
    /// </summary>
    public void AddUserMessage(string content)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _messages.Add(ChatMessage.User(content));
    }

    /// <summary>
    /// Add an assistant response to the conversation.
    /// </summary>
    public void AddAssistantMessage(string content, TokenUsage? usage = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _messages.Add(ChatMessage.Assistant(content));

        if (usage != null)
            AccumulateUsage(usage);
    }

    /// <summary>
    /// Add assistant message with tool calls.
    /// </summary>
    public void AddAssistantToolCalls(
        IReadOnlyList<ToolCall> toolCalls, 
        IReadOnlyList<ReasoningDetail>? reasoningDetails = null,
        TokenUsage? usage = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _messages.Add(ChatMessage.AssistantWithToolCalls(toolCalls, reasoningDetails));

        if (usage != null)
            AccumulateUsage(usage);
    }

    /// <summary>
    /// Add a tool result to the conversation.
    /// </summary>
    public void AddToolResult(string toolCallId, string content)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _messages.Add(ChatMessage.Tool(toolCallId, content));
    }

    private void AccumulateUsage(TokenUsage usage)
    {
        if (CumulativeUsage == null)
        {
            CumulativeUsage = usage;
        }
        else
        {
            CumulativeUsage = new TokenUsage
            {
                PromptTokens = CumulativeUsage.PromptTokens + usage.PromptTokens,
                CompletionTokens = CumulativeUsage.CompletionTokens + usage.CompletionTokens,
                TotalTokens = CumulativeUsage.TotalTokens + usage.TotalTokens
            };
        }
    }

    /// <summary>
    /// Get user/assistant exchange pairs for memory storage.
    /// Each pair is combined into a single string for embedding.
    /// </summary>
    public IEnumerable<(string UserMessage, string AssistantMessage, DateTime Timestamp)> GetExchangePairs()
    {
        string? pendingUserMessage = null;
        DateTime? userTimestamp = null;
        
        foreach (var msg in _messages)
        {
            if (msg.Role == "user" && !string.IsNullOrEmpty(msg.Content))
            {
                // Start a new pair
                pendingUserMessage = msg.Content;
                userTimestamp = DateTime.UtcNow; // We don't track individual timestamps, use now
            }
            else if (msg.Role == "assistant" && !string.IsNullOrEmpty(msg.Content) && pendingUserMessage != null)
            {
                // Complete the pair
                yield return (pendingUserMessage, msg.Content, userTimestamp ?? DateTime.UtcNow);
                pendingUserMessage = null;
                userTimestamp = null;
            }
        }
    }

    /// <summary>
    /// Get all user and assistant messages (legacy format).
    /// </summary>
    public IEnumerable<(string Role, string Content)> GetExchangesForMemory()
    {
        return _messages
            .Where(m => m.Role is "user" or "assistant" && !string.IsNullOrEmpty(m.Content))
            .Select(m => (m.Role, m.Content!));
    }

    /// <summary>
    /// Get a summary of the conversation for logging.
    /// </summary>
    public string GetSummary()
    {
        return $"Conversation {Id:N}: {TurnCount} turns, {Duration.TotalSeconds:F1}s, {CumulativeUsage?.TotalTokens ?? 0} tokens";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _duration.Stop();

        // Persist exchanges to memory (fire-and-forget, don't block dispose)
        if (_memory is { IsInitialized: true })
        {
            _ = PersistExchangesAsync();
        }
    }

    private async Task PersistExchangesAsync()
    {
        _logger?.LogInformation(
            "PERSIST: Starting memory persistence for conversation {Id} ({Turns} turns)",
            Id.ToString()[..8], TurnCount);
        
        try
        {
            var pairs = GetExchangePairs().ToList();
            
            if (pairs.Count == 0)
            {
                _logger?.LogInformation(
                    "PERSIST: No complete user/assistant pairs to persist (conversation {Id})",
                    Id.ToString()[..8]);
                return;
            }
            
            _logger?.LogInformation(
                "PERSIST: Found {Count} exchange pairs to store", pairs.Count);

            var entries = pairs.Select((pair, index) => new MemoryEntry
            {
                Id = Guid.NewGuid(),
                // Combined format: captures full exchange context
                Content = $"User: {pair.UserMessage}\nAssistant: {pair.AssistantMessage}",
                CreatedAt = StartedAt.AddSeconds(index), // Stagger timestamps slightly for ordering
                Type = "exchange",
                ConversationId = Id.ToString(),
                Metadata = new Dictionary<string, string>
                {
                    ["turn_index"] = index.ToString(),
                    ["user_length"] = pair.UserMessage.Length.ToString(),
                    ["assistant_length"] = pair.AssistantMessage.Length.ToString()
                }
            }).ToList();
            
            // Log each entry being persisted
            foreach (var (entry, idx) in entries.Select((e, i) => (e, i)))
            {
                _logger?.LogDebug(
                    "PERSIST: Entry {Index}: \"{Content}\"",
                    idx,
                    entry.Content.Length > 100 ? entry.Content[..100] + "..." : entry.Content);
            }

            await _memory!.AddBatchAsync(entries);
            
            _logger?.LogInformation(
                "PERSIST: Successfully stored {Count} exchanges from conversation {Id}",
                entries.Count, Id.ToString()[..8]);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "PERSIST: Failed to persist conversation {Id} to memory", Id.ToString()[..8]);
        }
    }
}
