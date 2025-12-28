using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using EDDA.Server.Models;
using EDDA.Server.Services;

namespace EDDA.Server.Handlers;

/// <summary>
/// Handles WebSocket connections for voice interaction.
/// Thin coordinator: delegates to VoiceSession for state and ResponsePipeline for output.
/// </summary>
public class WebSocketHandler(
    IWhisperService whisper,
    IResponsePipeline pipeline,
    AudioConfig config,
    ILogger<WebSocketHandler> logger)
{
    public async Task HandleConnectionAsync(WebSocket webSocket)
    {
        logger.LogInformation("Client connected");
        
        // Create session - encapsulates all state for this connection
        using var session = new VoiceSession(whisper, config, logger);
        
        // Create message sink for this connection
        var sink = new WebSocketMessageSink(webSocket);
        
        // Wire up the response handler - fires when user finishes speaking and timeout expires
        session.ResponseReady += async (transcription, pipelineTimer) =>
        {
            await EchoResponse(pipelineTimer, transcription, sink);
        };
        
        var buffer = new byte[1024 * 16];
        
        try
        {
            await ListenToConnection(webSocket, buffer, session);
        }
        catch (WebSocketException)
        {
            // Client disconnected ungracefully (crash, network loss, etc.) - expected behavior
            logger.LogDebug("Client disconnected unexpectedly");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Connection error");
        }
        
        logger.LogInformation("Client disconnected");
    }

    private async Task EchoResponse(Stopwatch? pipelineTimer, string transcription, WebSocketMessageSink sink)
    {
        // Echo mode: Repeat what the user said (tests full STT -> TTS pipeline)
        // TODO: Replace with actual LLM response
        var responseText = $"You said [chuckle] {transcription}.";
        await pipeline.StreamResponseAsync(sink, responseText, pipelineTimer);
    }

    private async Task ListenToConnection(WebSocket webSocket, byte[] buffer, VoiceSession session)
    {
        while (webSocket.State == WebSocketState.Open)
        {
            var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);

            switch (result.MessageType)
            {
                case WebSocketMessageType.Text:
                    await ProcessMessageAsync(session, buffer, result.Count);
                    break;
                case WebSocketMessageType.Close:
                    await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                    break;
            }
        }
    }


    enum MessageType
    {
        AudioChunk,
        EndSpeech
    }

    private static MessageType ParseMessageType(string? type)
    {
        return type switch
        {
            "audio_chunk" => MessageType.AudioChunk,
            "end_speech" => MessageType.EndSpeech,
            _ => throw new ArgumentException($"Unknown message type: {type}")
        };
    }

    /// <summary>
    /// Parse and dispatch incoming WebSocket message to the session.
    /// </summary>
    private static async Task ProcessMessageAsync(VoiceSession session, byte[] buffer, int count)
    {
        var json = Encoding.UTF8.GetString(buffer, 0, count);
        
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        
        if (!root.TryGetProperty("type", out var typeProp))
            return;
        
        var messageType = ParseMessageType(typeProp.GetString());
        
        switch (messageType)
        {
            case MessageType.AudioChunk:
            {
                var dataBase64 = root.GetProperty("data").GetString();
                if (dataBase64 != null)
                {
                    var audioData = Convert.FromBase64String(dataBase64);
                    await session.HandleAudioChunkAsync(audioData);
                }
                break;
            }
            case MessageType.EndSpeech:
                await session.HandleEndSpeechAsync();
                break;
        }
    }
}
