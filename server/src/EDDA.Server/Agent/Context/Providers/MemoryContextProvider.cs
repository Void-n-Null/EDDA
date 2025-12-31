using EDDA.Server.Models;
using EDDA.Server.Services.Memory;
using Microsoft.Extensions.Logging;

namespace EDDA.Server.Agent.Context.Providers;

/// <summary>
/// Provides relevant memories from past conversations via RAG.
/// 
/// Uses Qdrant vector database with time-decay search:
/// 1. Embeds the user's message
/// 2. Searches for semantically similar past exchanges  
/// 3. Reranks by time decay (recent memories weighted higher)
/// 4. Formats top results for context injection
/// </summary>
public class MemoryContextProvider : IContextProvider
{
    private readonly IConversationMemory _memory;
    private readonly ILogger<MemoryContextProvider>? _logger;
    
    // Search configuration
    private readonly TimeDecaySearchOptions _searchOptions = new()
    {
        OversampleCount = 30,    // Get 30 candidates
        RecencyWeight = 0.3f,    // 30% recency, 70% semantic
        HalfLifeHours = 72f,     // 3-day half-life for recency
        FinalCount = 5           // Return top 5 for context
    };
    
    public string Key => "memory_context";
    public int Priority => 100; // After session context

    public MemoryContextProvider(
        IConversationMemory memory,
        ILogger<MemoryContextProvider>? logger = null)
    {
        _memory = memory;
        _logger = logger;
    }

    public async Task<string?> GetContextAsync(ContextRequest request, CancellationToken ct = default)
    {
        // Need a user message to search against
        if (string.IsNullOrWhiteSpace(request.UserMessage))
        {
            _logger?.LogDebug("MEMORY CONTEXT: No user message, skipping");
            return null;
        }
        
        // Don't search if memory service isn't ready
        if (!_memory.IsInitialized)
        {
            _logger?.LogInformation("MEMORY CONTEXT: Service not initialized, skipping");
            return null;
        }
        
        _logger?.LogInformation(
            "MEMORY CONTEXT: Searching for context related to: \"{Query}\"",
            request.UserMessage.Length > 50 
                ? request.UserMessage[..50] + "..." 
                : request.UserMessage);
        
        try
        {
            // Filter to exchange type (combined user+assistant pairs)
            var filter = new MemoryFilter
            {
                Types = ["exchange"]
            };
            
            var results = await _memory.SearchWithTimeDecayAsync(
                request.UserMessage,
                _searchOptions,
                filter,
                ct);
            
            if (results.Count == 0)
            {
                _logger?.LogInformation("MEMORY CONTEXT: No relevant memories found");
                return null;
            }
            
            _logger?.LogInformation(
                "MEMORY CONTEXT: Found {Count} relevant memories, injecting into prompt",
                results.Count);
            
            var formatted = FormatMemories(results);
            _logger?.LogDebug("MEMORY CONTEXT: Formatted output:\n{Context}", formatted);
            
            return formatted;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "MEMORY CONTEXT: Search failed, continuing without context");
            return null;
        }
    }
    
    private static string FormatMemories(IReadOnlyList<MemorySearchResult> results)
    {
        var lines = new List<string> { "## Relevant Past Conversations" };
        
        foreach (var result in results)
        {
            var memory = result.Memory;
            var dateStr = memory.CreatedAt.ToString("MMM d");
            
            // For "exchange" type, content is "User: ...\nAssistant: ..."
            // Just show a condensed version
            var content = memory.Content;
            
            // Truncate content if too long
            if (content.Length > 200)
                content = content[..197] + "...";
            
            // Clean up for display - keep newlines for exchange format
            content = content.Replace("\r", "").Trim();
            
            lines.Add($"- **{dateStr}**: {content}");
        }
        
        return string.Join("\n", lines);
    }
}
