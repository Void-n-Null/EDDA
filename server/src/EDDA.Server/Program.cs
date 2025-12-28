using EDDA.Server.Handlers;
using EDDA.Server.Models;
using EDDA.Server.Services;

var builder = WebApplication.CreateBuilder(args);

// ============================================================================
// Configuration
// ============================================================================

var audioConfig = AudioConfig.FromEnvironment();
var ttsConfig = TtsConfig.FromEnvironment();

builder.Services.AddSingleton(audioConfig);
builder.Services.AddSingleton(ttsConfig);

// ============================================================================
// Services
// ============================================================================

// Speech-to-text (Whisper)
builder.Services.AddSingleton<IWhisperService, WhisperService>();

// Text-to-speech (Chatterbox via microservice)
builder.Services.AddSingleton<ITtsService, TtsService>();

// Audio processing (WAV parsing, tempo adjustment, etc.)
builder.Services.AddSingleton<IAudioProcessor, AudioProcessor>();

// WebSocket handler
builder.Services.AddTransient<WebSocketHandler>();

var app = builder.Build();

// ============================================================================
// Service Initialization
// ============================================================================

app.Logger.LogInformation("=" + new string('=', 59));
app.Logger.LogInformation("EDDA Server Starting");
app.Logger.LogInformation("=" + new string('=', 59));

// Initialize Whisper (STT)
app.Logger.LogInformation("Initializing Whisper STT...");
var whisper = app.Services.GetRequiredService<IWhisperService>();
await whisper.InitializeAsync();

// Initialize TTS service health monitoring
app.Logger.LogInformation("Initializing TTS service ({Backend}) at {Url}...", 
    ttsConfig.BackendName, ttsConfig.ActiveUrl);
var tts = app.Services.GetRequiredService<ITtsService>();
await tts.InitializeAsync();

app.Logger.LogInformation("TTS service ({Backend}): {Status}", 
    ttsConfig.BackendName, tts.IsHealthy ? "OK" : "UNAVAILABLE");
if (!tts.IsHealthy)
{
    app.Logger.LogWarning(
        "TTS service not available at startup. Ensure Docker containers are running: " +
        "cd docker && docker compose up -d");
    app.Logger.LogWarning(
        "To switch TTS backend, set TTS_URL environment variable:");
    app.Logger.LogWarning(
        "  Chatterbox (quality): TTS_URL=http://localhost:5000");
    app.Logger.LogWarning(
        "  Piper (speed):        TTS_URL=http://localhost:5001");
}

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

app.Logger.LogInformation("EDDA Server ready on http://0.0.0.0:8080");
app.Run("http://0.0.0.0:8080");