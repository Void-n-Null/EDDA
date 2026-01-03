using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using EDDA.Server.Agent;
using EDDA.Server.Models;
using EDDA.Server.Services;
using EDDA.Server.Services.Memory;
using EDDA.Server.Services.Session;

namespace EDDA.Server.Handlers;

/// <summary>
/// Memory search options matching what EddaAgent uses internally.
/// </summary>
file static class MemorySearchHelper
{
    public static async Task<string?> SearchAndFormatAsync(
        IConversationMemory memory,
        string query,
        ILogger logger,
        CancellationToken ct = default)
    {
        if (!memory.IsInitialized)
            return null;

        try
        {
            var searchOptions = new TimeDecaySearchOptions
            {
                OversampleCount = 30,
                RecencyWeight = 0.3f,
                HalfLifeHours = 72f,
                FinalCount = 5
            };

            var filter = new MemoryFilter
            {
                Types = ["exchange"]
            };

            var results = await memory.SearchWithTimeDecayAsync(query, searchOptions, filter, ct);

            if (results.Count == 0)
                return null;

            logger.LogInformation(
                "MEMORY: Found {Count} relevant memories (top score: {Score:F3})",
                results.Count,
                results[0].Score);

            // Format memories for injection (same format as EddaAgent)
            var sb = new StringBuilder();
            foreach (var result in results)
            {
                var dateStr = result.Memory.CreatedAt.ToString("MMM d");
                var content = result.Memory.Content;
                if (content.Length > 200)
                    content = content[..197] + "...";

                sb.AppendLine($"- {dateStr}: {content}");
            }

            return sb.ToString().Trim();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "MEMORY: Speculative search failed, will retry in agent");
            return null;
        }
    }
}

/// <summary>
/// Handles WebSocket connections for voice interaction.
/// Thin coordinator: delegates to VoiceSession for state, Agent for AI, and ResponsePipeline for TTS.
/// </summary>
public class WebSocketHandler(
    IWhisperService whisper,
    IWakeWordService wakeWord,
    IAgent agent,
    IResponsePipeline pipeline,
    IConversationMemory memory,
    IVoiceSessionAccessor sessions,
    IMessageSinkAccessor sinks,
    AudioConfig config,
    ILogger<WebSocketHandler> logger)
{
    private const string DeactivationPhrase = "done for now";
    public async Task HandleConnectionAsync(WebSocket webSocket)
    {
        logger.LogInformation("Client connected");

        // Create session - encapsulates all state for this connection
        // Pass memory service so conversations persist exchanges on dispose
        using var session = new VoiceSession(whisper, config, logger, memory);

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

        // If active, process with agent
        if (session.IsActive && session.CurrentConversation != null)
        {
            await ProcessWithAgentAsync(session, sink, transcription, pipelineTimer);
            return;
        }

        // Inactive mode - check for wake word AND speculatively search memory in parallel
        // This hides ~500ms of memory search latency behind the ~1000ms wake word check
        logger.LogDebug("Inactive mode, checking for wake word: \"{Text}\"",
            transcription.Length > 50 ? transcription[..50] + "..." : transcription);

        var strippedText = StripWakeWord(transcription);
        
        // Start both operations in parallel
        var wakeWordTask = wakeWord.IsWakeWordAsync(transcription);
        var memoryTask = !string.IsNullOrWhiteSpace(strippedText)
            ? MemorySearchHelper.SearchAndFormatAsync(memory, strippedText, logger)
            : Task.FromResult<string?>(null);

        // Wait for wake word first (it's the gate)
        var isWakeWord = await wakeWordTask;

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

        if (!string.IsNullOrWhiteSpace(strippedText) && session.CurrentConversation != null)
        {
            // Await the memory search (should be done or nearly done by now)
            var preloadedMemory = await memoryTask;
            await ProcessWithAgentAsync(session, sink, strippedText, pipelineTimer, preloadedMemory);
        }
        else
        {
            // Just activated with wake word only, acknowledge
            var greeting = "Hey, what's up?";
            await pipeline.StreamResponseAsync(sink, greeting, pipelineTimer);
        }
    }

    /// <summary>
    /// Process user message through the agent and stream TTS for each sentence.
    /// Uses the streaming API to play loading audio until first sentence, then stream sentences.
    /// </summary>
    /// <param name="session">The voice session.</param>
    /// <param name="sink">Message sink for sending responses.</param>
    /// <param name="userMessage">The user's message to process.</param>
    /// <param name="pipelineTimer">Timer for TTFA tracking.</param>
    /// <param name="preloadedMemoryContext">Optional pre-fetched memory context (for parallel wake word + memory search).</param>
    private async Task ProcessWithAgentAsync(
        VoiceSession session,
        WebSocketMessageSink sink,
        string userMessage,
        Stopwatch? pipelineTimer,
        string? preloadedMemoryContext = null)
    {
        if (session.CurrentConversation == null)
        {
            logger.LogWarning("ProcessWithAgentAsync called without active conversation");
            return;
        }

        // Start streaming (plays loading audio)
        var streamContext = await pipeline.BeginStreamingAsync(sink, pipelineTimer);

        try
        {
            // Make session and sink available to tools executed within the agent call.
            using var sessionScope = sessions.Use(session);
            using var sinkScope = sinks.Use(sink);

            await foreach (var chunk in agent.ProcessStreamAsync(
                session.CurrentConversation,
                userMessage,
                preloadedMemoryContext,
                CancellationToken.None))
            {
                switch (chunk.Type)
                {
                    case AgentChunkType.Sentence:
                        // Stream this sentence to TTS immediately
                        // First call cancels loading audio
                        await pipeline.StreamSentenceAsync(streamContext, chunk.Text!);
                        break;

                    case AgentChunkType.ToolExecuting:
                        logger.LogDebug("Tool executing: {Tool}", chunk.ToolName);
                        // TODO: Could play a different "thinking" sound for tools
                        break;

                    case AgentChunkType.Complete:
                        // Handled in finally block
                        break;
                }
            }
        }
        finally
        {
            // Always end streaming (sends response_complete, logs stats)
            await pipeline.EndStreamingAsync(streamContext);

            // Apply tool-requested deactivation *after* the response completes.
            if (session.IsDeactivationRequested)
            {
                session.Deactivate();
                await sink.SendStatusAsync("deactivated");
            }
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
