using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using EDDA.Server.Models;
using Microsoft.Extensions.Logging;

namespace EDDA.Server.Services.Memory;

/// <summary>
/// Embedding service using OpenRouter's embedding models.
/// 
/// Default: Qwen3-Embedding-8B with 1024 dimensions (Matryoshka reduced)
/// - Full quality: 4096 dimensions
/// - Good balance: 1024 dimensions (recommended for voice assistant)
/// - Fast/cheap: 512 dimensions
/// 
/// Qwen3-Embedding-8B supports 32k context and 100+ languages.
/// </summary>
public class OpenRouterEmbeddingService : IEmbeddingService, IDisposable
{
    private readonly OpenRouterConfig _config;
    private readonly HttpClient _http;
    private readonly ILogger<OpenRouterEmbeddingService>? _logger;
    private readonly string _model;
    private readonly int _dimensions;
    
    // Qwen3-Embedding-8B - high quality, supports Matryoshka (flexible dimensions 32-4096)
    private const string DefaultEmbeddingModel = "qwen/qwen3-embedding-8b";
    
    // 1024 dims is a good balance of quality and speed for RAG memory
    // Can go up to 4096 for max quality, or down to 512 for faster search
    private const int DefaultDimensions = 1024;
    
    public bool IsInitialized { get; private set; }
    
    /// <summary>
    /// The dimensionality of embeddings produced.
    /// Qwen3-Embedding-8B supports 32-4096, defaults to 1024.
    /// </summary>
    public int Dimensions => _dimensions;

    public OpenRouterEmbeddingService(
        OpenRouterConfig config,
        ILogger<OpenRouterEmbeddingService>? logger = null,
        string? model = null,
        int? dimensions = null)
    {
        _config = config;
        _logger = logger;
        _model = model ?? DefaultEmbeddingModel;
        _dimensions = dimensions ?? DefaultDimensions;

        _http = new HttpClient
        {
            BaseAddress = new Uri(_config.BaseUrl),
            Timeout = TimeSpan.FromSeconds(60)
        };

        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _config.ApiKey);
    }

    public async Task InitializeAsync()
    {
        _logger?.LogInformation(
            "Initializing embedding service (model: {Model}, dims: {Dims})...", 
            _model, _dimensions);
        
        try
        {
            // Test with a simple embedding request
            var testEmbed = await EmbedAsync("test");
            
            if (testEmbed.Length == _dimensions)
            {
                _logger?.LogInformation(
                    "Embedding service initialized (dimensions: {Dims})", 
                    testEmbed.Length);
                IsInitialized = true;
            }
            else if (testEmbed.Length > 0)
            {
                _logger?.LogWarning(
                    "Embedding dimensions mismatch: expected {Expected}, got {Actual}",
                    _dimensions, testEmbed.Length);
                IsInitialized = true;
            }
            else
            {
                _logger?.LogError("Embedding service returned empty vector");
                IsInitialized = false;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to initialize embedding service");
            IsInitialized = false;
        }
    }

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        var result = await EmbedBatchAsync([text], ct);
        return result[0];
    }

    public async Task<float[][]> EmbedBatchAsync(IEnumerable<string> texts, CancellationToken ct = default)
    {
        var textList = texts.ToList();
        
        if (textList.Count == 0)
            return [];

        // Build request with dimensions parameter for Matryoshka support
        // Force DeepInfra provider for lower latency (testing)
        var request = new
        {
            model = _model,
            input = textList,
            dimensions = _dimensions,
            provider = new
            {
                order = new[] { "DeepInfra" }
            }
        };

        var json = JsonSerializer.Serialize(request);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        _logger?.LogInformation("EMBED: Generating embeddings for {Count} text(s) ({Dims}d) [provider: DeepInfra]...", textList.Count, _dimensions);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var response = await _http.PostAsync("embeddings", content, ct);
        sw.Stop();
        
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            _logger?.LogError("Embedding request failed: {Status} - {Body}", 
                response.StatusCode, errorBody);
            throw new HttpRequestException($"Embedding request failed: {response.StatusCode}");
        }

        var responseJson = await response.Content.ReadAsStringAsync(ct);
        
        _logger?.LogInformation("EMBED: Completed in {Ms}ms", sw.ElapsedMilliseconds);
        
        return ParseEmbeddingResponse(responseJson);
    }

    private static float[][] ParseEmbeddingResponse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        
        var data = root.GetProperty("data");
        var results = new float[data.GetArrayLength()][];
        
        foreach (var item in data.EnumerateArray())
        {
            var index = item.GetProperty("index").GetInt32();
            var embedding = item.GetProperty("embedding");
            
            var vector = new float[embedding.GetArrayLength()];
            var i = 0;
            foreach (var value in embedding.EnumerateArray())
            {
                vector[i++] = value.GetSingle();
            }
            
            results[index] = vector;
        }
        
        return results;
    }

    public void Dispose()
    {
        _http.Dispose();
    }
}
