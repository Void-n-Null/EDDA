using EDDA.Server.Models;
using Microsoft.Extensions.Logging;
using Qdrant.Client;
using Qdrant.Client.Grpc;

namespace EDDA.Server.Services.Memory;

/// <summary>
/// Qdrant-backed conversation memory service.
/// Runs on the basement server alongside the C# server.
/// </summary>
public class QdrantMemoryService : IConversationMemory, IDisposable
{
    private readonly QdrantClient _client;
    private readonly IEmbeddingService _embeddings;
    private readonly ILogger<QdrantMemoryService>? _logger;
    private readonly string _collectionName;
    
    // Payload field names
    private const string FieldContent = "content";
    private const string FieldType = "type";
    private const string FieldCreatedAt = "created_at";
    private const string FieldConversationId = "conversation_id";
    private const string FieldSessionId = "session_id";
    private const string FieldMetadata = "metadata";

    public bool IsInitialized { get; private set; }

    public QdrantMemoryService(
        IEmbeddingService embeddings,
        ILogger<QdrantMemoryService>? logger = null,
        string? host = null,
        int port = 6334,
        string collectionName = "edda_memories")
    {
        _embeddings = embeddings;
        _logger = logger;
        _collectionName = collectionName;
        
        // Connect to Qdrant on localhost (running in Docker on same server)
        // Uses gRPC port 6334 for better performance
        var qdrantHost = host ?? "localhost";
        _client = new QdrantClient(qdrantHost, port);
        
        _logger?.LogInformation("Qdrant client created for {Host}:{Port}", qdrantHost, port);
    }

    public async Task InitializeAsync()
    {
        _logger?.LogInformation("Initializing Qdrant memory service...");
        
        try
        {
            // Check if collection exists
            var collections = await _client.ListCollectionsAsync();
            var exists = collections.Any(c => c == _collectionName);
            
            if (!exists)
            {
                _logger?.LogInformation("Creating collection '{Collection}' with {Dims} dimensions...",
                    _collectionName, _embeddings.Dimensions);
                
                await _client.CreateCollectionAsync(
                    _collectionName,
                    new VectorParams
                    {
                        Size = (ulong)_embeddings.Dimensions,
                        Distance = Distance.Cosine
                    });
                
                // Create payload indexes for filtering
                await _client.CreatePayloadIndexAsync(
                    _collectionName, 
                    FieldType, 
                    PayloadSchemaType.Keyword);
                
                await _client.CreatePayloadIndexAsync(
                    _collectionName, 
                    FieldConversationId, 
                    PayloadSchemaType.Keyword);
                
                await _client.CreatePayloadIndexAsync(
                    _collectionName, 
                    FieldCreatedAt, 
                    PayloadSchemaType.Integer);
                
                _logger?.LogInformation("Collection '{Collection}' created with indexes", _collectionName);
            }
            else
            {
                var info = await _client.GetCollectionInfoAsync(_collectionName);
                _logger?.LogInformation(
                    "Collection '{Collection}' exists ({Count} points)", 
                    _collectionName, 
                    info.PointsCount);
            }
            
            IsInitialized = true;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to initialize Qdrant memory service");
            IsInitialized = false;
        }
    }

    public async Task AddAsync(MemoryEntry entry, CancellationToken ct = default)
    {
        await AddBatchAsync([entry], ct);
    }

    public async Task AddBatchAsync(IEnumerable<MemoryEntry> entries, CancellationToken ct = default)
    {
        var entryList = entries.ToList();
        
        if (entryList.Count == 0)
            return;
        
        _logger?.LogInformation(
            "QDRANT: Adding {Count} memories (types: {Types})",
            entryList.Count,
            string.Join(", ", entryList.Select(e => e.Type).Distinct()));
        
        // Generate embeddings for all entries
        var texts = entryList.Select(e => e.Content).ToList();
        
        _logger?.LogDebug("QDRANT: Generating embeddings for {Count} texts...", texts.Count);
        var embeddings = await _embeddings.EmbedBatchAsync(texts, ct);
        _logger?.LogDebug("QDRANT: Embeddings generated ({Dims}d each)", embeddings[0].Length);
        
        // Build points
        var points = new List<PointStruct>();
        
        for (int i = 0; i < entryList.Count; i++)
        {
            var entry = entryList[i];
            var vector = embeddings[i];
            
            var payload = new Dictionary<string, Value>
            {
                [FieldContent] = entry.Content,
                [FieldType] = entry.Type,
                [FieldCreatedAt] = entry.CreatedAt.Ticks
            };
            
            if (entry.ConversationId is not null)
                payload[FieldConversationId] = entry.ConversationId;
            
            if (entry.SessionId is not null)
                payload[FieldSessionId] = entry.SessionId;
            
            if (entry.Metadata is { Count: > 0 })
            {
                // Store metadata as JSON-like structure
                var metaStruct = new Struct();
                foreach (var (key, value) in entry.Metadata)
                {
                    metaStruct.Fields[key] = new Value { StringValue = value };
                }
                payload[FieldMetadata] = new Value { StructValue = metaStruct };
            }
            
            points.Add(new PointStruct
            {
                Id = new PointId { Uuid = entry.Id.ToString() },
                Vectors = vector,
                Payload = { payload }
            });
            
            // Log each entry being added
            _logger?.LogDebug(
                "QDRANT: Point {Index}: id={Id}, type={Type}, content=\"{Content}\"",
                i, entry.Id.ToString()[..8], entry.Type,
                entry.Content.Length > 60 ? entry.Content[..60] + "..." : entry.Content);
        }
        
        await _client.UpsertAsync(_collectionName, points, cancellationToken: ct);
        
        _logger?.LogInformation("QDRANT: Successfully stored {Count} memories", entryList.Count);
    }

    public async Task<IReadOnlyList<MemorySearchResult>> SearchAsync(
        string query,
        int limit = 10,
        MemoryFilter? filter = null,
        CancellationToken ct = default)
    {
        _logger?.LogInformation(
            "QDRANT SEARCH: \"{Query}\" (limit: {Limit})", 
            query.Length > 50 ? query[..50] + "..." : query, limit);
        
        // Generate query embedding
        _logger?.LogDebug("QDRANT SEARCH: Generating query embedding...");
        var queryVector = await _embeddings.EmbedAsync(query, ct);
        
        // Build filter
        var qdrantFilter = BuildFilter(filter);
        if (qdrantFilter != null)
        {
            _logger?.LogDebug("QDRANT SEARCH: Filter applied");
        }
        
        // Search
        var results = await _client.SearchAsync(
            _collectionName,
            queryVector,
            filter: qdrantFilter,
            limit: (ulong)limit,
            cancellationToken: ct);
        
        var parsed = results.Select(ParseSearchResult).ToList();
        
        _logger?.LogInformation(
            "QDRANT SEARCH: Found {Count} results (top score: {Score:F3})",
            parsed.Count,
            parsed.Count > 0 ? parsed[0].Score : 0);
        
        // Log top results at debug level
        foreach (var (result, idx) in parsed.Take(3).Select((r, i) => (r, i)))
        {
            _logger?.LogDebug(
                "QDRANT RESULT [{Index}]: score={Score:F3}, type={Type}, content=\"{Content}\"",
                idx, result.Score, result.Memory.Type,
                result.Memory.Content.Length > 60 
                    ? result.Memory.Content[..60] + "..." 
                    : result.Memory.Content);
        }
        
        return parsed;
    }

    public async Task<IReadOnlyList<MemorySearchResult>> SearchWithTimeDecayAsync(
        string query,
        TimeDecaySearchOptions? options = null,
        MemoryFilter? filter = null,
        CancellationToken ct = default)
    {
        options ??= new TimeDecaySearchOptions();
        
        _logger?.LogInformation(
            "QDRANT TIME-DECAY: \"{Query}\" (oversample: {Over}, recency: {Rec}%, final: {Final})",
            query.Length > 40 ? query[..40] + "..." : query,
            options.OversampleCount,
            (int)(options.RecencyWeight * 100),
            options.FinalCount);
        
        // Step 1: Oversample - get more candidates than we need
        var candidates = await SearchAsync(query, options.OversampleCount, filter, ct);
        
        if (candidates.Count == 0)
        {
            _logger?.LogInformation("QDRANT TIME-DECAY: No candidates found");
            return candidates;
        }
        
        // Step 2: Apply time-decay re-ranking
        var now = DateTime.UtcNow;
        var halfLifeSeconds = options.HalfLifeHours * 3600;
        var semanticWeight = 1f - options.RecencyWeight;
        
        var reranked = candidates
            .Select(result =>
            {
                var ageSeconds = (now - result.Memory.CreatedAt).TotalSeconds;
                
                // Exponential decay: recency_score = 2^(-age / half_life)
                // Score of 1.0 at age=0, 0.5 at age=half_life, 0.25 at age=2*half_life, etc.
                var recencyScore = (float)Math.Pow(2, -ageSeconds / halfLifeSeconds);
                
                // Combined score: weighted average of semantic and recency
                var combinedScore = (semanticWeight * result.Score) + (options.RecencyWeight * recencyScore);
                
                _logger?.LogDebug(
                    "QDRANT RERANK: sem={Sem:F3} + rec={Rec:F3} (age={Age:F0}s) = {Combined:F3}",
                    result.Score, recencyScore, ageSeconds, combinedScore);
                
                return (result, combinedScore);
            })
            .OrderByDescending(x => x.combinedScore)
            .Take(options.FinalCount)
            .Select(x => x.result with { Score = x.combinedScore })
            .ToList();
        
        _logger?.LogInformation(
            "QDRANT TIME-DECAY: {Candidates} candidates -> {Final} results (top score: {Score:F3})",
            candidates.Count, reranked.Count,
            reranked.Count > 0 ? reranked[0].Score : 0);
        
        return reranked;
    }

    public async Task DeleteAsync(IEnumerable<Guid> ids, CancellationToken ct = default)
    {
        var idList = ids.ToList();
        
        if (idList.Count == 0)
            return;
        
        _logger?.LogDebug("Deleting {Count} memories...", idList.Count);
        
        // Delete using IEnumerable<Guid> overload
        await _client.DeleteAsync(_collectionName, idList, cancellationToken: ct);
    }

    public async Task DeleteConversationAsync(string conversationId, CancellationToken ct = default)
    {
        _logger?.LogDebug("Deleting all memories for conversation: {ConvId}", conversationId);
        
        var filter = new Filter
        {
            Must =
            {
                new Condition
                {
                    Field = new FieldCondition
                    {
                        Key = FieldConversationId,
                        Match = new Match { Keyword = conversationId }
                    }
                }
            }
        };
        
        await _client.DeleteAsync(_collectionName, filter, cancellationToken: ct);
    }

    public async Task<long> CountAsync(CancellationToken ct = default)
    {
        var info = await _client.GetCollectionInfoAsync(_collectionName, ct);
        return (long)info.PointsCount;
    }

    private static Filter? BuildFilter(MemoryFilter? filter)
    {
        if (filter is null)
            return null;
        
        var conditions = new List<Condition>();
        
        if (filter.Types is { Count: > 0 })
        {
            if (filter.Types.Count == 1)
            {
                conditions.Add(new Condition
                {
                    Field = new FieldCondition
                    {
                        Key = FieldType,
                        Match = new Match { Keyword = filter.Types[0] }
                    }
                });
            }
            else
            {
                // Multiple types - use Should (OR)
                var typeConditions = filter.Types.Select(t => new Condition
                {
                    Field = new FieldCondition
                    {
                        Key = FieldType,
                        Match = new Match { Keyword = t }
                    }
                }).ToList();
                
                conditions.Add(new Condition
                {
                    Filter = new Filter { Should = { typeConditions } }
                });
            }
        }
        
        if (filter.ConversationId is not null)
        {
            conditions.Add(new Condition
            {
                Field = new FieldCondition
                {
                    Key = FieldConversationId,
                    Match = new Match { Keyword = filter.ConversationId }
                }
            });
        }
        
        if (filter.SessionId is not null)
        {
            conditions.Add(new Condition
            {
                Field = new FieldCondition
                {
                    Key = FieldSessionId,
                    Match = new Match { Keyword = filter.SessionId }
                }
            });
        }
        
        if (filter.CreatedAfter is not null)
        {
            conditions.Add(new Condition
            {
                Field = new FieldCondition
                {
                    Key = FieldCreatedAt,
                    Range = new Qdrant.Client.Grpc.Range { Gte = filter.CreatedAfter.Value.Ticks }
                }
            });
        }
        
        if (filter.CreatedBefore is not null)
        {
            conditions.Add(new Condition
            {
                Field = new FieldCondition
                {
                    Key = FieldCreatedAt,
                    Range = new Qdrant.Client.Grpc.Range { Lte = filter.CreatedBefore.Value.Ticks }
                }
            });
        }
        
        if (conditions.Count == 0)
            return null;
        
        return new Filter { Must = { conditions } };
    }

    private static MemorySearchResult ParseSearchResult(ScoredPoint point)
    {
        var payload = point.Payload;
        
        var entry = new MemoryEntry
        {
            Id = Guid.Parse(point.Id.Uuid),
            Content = payload[FieldContent].StringValue,
            Type = payload[FieldType].StringValue,
            CreatedAt = new DateTime(payload[FieldCreatedAt].IntegerValue, DateTimeKind.Utc),
            ConversationId = payload.TryGetValue(FieldConversationId, out var convId) 
                ? convId.StringValue 
                : null,
            SessionId = payload.TryGetValue(FieldSessionId, out var sessId) 
                ? sessId.StringValue 
                : null
        };
        
        return new MemorySearchResult
        {
            Memory = entry,
            Score = point.Score
        };
    }

    public void Dispose()
    {
        _client.Dispose();
    }
}
