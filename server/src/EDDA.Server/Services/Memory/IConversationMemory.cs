using EDDA.Server.Models;

namespace EDDA.Server.Services.Memory;

/// <summary>
/// Service for storing and retrieving conversation memories from a vector database.
/// </summary>
public interface IConversationMemory
{
    /// <summary>
    /// Whether the service is initialized and ready.
    /// </summary>
    bool IsInitialized { get; }
    
    /// <summary>
    /// Initialize the memory service (creates collection if needed).
    /// </summary>
    Task InitializeAsync();
    
    /// <summary>
    /// Add a single memory entry.
    /// </summary>
    Task AddAsync(MemoryEntry entry, CancellationToken ct = default);
    
    /// <summary>
    /// Add multiple memory entries in batch.
    /// </summary>
    Task AddBatchAsync(IEnumerable<MemoryEntry> entries, CancellationToken ct = default);
    
    /// <summary>
    /// Pure semantic search - retrieve memories most similar to the query.
    /// </summary>
    /// <param name="query">The text to search for.</param>
    /// <param name="limit">Maximum number of results.</param>
    /// <param name="filter">Optional filter (e.g., by type or conversation).</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<MemorySearchResult>> SearchAsync(
        string query,
        int limit = 10,
        MemoryFilter? filter = null,
        CancellationToken ct = default);
    
    /// <summary>
    /// Time-decay search - combines semantic similarity with recency.
    /// Oversamples candidates, applies time decay formula, returns top results.
    /// </summary>
    /// <param name="query">The text to search for.</param>
    /// <param name="options">Time decay search configuration.</param>
    /// <param name="filter">Optional filter (e.g., by type or conversation).</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<MemorySearchResult>> SearchWithTimeDecayAsync(
        string query,
        TimeDecaySearchOptions? options = null,
        MemoryFilter? filter = null,
        CancellationToken ct = default);
    
    /// <summary>
    /// Delete memories by ID.
    /// </summary>
    Task DeleteAsync(IEnumerable<Guid> ids, CancellationToken ct = default);
    
    /// <summary>
    /// Delete all memories for a conversation.
    /// </summary>
    Task DeleteConversationAsync(string conversationId, CancellationToken ct = default);
    
    /// <summary>
    /// Get the total count of memories.
    /// </summary>
    Task<long> CountAsync(CancellationToken ct = default);
}

/// <summary>
/// Filter options for memory searches.
/// </summary>
public record MemoryFilter
{
    /// <summary>
    /// Filter to specific memory types.
    /// </summary>
    public IReadOnlyList<string>? Types { get; init; }
    
    /// <summary>
    /// Filter to a specific conversation ID.
    /// </summary>
    public string? ConversationId { get; init; }
    
    /// <summary>
    /// Filter to a specific session ID.
    /// </summary>
    public string? SessionId { get; init; }
    
    /// <summary>
    /// Only include memories created after this time.
    /// </summary>
    public DateTime? CreatedAfter { get; init; }
    
    /// <summary>
    /// Only include memories created before this time.
    /// </summary>
    public DateTime? CreatedBefore { get; init; }
}
