using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using EDDA.Server.Models;
using EDDA.Server.Services;

namespace EDDA.Server.Handlers;

/// <summary>
/// Handles WebSocket connections for audio streaming and transcription.
/// This is the main entry point for the WebSocket server.
/// </summary>
public class WebSocketHandler
{
    private readonly IWhisperService _whisper;
    private readonly ITtsService _tts;
    private readonly IAudioProcessor _audio;
    private readonly AudioConfig _config;
    private readonly ILogger<WebSocketHandler> _logger;
    private readonly byte[] _loadingAudio;
    
    public WebSocketHandler(
        IWhisperService whisper,
        ITtsService tts,
        IAudioProcessor audio,
        AudioConfig config,
        ILogger<WebSocketHandler> logger)
    {
        _whisper = whisper;
        _tts = tts;
        _audio = audio;
        _config = config;
        _logger = logger;
        
        // Load embedded loading audio resource
        _loadingAudio = LoadEmbeddedResource("EDDA.Server.Resources.loading.wav");
        if (_loadingAudio.Length > 0)
        {
            _logger.LogInformation("Loaded embedded loading audio ({Bytes} bytes)", _loadingAudio.Length);
        }
        else
        {
            _logger.LogWarning("Failed to load embedded loading audio resource");
        }
    }
    
    private static byte[] LoadEmbeddedResource(string resourceName)
    {
        var assembly = typeof(WebSocketHandler).Assembly;
        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
            return [];
        
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    private static async Task SendJsonAsync(WebSocket webSocket, object payload, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(payload);
        var bytes = Encoding.UTF8.GetBytes(json);
        await webSocket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken: ct);
    }

    // Note: Streaming payloads are still used for loading audio.
    // TTS now uses AudioSentencePayload (complete WAV files) instead.
    private static object StreamStartPayload(string stream, int sampleRate, int channels, float tempo = 1.0f)
        => new
        {
            type = "audio_stream_start",
            stream,
            sample_rate = sampleRate,
            channels,
            sample_format = "s16le",
            tempo
        };

    private static object StreamChunkPayload(string stream, byte[] pcmChunk)
        => new
        {
            type = "audio_stream_chunk",
            stream,
            data = Convert.ToBase64String(pcmChunk)
        };

    private static object StreamEndPayload(string stream)
        => new
        {
            type = "audio_stream_end",
            stream
        };

    private static object AudioSentencePayload(byte[] wavBytes, int sentenceIndex, int totalSentences, 
                                                int durationMs, int sampleRate, float tempoApplied)
        => new
        {
            type = "audio_sentence",
            data = Convert.ToBase64String(wavBytes),
            sentence_index = sentenceIndex,
            total_sentences = totalSentences,
            duration_ms = durationMs,
            sample_rate = sampleRate,
            tempo_applied = tempoApplied
        };

    private static object AudioCachePlayPayload(string cacheKey, bool loop = false)
        => new
        {
            type = "audio_cache_play",
            cache_key = cacheKey,
            loop
        };

    private static object AudioCacheStorePayload(string cacheKey, byte[] wavBytes, int sampleRate, int channels, int durationMs)
        => new
        {
            type = "audio_cache_store",
            cache_key = cacheKey,
            data = Convert.ToBase64String(wavBytes),
            sample_rate = sampleRate,
            channels = channels,
            duration_ms = durationMs
        };
    
    public async Task HandleConnectionAsync(WebSocket webSocket)
    {
        _logger.LogInformation("WebSocket connection established");
        
        // Create session - encapsulates all state for this connection
        using var session = new VoiceSession(_whisper, _config, _logger);
        
        // Wire up the response handler - fires when user finishes speaking and timeout expires
        session.ResponseReady += async (transcription, pipelineTimer) =>
        {
            // Echo mode: Repeat what the user said (tests full STT -> TTS pipeline)
            // TODO: Replace with actual LLM response
            var sttAndWaitMs = pipelineTimer?.ElapsedMilliseconds ?? 0;
            var llmStart = pipelineTimer?.ElapsedMilliseconds ?? 0;
            
            var responseText = $"You said [chuckle] {transcription}.";
            var llmMs = (pipelineTimer?.ElapsedMilliseconds ?? 0) - llmStart;
            
            _logger.LogInformation("⏱️ Pipeline | STT+Wait: {SttWaitMs}ms | LLM: {LlmMs}ms (echo mode) | Starting TTS...",
                sttAndWaitMs, llmMs);
            
            await SendTtsResponseAsync(webSocket, responseText, pipelineTimer);
        };
        
        var buffer = new byte[1024 * 16];
        
        try
        {
            while (webSocket.State == WebSocketState.Open)
            {
                var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                
                if (result.MessageType == WebSocketMessageType.Text)
                {
                    await ProcessMessageAsync(session, buffer, result.Count);
                }
                else if (result.MessageType == WebSocketMessageType.Close)
                {
                    await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                    _logger.LogInformation("WebSocket closed by client");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Connection error");
        }
    }
    
    /// <summary>
    /// Parse and dispatch incoming WebSocket message to the session.
    /// </summary>
    private async Task ProcessMessageAsync(VoiceSession session, byte[] buffer, int count)
    {
        var json = Encoding.UTF8.GetString(buffer, 0, count);
        
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        
        if (!root.TryGetProperty("type", out var typeProp))
            return;
        
        var messageType = typeProp.GetString();
        
        switch (messageType)
        {
            case "audio_chunk":
            {
                var dataBase64 = root.GetProperty("data").GetString();
                if (dataBase64 != null)
                {
                    var audioData = Convert.FromBase64String(dataBase64);
                    await session.HandleAudioChunkAsync(audioData);
                }
                break;
            }
            case "end_speech":
                await session.HandleEndSpeechAsync();
                break;
        }
    }
    
    // Loading audio is now streamed as raw PCM via audio_stream_* messages inside SendTtsResponseAsync.
    
    /// <summary>
    /// Generate speech using TTS and stream to client sentence-by-sentence.
    /// This reduces time-to-first-audio by sending each sentence as soon as it's ready.
    /// Falls back to loading audio loop if TTS is unavailable.
    /// </summary>
    private async Task SendTtsResponseAsync(WebSocket webSocket, string text, Stopwatch? ttfaTimer = null)
    {
        // Split into sentences for streaming
        var sentences = SplitIntoSentences(text);
        _logger.LogInformation("Streaming TTS response: {Count} sentence(s)", sentences.Count);

        // One sender owns the websocket; producers enqueue payloads to a bounded channel.
        var payloads = Channel.CreateBounded<object>(new BoundedChannelOptions(1024)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });

        long senderBacklog = 0;
        async ValueTask EnqueueAsync(object payload)
        {
            Interlocked.Increment(ref senderBacklog);
            await payloads.Writer.WriteAsync(payload);
        }

        var sender = Task.Run(async () =>
        {
            try
            {
                await foreach (var payload in payloads.Reader.ReadAllAsync())
                {
                    if (webSocket.State != WebSocketState.Open)
                        break;
                    try
                    {
                        await SendJsonAsync(webSocket, payload);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref senderBacklog);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Sender loop ended");
            }
        });

        using var allCts = new CancellationTokenSource();
        using var loadingCts = new CancellationTokenSource();

        // Request loading audio from cache (with fallback data if not cached).
        var loadingTask = Task.Run(async () =>
        {
            if (!_audio.TryParsePcmWav(_loadingAudio, out var loading) || loading == null)
                return;

            const string cacheKey = "loading_v2"; // v2 includes silence padding
            
            // Add silence padding to prevent audio device startup clipping
            var paddedLoading = _audio.AddSilencePadding(_loadingAudio, paddingMs: 150);
            var durationMs = (int)Math.Round(loading.Pcm.Length * 1000.0 / (loading.SampleRate * loading.Channels * 2.0)) + 150;

            // Request cached playback (client will use cache if available)
            await EnqueueAsync(AudioCachePlayPayload(cacheKey, loop: true));

            // Immediately send store message as fallback (client will cache if not already cached)
            await EnqueueAsync(AudioCacheStorePayload(cacheKey, paddedLoading, loading.SampleRate, loading.Channels, durationMs));

            _logger.LogInformation("Loading audio requested from cache: {Key} ({Size}B, {Duration}ms, 150ms padding)", 
                cacheKey, paddedLoading.Length, durationMs);

            // Wait until loading is cancelled (when TTS is ready)
            await Task.Delay(Timeout.Infinite, loadingCts.Token);
        });

        var sentenceIndex = 0;
        var ttsStreamStarted = false;
        var firstTtsChunkEnqueued = false;

        for (var i = 0; i < sentences.Count; i++)
        {
            sentenceIndex = i + 1;
            var trimmed = sentences[i].Trim();
            if (string.IsNullOrEmpty(trimmed))
                continue;

            if (webSocket.State != WebSocketState.Open)
                break;

            if (!_tts.IsHealthy)
            {
                _logger.LogWarning("TTS became unhealthy mid-response");
                break;
            }

            try
            {
                var ttsChunkStart = ttfaTimer?.ElapsedMilliseconds ?? 0;
                _logger.LogInformation(
                    "TTS gen start [{Index}/{Total}] (sender backlog={Backlog}) : \"{Sentence}\"",
                    sentenceIndex, sentences.Count, Interlocked.Read(ref senderBacklog), trimmed);

                var wavBytes = await _tts.GenerateSpeechAsync(trimmed, exaggeration: 0.6f, cancellationToken: allCts.Token);
                var ttsChunkMs = (ttfaTimer?.ElapsedMilliseconds ?? 0) - ttsChunkStart;

                if (wavBytes.Length == 0)
                    continue;

                if (!_audio.TryParsePcmWav(wavBytes, out var ttsWav) || ttsWav == null)
                {
                    _logger.LogWarning("TTS returned unsupported WAV format for sentence {Index}", sentenceIndex);
                    continue;
                }

                var ttsBytesPerSecond = ttsWav.SampleRate * ttsWav.Channels * 2;
                var ttsAudioMs = (int)Math.Round(ttsWav.Pcm.Length * 1000.0 / Math.Max(1, ttsBytesPerSecond));
                _logger.LogInformation(
                    "TTS gen done  [{Index}/{Total}] in {GenMs}ms -> {AudioMs}ms audio (pcm={PcmBytes}B, fmt={Rate}Hz/{Ch}ch)",
                    sentenceIndex, sentences.Count, ttsChunkMs, ttsAudioMs, ttsWav.Pcm.Length, ttsWav.SampleRate, ttsWav.Channels);

                // First TTS audio available: stop loading audio
                if (!ttsStreamStarted)
                {
                    ttsStreamStarted = true;
                    loadingCts.Cancel();
                    try { await loadingTask; } catch { /* ignore */ }
                }

                // Calculate optimal tempo to match playback duration to next sentence's generation time
                // tempo < 1.0 = slower (buy time), tempo > 1.0 = faster (we're ahead)
                var tempo = 1.0f;
                if (_tts is TtsService service && service.Config.TempoAdjustmentEnabled && i + 1 < sentences.Count)
                {
                    var nextSentence = sentences[i + 1].Trim();
                    var estimatedNextGenMs = nextSentence.Length * service.Config.AvgMsPerChar;
                    
                    // Ideal tempo: make current audio last exactly as long as next gen takes
                    // desiredTempo = audioMs / genMs (lower = slower playback = more time)
                    var desiredTempo = ttsAudioMs / estimatedNextGenMs;
                    tempo = Math.Clamp(desiredTempo, service.Config.MinTempo, service.Config.MaxTempo);
                    
                    // Only log if we're actually adjusting
                    if (Math.Abs(tempo - 1.0f) > 0.01f)
                    {
                        var direction = tempo < 1.0f ? "slower" : "faster";
                        _logger.LogInformation(
                            "Tempo: {Tempo:F2}x ({Direction}) | audio={AudioMs}ms, est_next_gen={EstMs:F0}ms, ideal={Ideal:F2}x",
                            tempo, direction, ttsAudioMs, estimatedNextGenMs, desiredTempo);
                    }
                }

                // Apply tempo adjustment via ffmpeg if needed
                var adjustedWav = await _audio.AdjustTempoAsync(wavBytes, tempo, allCts.Token);
                
                // Add silence padding to prevent audio device startup clipping
                var paddedWav = _audio.AddSilencePadding(adjustedWav, paddingMs: 150);
                
                // Calculate adjusted duration if tempo was applied (add padding time)
                var adjustedDurationMs = tempo < 0.99f || tempo > 1.01f 
                    ? (int)Math.Round(ttsAudioMs / tempo) + 150
                    : ttsAudioMs + 150;

                // Send complete WAV file as single message
                var timer = ttfaTimer;
                var enqueueStart = timer?.ElapsedMilliseconds ?? 0;
                
                await EnqueueAsync(AudioSentencePayload(
                    paddedWav, 
                    sentenceIndex, 
                    sentences.Count, 
                    adjustedDurationMs,
                    ttsWav.SampleRate, 
                    tempo));

                if (!firstTtsChunkEnqueued && timer is not null)
                {
                    firstTtsChunkEnqueued = true;
                    var wsEnqueueMs = timer.ElapsedMilliseconds - enqueueStart;
                    _logger.LogInformation(
                        "⏱️ TTFA (server enqueue) | Total: {TotalMs}ms | TTS={TtsMs}ms | Tempo={TempoMs}ms | Enqueue={WsMs}ms",
                        timer.ElapsedMilliseconds, ttsChunkMs, (timer.ElapsedMilliseconds - ttsChunkMs - wsEnqueueMs), wsEnqueueMs);
                }

                _logger.LogInformation(
                    "Sentence sent [{Index}/{Total}] {Size}B @ {Tempo:F3}x +150ms pad (backlog={Backlog})",
                    sentenceIndex, sentences.Count, paddedWav.Length, tempo, Interlocked.Read(ref senderBacklog));
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "TTS failed for sentence {Index}, skipping", sentenceIndex);
            }
        }

        // Stop loading if we never started TTS.
        try { loadingCts.Cancel(); } catch { /* ignore */ }
        try { await loadingTask; } catch { /* ignore */ }

        payloads.Writer.TryComplete();
        try { await sender; } catch { /* ignore */ }

        await SendResponseCompleteAsync(webSocket);

        if (ttfaTimer != null)
        {
            _logger.LogInformation("⏱️ Full Response | Total time: {Ms}ms for {Count} sentence(s)",
                ttfaTimer.ElapsedMilliseconds, sentenceIndex);
        }
    }
    
    /// <summary>
    /// Split text into sentences using regex.
    /// Handles common abbreviations and edge cases.
    /// </summary>
    private static List<string> SplitIntoSentences(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];
        
        // Split on sentence-ending punctuation followed by whitespace or end of string
        // This regex looks for .!? followed by space or end, but preserves the punctuation
        var pattern = @"(?<=[.!?])\s+";
        var sentences = Regex.Split(text.Trim(), pattern)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();
        
        // If no splits occurred (single sentence or no punctuation), return the whole text
        if (sentences.Count == 0)
        {
            sentences.Add(text.Trim());
        }
        
        return sentences;
    }
    
    /// <summary>
    /// Send an audio chunk to the WebSocket client.
    /// </summary>
    private async Task SendAudioChunkAsync(WebSocket webSocket, byte[] audioData, int chunkIndex, int totalChunks)
    {
        var message = new
        {
            type = "audio_playback",
            data = Convert.ToBase64String(audioData),
            format = "wav",
            chunk = chunkIndex,
            total_chunks = totalChunks
        };
        
        var json = JsonSerializer.Serialize(message);
        var bytes = Encoding.UTF8.GetBytes(json);
        
        await webSocket.SendAsync(
            new ArraySegment<byte>(bytes),
            WebSocketMessageType.Text,
            endOfMessage: true,
            CancellationToken.None);
        
        _logger.LogInformation("Sent audio chunk {Index}/{Total} ({Bytes} bytes)", chunkIndex, totalChunks, audioData.Length);
    }
    
    /// <summary>
    /// Send a response_complete message to signal end of TTS output.
    /// </summary>
    private async Task SendResponseCompleteAsync(WebSocket webSocket)
    {
        var message = new { type = "response_complete" };
        var json = JsonSerializer.Serialize(message);
        var bytes = Encoding.UTF8.GetBytes(json);
        
        await webSocket.SendAsync(
            new ArraySegment<byte>(bytes),
            WebSocketMessageType.Text,
            endOfMessage: true,
            CancellationToken.None);
        
        _logger.LogDebug("Sent response_complete");
    }
}

