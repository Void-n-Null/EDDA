using EDDA.Server.Handlers;
using EDDA.Server.Models;
using EDDA.Server.Services;

var builder = WebApplication.CreateBuilder(args);

// Configuration
var audioConfig = AudioConfig.FromEnvironment();
builder.Services.AddSingleton(audioConfig);
// Services
builder.Services.AddSingleton<IWhisperService, WhisperService>();
builder.Services.AddTransient<WebSocketHandler>();

var app = builder.Build();

// Initialize Whisper at startup
var whisper = app.Services.GetRequiredService<IWhisperService>();
await whisper.InitializeAsync();

app.UseWebSockets();

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

app.Logger.LogInformation("EDDA Server starting...");
app.Run("http://0.0.0.0:8080");