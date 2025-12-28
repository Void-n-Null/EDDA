namespace EDDA.Server.Messages;

/// <summary>
/// Factory methods for audio-related WebSocket message payloads.
/// </summary>
public static class AudioPayloads
{
    /// <summary>
    /// Start a streaming audio session (used for loading audio).
    /// </summary>
    public static object StreamStart(string stream, int sampleRate, int channels, float tempo = 1.0f)
        => new
        {
            type = "audio_stream_start",
            stream,
            sample_rate = sampleRate,
            channels,
            sample_format = "s16le",
            tempo
        };

    /// <summary>
    /// A chunk of PCM audio data in a stream.
    /// </summary>
    public static object StreamChunk(string stream, byte[] pcmChunk)
        => new
        {
            type = "audio_stream_chunk",
            stream,
            data = Convert.ToBase64String(pcmChunk)
        };

    /// <summary>
    /// End a streaming audio session.
    /// </summary>
    public static object StreamEnd(string stream)
        => new
        {
            type = "audio_stream_end",
            stream
        };

    /// <summary>
    /// A complete WAV sentence for the client to queue and play.
    /// </summary>
    public static object Sentence(byte[] wavBytes, int sentenceIndex, int totalSentences, 
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

    /// <summary>
    /// Request the client to play audio from its cache.
    /// </summary>
    public static object CachePlay(string cacheKey, bool loop = false)
        => new
        {
            type = "audio_cache_play",
            cache_key = cacheKey,
            loop
        };

    /// <summary>
    /// Store audio in the client's cache.
    /// </summary>
    public static object CacheStore(string cacheKey, byte[] wavBytes, int sampleRate, int channels, int durationMs)
        => new
        {
            type = "audio_cache_store",
            cache_key = cacheKey,
            data = Convert.ToBase64String(wavBytes),
            sample_rate = sampleRate,
            channels,
            duration_ms = durationMs
        };

    /// <summary>
    /// Signal that the response is complete.
    /// </summary>
    public static object ResponseComplete()
        => new { type = "response_complete" };
}
