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
}
