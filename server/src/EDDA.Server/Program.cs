using EDDA.Server.Agent;
using EDDA.Server.Agent.Context;
using EDDA.Server.Agent.Context.Providers;
using EDDA.Server.Handlers;
using EDDA.Server.Models;
using EDDA.Server.Services;
using EDDA.Server.Services.Llm;
using EDDA.Server.Services.Memory;
using EDDA.Server.Services.Session;
using EDDA.Server.Services.WebSearch;

var builder = WebApplication.CreateBuilder(args);

// ============================================================================
// Configuration
// ============================================================================

var audioConfig = AudioConfig.FromEnvironment();
var ttsConfig = TtsConfig.FromEnvironment();
var openRouterConfig = OpenRouterConfig.FromEnvironment();

builder.Services.AddSingleton(audioConfig);
builder.Services.AddSingleton(ttsConfig);
builder.Services.AddSingleton(openRouterConfig);

// ============================================================================
// Services
// ============================================================================

// Speech-to-text (Whisper)
builder.Services.AddSingleton<IWhisperService, WhisperService>();

// Text-to-speech (Chatterbox via microservice)
builder.Services.AddSingleton<ITtsService, TtsService>();

// Audio processing (WAV parsing, tempo adjustment, etc.)
builder.Services.AddSingleton<IAudioProcessor, AudioProcessor>();

// Response pipeline (TTS orchestration, sentence streaming)
builder.Services.AddSingleton<IResponsePipeline, ResponsePipeline>();

// Per-connection session accessor (for tools)
builder.Services.AddSingleton<IVoiceSessionAccessor, VoiceSessionAccessor>();

// Per-connection message sink accessor (for tools that send messages to client)
builder.Services.AddSingleton<IMessageSinkAccessor, MessageSinkAccessor>();

// LLM services (OpenRouter) - with tool executor for agent
builder.Services.AddSingleton<ToolDiscovery>(sp =>
{
    var discovery = new ToolDiscovery(sp, sp.GetService<ILogger<ToolDiscovery>>());
    // Auto-discover tools from this assembly
    discovery.FromAssembly(typeof(Program).Assembly);
    return discovery;
});

builder.Services.AddSingleton<ToolExecutor>(sp =>
{
    var discovery = sp.GetRequiredService<ToolDiscovery>();
    var logger = sp.GetService<ILogger<ToolExecutor>>();
    return new ToolExecutor(discovery, logger);
});

builder.Services.AddSingleton<IOpenRouterService>(sp =>
{
    var config = sp.GetRequiredService<OpenRouterConfig>();
    var logger = sp.GetRequiredService<ILogger<OpenRouterService>>();
    var toolExecutor = sp.GetRequiredService<ToolExecutor>();
    return new OpenRouterService(config, logger, toolExecutor);
});

// Wake word detection (LLM-based)
builder.Services.AddSingleton<IWakeWordService>(sp =>
{
    var llm = sp.GetRequiredService<IOpenRouterService>();
    var logger = sp.GetRequiredService<ILogger<WakeWordService>>();
    return new WakeWordService(llm, logger);
});

// ============================================================================
// Web Search Services (optional - requires WEBSEARCH_API_KEY)
// ============================================================================

var webSearchConfig = WebSearchConfig.TryFromEnvironment();
if (webSearchConfig is not null)
{
    builder.Services.AddSingleton(webSearchConfig);
    builder.Services.AddSingleton<IWebSearchService>(sp =>
    {
        var config = sp.GetRequiredService<WebSearchConfig>();
        var logger = sp.GetService<ILogger<TavilyWebSearchService>>();
        return new TavilyWebSearchService(config, logger);
    });
}

// ============================================================================
// Memory Services (Qdrant on basement server)
// ============================================================================

// Embedding service (uses OpenRouter's text-embedding-3-small)
builder.Services.AddSingleton<IEmbeddingService>(sp =>
{
    var config = sp.GetRequiredService<OpenRouterConfig>();
    var logger = sp.GetService<ILogger<OpenRouterEmbeddingService>>();
    return new OpenRouterEmbeddingService(config, logger);
});

// Qdrant memory service (connects to localhost:6334 - same server as this app)
builder.Services.AddSingleton<IConversationMemory>(sp =>
{
    var embeddings = sp.GetRequiredService<IEmbeddingService>();
    var logger = sp.GetService<ILogger<QdrantMemoryService>>();
    // Qdrant runs in Docker on the same basement server
    // gRPC port 6334 for better performance
    return new QdrantMemoryService(embeddings, logger, host: "localhost", port: 6334);
});

// ============================================================================
// Agent Services
// ============================================================================

// Context providers (modular - add new providers here)
builder.Services.AddSingleton<IContextProvider, TimeContextProvider>();
builder.Services.AddSingleton<IContextProvider, ConversationContextProvider>();
builder.Services.AddSingleton<IContextProvider>(sp =>
{
    var memory = sp.GetRequiredService<IConversationMemory>();
    var logger = sp.GetService<ILogger<MemoryContextProvider>>();
    return new MemoryContextProvider(memory, logger);
});

// Context builder (combines all providers)
builder.Services.AddSingleton<ContextBuilder>();

// Main agent
builder.Services.AddSingleton<IAgent, EddaAgent>();

// WebSocket handler
builder.Services.AddTransient<WebSocketHandler>();

// ============================================================================
// Logging Configuration
// ============================================================================

builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Warning);

var app = builder.Build();

// ============================================================================
// Service Initialization
// ============================================================================

var log = app.Logger;

log.LogInformation("");
log.LogInformation("┌────────────────────────────────────────────────────────┐");
log.LogInformation("│  EDDA Server                                           │");
log.LogInformation("└────────────────────────────────────────────────────────┘");

// Initialize Whisper (STT)
var whisper = app.Services.GetRequiredService<IWhisperService>();
await whisper.InitializeAsync();

// Initialize TTS service health monitoring
var tts = app.Services.GetRequiredService<ITtsService>();
await tts.InitializeAsync();

// Initialize OpenRouter (LLM for wake word detection)
var openRouter = app.Services.GetRequiredService<IOpenRouterService>();
await openRouter.InitializeAsync();

// Initialize memory services (embedding + Qdrant)
var embeddings = app.Services.GetRequiredService<IEmbeddingService>();
await embeddings.InitializeAsync();

var memory = app.Services.GetRequiredService<IConversationMemory>();
await memory.InitializeAsync();

// Initialize web search (optional)
var webSearch = app.Services.GetService<IWebSearchService>();
if (webSearch is not null)
{
    await webSearch.InitializeAsync();
}

// Warm up response pipeline (loads embedded resources)
_ = app.Services.GetRequiredService<IResponsePipeline>();

// Warm up agent (loads prompt templates)
var agent = app.Services.GetRequiredService<IAgent>();
var toolDiscovery = app.Services.GetRequiredService<ToolDiscovery>();
var contextBuilder = app.Services.GetRequiredService<ContextBuilder>();

// Summary log
log.LogInformation("");
log.LogInformation("  STT: {Status}", whisper.IsInitialized ? "✓ Ready" : "✗ FAILED");
log.LogInformation("  TTS: {Status} ({Backend})",
    tts.IsHealthy ? "✓ Ready" : "✗ Unavailable",
    ttsConfig.BackendName);
log.LogInformation("  LLM: {Status}", openRouter.IsInitialized ? "✓ Ready" : "✗ FAILED");
log.LogInformation("  Memory: {Status}",
    embeddings.IsInitialized && memory.IsInitialized ? "✓ Ready" : "✗ Unavailable");
log.LogInformation("  WebSearch: {Status}",
    webSearch?.IsInitialized == true ? "✓ Ready" : "✗ Not configured");
log.LogInformation("  Agent: ✓ Ready ({Tools} tools, {Contexts} context providers)",
    toolDiscovery.Tools.Count,
    contextBuilder.RegisteredKeys.Count);

if (!memory.IsInitialized)
{
    log.LogWarning("");
    log.LogWarning("  Qdrant not responding. Start containers:");
    log.LogWarning("    cd docker && docker compose up -d qdrant");
}

if (!tts.IsHealthy)
{
    log.LogWarning("");
    log.LogWarning("  TTS not responding. Start containers:");
    log.LogWarning("    cd docker && docker compose up -d");
}

log.LogInformation("");
log.LogInformation("  Listening on http://0.0.0.0:8080");
log.LogInformation("");

// ============================================================================
// Endpoints
// ============================================================================

app.UseWebSockets();

// WebSocket endpoint for voice communication
app.Map("/ws", async context =>
{
    if (context.WebSockets.IsWebSocketRequest)
    {
        using var webSocket = await context.WebSockets.AcceptWebSocketAsync();
        var handler = context.RequestServices.GetRequiredService<WebSocketHandler>();
        await handler.HandleConnectionAsync(webSocket);
    }
    else
    {
        context.Response.StatusCode = 400;
    }
});

// Health check endpoint
app.MapGet("/health", (IWhisperService whisper, ITtsService tts) => new
{
    status = whisper.IsInitialized && tts.IsHealthy ? "healthy" : "degraded",
    whisper = new { ready = whisper.IsInitialized },
    tts = new { ready = tts.IsHealthy, status = tts.LastHealthStatus },
});

app.Run("http://0.0.0.0:8080");
