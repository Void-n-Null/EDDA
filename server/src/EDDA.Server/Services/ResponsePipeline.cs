using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using EDDA.Server.Messages;

namespace EDDA.Server.Services;

/// <summary>
/// Orchestrates TTS response generation and streaming.
/// Handles loading audio, sentence splitting, tempo adjustment, and delivery.
/// </summary>
public class ResponsePipeline : IResponsePipeline
{
    private readonly ITtsService _tts;
    private readonly IAudioProcessor _audio;
    private readonly ILogger<ResponsePipeline> _logger;
    private readonly byte[] _loadingAudio;

    public ResponsePipeline(
        ITtsService tts,
        IAudioProcessor audio,
        ILogger<ResponsePipeline> logger)
    {
        _tts = tts;
        _audio = audio;
        _logger = logger;

        // Load embedded loading audio resource
        _loadingAudio = LoadEmbeddedResource("EDDA.Server.Resources.loading.wav");
        if (_loadingAudio.Length == 0)
        {
            _logger.LogWarning("Failed to load embedded loading audio");
        }
    }

    private static byte[] LoadEmbeddedResource(string resourceName)
    {
        var assembly = typeof(ResponsePipeline).Assembly;
        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
            return [];

        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    /// <inheritdoc />
    public async Task StreamResponseAsync(IMessageSink sink, string text, Stopwatch? ttfaTimer = null)
    {
        // Split into sentences for streaming
        var sentences = SplitIntoSentences(text);

        // One sender owns the sink; producers enqueue payloads to a bounded channel.
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
                    if (!sink.IsConnected)
                        break;
                    try
                    {
                        await sink.SendAsync(payload);
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
            await EnqueueAsync(AudioPayloads.CachePlay(cacheKey, loop: true));

            // Immediately send store message as fallback (client will cache if not already cached)
            await EnqueueAsync(AudioPayloads.CacheStore(cacheKey, paddedLoading, loading.SampleRate, loading.Channels, durationMs));

            _logger.LogDebug("Loading audio sent: {Key}", cacheKey);

            // Wait until loading is cancelled (when TTS is ready)
            await Task.Delay(Timeout.Infinite, loadingCts.Token);
        });

        var sentenceIndex = 0;
        var ttsStreamStarted = false;
        var firstTtsChunkEnqueued = false;
        long firstTtfaMs = 0;

        for (var i = 0; i < sentences.Count; i++)
        {
            sentenceIndex = i + 1;
            var trimmed = sentences[i].Trim();
            if (string.IsNullOrEmpty(trimmed))
                continue;

            if (!sink.IsConnected)
                break;

            if (!_tts.IsHealthy)
            {
                _logger.LogWarning("TTS became unhealthy mid-response");
                break;
            }

            try
            {
                var ttsChunkStart = ttfaTimer?.ElapsedMilliseconds ?? 0;

                var wavBytes = await _tts.GenerateSpeechAsync(trimmed, exaggeration: 0.6f, cancellationToken: allCts.Token);
                var ttsChunkMs = (ttfaTimer?.ElapsedMilliseconds ?? 0) - ttsChunkStart;

                if (wavBytes.Length == 0)
                    continue;

                if (!_audio.TryParsePcmWav(wavBytes, out var ttsWav) || ttsWav == null)
                {
                    _logger.LogWarning("TTS returned invalid WAV for sentence {Index}", sentenceIndex);
                    continue;
                }

                var ttsBytesPerSecond = ttsWav.SampleRate * ttsWav.Channels * 2;
                var ttsAudioMs = (int)Math.Round(ttsWav.Pcm.Length * 1000.0 / Math.Max(1, ttsBytesPerSecond));

                _logger.LogDebug("TTS [{Index}/{Total}] {Ms}ms gen -> {AudioMs}ms audio",
                    sentenceIndex, sentences.Count, ttsChunkMs, ttsAudioMs);

                // First TTS audio available: stop loading audio
                if (!ttsStreamStarted)
                {
                    ttsStreamStarted = true;
                    loadingCts.Cancel();
                    try { await loadingTask; } catch { /* ignore */ }
                }

                // Calculate optimal tempo to match playback duration to next sentence's generation time
                var tempo = 1.0f;
                if (_tts is TtsService service && service.Config.TempoAdjustmentEnabled && i + 1 < sentences.Count)
                {
                    var nextSentence = sentences[i + 1].Trim();
                    var estimatedNextGenMs = nextSentence.Length * service.Config.AvgMsPerChar;

                    var desiredTempo = ttsAudioMs / estimatedNextGenMs;
                    tempo = Math.Clamp(desiredTempo, service.Config.MinTempo, service.Config.MaxTempo);
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
                await EnqueueAsync(AudioPayloads.Sentence(
                    paddedWav,
                    sentenceIndex,
                    sentences.Count,
                    adjustedDurationMs,
                    ttsWav.SampleRate,
                    tempo));

                if (!firstTtsChunkEnqueued && ttfaTimer is not null)
                {
                    firstTtsChunkEnqueued = true;
                    firstTtfaMs = ttfaTimer.ElapsedMilliseconds;
                }
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

        await sink.SendAsync(AudioPayloads.ResponseComplete());

        // Single consolidated response log
        if (ttfaTimer != null)
        {
            _logger.LogInformation("RESPONSE: {Count} sentence(s) | TTFA: {TtfaMs}ms | Total: {TotalMs}ms",
                sentenceIndex, firstTtfaMs, ttfaTimer.ElapsedMilliseconds);
        }
    }

    /// <summary>
    /// Split text into sentences using regex.
    /// </summary>
    private static List<string> SplitIntoSentences(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        var pattern = @"(?<=[.!?])\s+";
        var sentences = Regex.Split(text.Trim(), pattern)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();

        if (sentences.Count == 0)
        {
            sentences.Add(text.Trim());
        }

        return sentences;
    }

    // ========================================================================
    // Streaming API (for agent-driven incremental responses)
    // ========================================================================

    /// <inheritdoc />
    public Task<StreamingContext> BeginStreamingAsync(IMessageSink sink, Stopwatch? pipelineTimer = null)
    {
        var context = new StreamingContext
        {
            Sink = sink,
            PipelineTimer = pipelineTimer
        };

        // Start loading audio (will be cancelled when first sentence arrives)
        _ = PlayLoadingAudioAsync(context);

        return Task.FromResult(context);
    }

    /// <summary>
    /// Play loading audio until cancelled.
    /// </summary>
    private async Task PlayLoadingAudioAsync(StreamingContext context)
    {
        try
        {
            if (!_audio.TryParsePcmWav(_loadingAudio, out var loading) || loading == null)
                return;

            const string cacheKey = "loading_v2";

            var paddedLoading = _audio.AddSilencePadding(_loadingAudio, paddingMs: 150);
            var durationMs = (int)Math.Round(loading.Pcm.Length * 1000.0 / (loading.SampleRate * loading.Channels * 2.0)) + 150;

            await context.Sink.SendAsync(AudioPayloads.CachePlay(cacheKey, loop: true));
            await context.Sink.SendAsync(AudioPayloads.CacheStore(cacheKey, paddedLoading, loading.SampleRate, loading.Channels, durationMs));

            _logger.LogDebug("Loading audio started");

            // Wait until cancelled
            await Task.Delay(Timeout.Infinite, context.LoadingCts.Token);
        }
        catch (OperationCanceledException)
        {
            // Expected - loading was cancelled by first sentence
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Loading audio task ended");
        }
    }

    /// <inheritdoc />
    public async Task StreamSentenceAsync(StreamingContext context, string sentence)
    {
        var trimmed = sentence.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return;

        if (!context.Sink.IsConnected)
            return;

        if (!_tts.IsHealthy)
        {
            _logger.LogWarning("TTS became unhealthy mid-response");
            return;
        }

        context.SentenceIndex++;

        try
        {
            var ttsStart = context.PipelineTimer?.ElapsedMilliseconds ?? 0;

            var wavBytes = await _tts.GenerateSpeechAsync(trimmed, exaggeration: 0.6f);
            var ttsMs = (context.PipelineTimer?.ElapsedMilliseconds ?? 0) - ttsStart;

            if (wavBytes.Length == 0)
                return;

            if (!_audio.TryParsePcmWav(wavBytes, out var ttsWav) || ttsWav == null)
            {
                _logger.LogWarning("TTS returned invalid WAV for sentence {Index}", context.SentenceIndex);
                return;
            }

            var ttsBytesPerSecond = ttsWav.SampleRate * ttsWav.Channels * 2;
            var ttsAudioMs = (int)Math.Round(ttsWav.Pcm.Length * 1000.0 / Math.Max(1, ttsBytesPerSecond));

            _logger.LogDebug("TTS [{Index}] {Ms}ms gen -> {AudioMs}ms audio",
                context.SentenceIndex, ttsMs, ttsAudioMs);

            // First sentence: stop loading audio
            if (!context.FirstSentenceSent)
            {
                context.FirstSentenceSent = true;
                context.LoadingCts.Cancel();

                if (context.PipelineTimer != null)
                {
                    context.FirstTtfaMs = context.PipelineTimer.ElapsedMilliseconds;
                }
            }

            // Add silence padding
            var paddedWav = _audio.AddSilencePadding(wavBytes, paddingMs: 150);
            var adjustedDurationMs = ttsAudioMs + 150;

            // Send sentence (we don't know total count with streaming, pass 0)
            await context.Sink.SendAsync(AudioPayloads.Sentence(
                paddedWav,
                context.SentenceIndex,
                0, // Unknown total in streaming mode
                adjustedDurationMs,
                ttsWav.SampleRate,
                1.0f));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "TTS failed for sentence {Index}", context.SentenceIndex);
        }
    }

    /// <inheritdoc />
    public async Task EndStreamingAsync(StreamingContext context)
    {
        // Cancel loading if it never got cancelled (no sentences)
        try { context.LoadingCts.Cancel(); } catch { /* ignore */ }

        await context.Sink.SendAsync(AudioPayloads.ResponseComplete());

        if (context.PipelineTimer != null)
        {
            _logger.LogInformation("RESPONSE: {Count} sentence(s) | TTFA: {TtfaMs}ms | Total: {TotalMs}ms",
                context.SentenceIndex,
                context.FirstTtfaMs,
                context.PipelineTimer.ElapsedMilliseconds);
        }
    }
}
