namespace EDDA.Server.Services.Memory;

/// <summary>
/// Service for generating text embeddings.
/// </summary>
public interface IEmbeddingService
{
    /// <summary>
    /// Whether the service is initialized and ready.
    /// </summary>
    bool IsInitialized { get; }
    
    /// <summary>
    /// The dimensionality of embeddings produced by this service.
    /// </summary>
    int Dimensions { get; }
    
    /// <summary>
    /// Initialize the embedding service.
    /// </summary>
    Task InitializeAsync();
    
    /// <summary>
    /// Generate an embedding for a single text.
    /// </summary>
    Task<float[]> EmbedAsync(string text, CancellationToken ct = default);
    
    /// <summary>
    /// Generate embeddings for multiple texts in batch.
    /// </summary>
    Task<float[][]> EmbedBatchAsync(IEnumerable<string> texts, CancellationToken ct = default);
}
