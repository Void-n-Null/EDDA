namespace EDDA.Server.Models;

/// <summary>
/// A memory entry stored in the vector database.
/// Represents a single conversation turn or fact to remember.
/// </summary>
public record MemoryEntry
{
    /// <summary>
    /// Unique identifier for this memory.
    /// </summary>
    public required Guid Id { get; init; }
    
    /// <summary>
    /// The text content of the memory (what to remember).
    /// </summary>
    public required string Content { get; init; }
    
    /// <summary>
    /// When this memory was created.
    /// </summary>
    public required DateTime CreatedAt { get; init; }
    
    /// <summary>
    /// Type of memory (e.g., "user_message", "assistant_message", "fact", "summary").
    /// </summary>
    public required string Type { get; init; }
    
    /// <summary>
    /// Optional conversation ID this memory belongs to.
    /// </summary>
    public string? ConversationId { get; init; }
    
    /// <summary>
    /// Optional session ID (voice session) this memory belongs to.
    /// </summary>
    public string? SessionId { get; init; }
    
    /// <summary>
    /// Optional additional metadata as key-value pairs.
    /// </summary>
    public Dictionary<string, string>? Metadata { get; init; }
    
    /// <summary>
    /// Create a user message memory.
    /// </summary>
    public static MemoryEntry UserMessage(string content, string? conversationId = null)
        => new()
        {
            Id = Guid.NewGuid(),
            Content = content,
            CreatedAt = DateTime.UtcNow,
            Type = "user_message",
            ConversationId = conversationId
        };
    
    /// <summary>
    /// Create an assistant message memory.
    /// </summary>
    public static MemoryEntry AssistantMessage(string content, string? conversationId = null)
        => new()
        {
            Id = Guid.NewGuid(),
            Content = content,
            CreatedAt = DateTime.UtcNow,
            Type = "assistant_message",
            ConversationId = conversationId
        };
    
    /// <summary>
    /// Create a fact/knowledge memory.
    /// </summary>
    public static MemoryEntry Fact(string content)
        => new()
        {
            Id = Guid.NewGuid(),
            Content = content,
            CreatedAt = DateTime.UtcNow,
            Type = "fact"
        };
    
    /// <summary>
    /// Create a conversation summary memory.
    /// </summary>
    public static MemoryEntry Summary(string content, string conversationId)
        => new()
        {
            Id = Guid.NewGuid(),
            Content = content,
            CreatedAt = DateTime.UtcNow,
            Type = "summary",
            ConversationId = conversationId
        };
}

/// <summary>
/// Result from a memory search operation.
/// </summary>
public record MemorySearchResult
{
    /// <summary>
    /// The memory entry that matched.
    /// </summary>
    public required MemoryEntry Memory { get; init; }
    
    /// <summary>
    /// Cosine similarity score (0-1, higher = more similar).
    /// </summary>
    public required float Score { get; init; }
    
    /// <summary>
    /// Age of this memory in seconds from search time.
    /// Used for time-decay calculations.
    /// </summary>
    public double AgeSeconds => (DateTime.UtcNow - Memory.CreatedAt).TotalSeconds;
}

/// <summary>
/// Configuration for time-decay search.
/// </summary>
public record TimeDecaySearchOptions
{
    /// <summary>
    /// Number of initial candidates to retrieve (oversample).
    /// Default: 50
    /// </summary>
    public int OversampleCount { get; init; } = 50;
    
    /// <summary>
    /// Weight factor for recency (0-1). 
    /// 0 = pure semantic, 1 = heavily favor recent.
    /// Default: 0.3 (30% recency, 70% semantic)
    /// </summary>
    public float RecencyWeight { get; init; } = 0.3f;
    
    /// <summary>
    /// Half-life in hours for time decay.
    /// After this many hours, recency contribution is halved.
    /// Default: 24 hours
    /// </summary>
    public float HalfLifeHours { get; init; } = 24f;
    
    /// <summary>
    /// Final number of results to return after re-ranking.
    /// Default: 10
    /// </summary>
    public int FinalCount { get; init; } = 10;
}
