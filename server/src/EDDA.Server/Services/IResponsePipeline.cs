using System.Diagnostics;

namespace EDDA.Server.Services;

/// <summary>
/// Orchestrates response generation and streaming to the client.
/// Coordinates loading audio, TTS generation, and sentence-by-sentence delivery.
/// </summary>
public interface IResponsePipeline
{
    /// <summary>
    /// Stream a text response to the client via TTS.
    /// Handles loading audio, sentence splitting, tempo adjustment, and delivery.
    /// </summary>
    /// <param name="sink">Message sink to send payloads to</param>
    /// <param name="text">Text to convert to speech and stream</param>
    /// <param name="pipelineTimer">Optional stopwatch for TTFA measurement</param>
    Task StreamResponseAsync(IMessageSink sink, string text, Stopwatch? pipelineTimer = null);

    /// <summary>
    /// Begin streaming a response. Plays loading audio until first sentence arrives.
    /// Call StreamSentenceAsync for each sentence, then EndStreamingAsync when done.
    /// </summary>
    /// <param name="sink">Message sink to send payloads to</param>
    /// <param name="pipelineTimer">Optional stopwatch for TTFA measurement</param>
    /// <returns>Streaming context to pass to subsequent calls</returns>
    Task<StreamingContext> BeginStreamingAsync(IMessageSink sink, Stopwatch? pipelineTimer = null);

    /// <summary>
    /// Stream a single sentence. Cancels loading audio on first call.
    /// </summary>
    /// <param name="context">Context from BeginStreamingAsync</param>
    /// <param name="sentence">Sentence text to TTS and stream</param>
    Task StreamSentenceAsync(StreamingContext context, string sentence);

    /// <summary>
    /// End the streaming response. Sends response_complete message.
    /// </summary>
    /// <param name="context">Context from BeginStreamingAsync</param>
    Task EndStreamingAsync(StreamingContext context);
}

/// <summary>
/// Context for an in-progress streaming response.
/// </summary>
public class StreamingContext
{
    public required IMessageSink Sink { get; init; }
    public Stopwatch? PipelineTimer { get; init; }
    public CancellationTokenSource LoadingCts { get; } = new();
    public int SentenceIndex { get; set; }
    public bool FirstSentenceSent { get; set; }
    public long FirstTtfaMs { get; set; }
}
