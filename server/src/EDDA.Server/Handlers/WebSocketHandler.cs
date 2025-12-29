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
    IWakeWordService wakeWord,
    IResponsePipeline pipeline,
    AudioConfig config,
    ILogger<WebSocketHandler> logger)
{
    private const string DeactivationPhrase = "done for now";
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
            await HandleTranscriptionAsync(session, sink, transcription, pipelineTimer);
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

    /// <summary>
    /// Handle a transcription based on activation state.
    /// </summary>
    private async Task HandleTranscriptionAsync(
        VoiceSession session,
        WebSocketMessageSink sink,
        string transcription,
        Stopwatch? pipelineTimer)
    {
        // Check for deactivation phrase first (applies in both active and inactive modes)
        if (ContainsDeactivationPhrase(transcription))
        {
            if (session.IsActive)
            {
                session.Deactivate();
                await sink.SendStatusAsync("deactivated");
                
                // Send farewell message
                var farewell = "Okay, I'll be here if you need me.";
                await pipeline.StreamResponseAsync(sink, farewell, pipelineTimer);
            }
            else
            {
                // Already inactive, just ignore
                logger.LogDebug("Deactivation phrase in inactive mode, ignoring");
                await sink.SendStatusAsync("inactive");
            }
            return;
        }
        
        // If active, process directly
        if (session.IsActive)
        {
            await EchoResponse(pipelineTimer, transcription, sink);
            return;
        }
        
        // Inactive mode - check for wake word
        logger.LogDebug("Inactive mode, checking for wake word: \"{Text}\"",
            transcription.Length > 50 ? transcription[..50] + "..." : transcription);
        
        var isWakeWord = await wakeWord.IsWakeWordAsync(transcription);
        
        if (!isWakeWord)
        {
            logger.LogDebug("Not wake word, discarding");
            await sink.SendStatusAsync("inactive");
            return;
        }
        
        // Wake word detected - activate and process
        logger.LogInformation("Wake word detected, activating session");
        session.Activate();
        await sink.SendStatusAsync("active");
        
        // Strip the wake word from the transcription before processing
        var strippedText = StripWakeWord(transcription);
        
        if (!string.IsNullOrWhiteSpace(strippedText))
        {
            await EchoResponse(pipelineTimer, strippedText, sink);
        }
        else
        {
            // Just activated with wake word only, acknowledge
            var greeting = "Hey, what's up?";
            await pipeline.StreamResponseAsync(sink, greeting, pipelineTimer);
        }
    }
    
    /// <summary>
    /// Check if the transcription contains the deactivation phrase.
    /// </summary>
    private static bool ContainsDeactivationPhrase(string transcription)
    {
        return transcription.Contains(DeactivationPhrase, StringComparison.OrdinalIgnoreCase);
    }
    
    /// <summary>
    /// Strip wake word patterns from the beginning of the transcription.
    /// </summary>
    private static string StripWakeWord(string transcription)
    {
        // Common patterns to strip (case-insensitive)
        string[] patterns = 
        [
            "hey nyxie",
            "hey nixie",
            "hey nicky",
            "hey pixie",
            "nyxie",
            "nixie",
            "nicky",
            "pixie"
        ];
        
        var result = transcription.Trim();
        
        foreach (var pattern in patterns)
        {
            if (result.StartsWith(pattern, StringComparison.OrdinalIgnoreCase))
            {
                result = result[pattern.Length..].TrimStart(',', ' ', '.');
                break;
            }
        }
        
        return result.Trim();
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
