using EDDA.Server.Handlers;
using EDDA.Server.Models;
using EDDA.Server.Services;
using EDDA.Server.Services.Llm;

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

// LLM services (OpenRouter)
builder.Services.AddSingleton<IOpenRouterService>(sp =>
{
    var config = sp.GetRequiredService<OpenRouterConfig>();
    var logger = sp.GetRequiredService<ILogger<OpenRouterService>>();
    return new OpenRouterService(config, logger);
});

// Wake word detection (LLM-based)
builder.Services.AddSingleton<IWakeWordService>(sp =>
{
    var llm = sp.GetRequiredService<IOpenRouterService>();
    var logger = sp.GetRequiredService<ILogger<WakeWordService>>();
    return new WakeWordService(llm, logger);
});

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

// Warm up response pipeline (loads embedded resources)
_ = app.Services.GetRequiredService<IResponsePipeline>();

// Summary log
log.LogInformation("");
log.LogInformation("  STT: {Status}", whisper.IsInitialized ? "✓ Ready" : "✗ FAILED");
log.LogInformation("  TTS: {Status} ({Backend})", 
    tts.IsHealthy ? "✓ Ready" : "✗ Unavailable", 
    ttsConfig.BackendName);
log.LogInformation("  LLM: {Status} (wake word)", openRouter.IsInitialized ? "✓ Ready" : "✗ FAILED");

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
