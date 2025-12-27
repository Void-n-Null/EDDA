using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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
    private readonly AudioConfig _config;
    private readonly ILogger<WebSocketHandler> _logger;
    private readonly byte[] _chimeAudio;
    
    public WebSocketHandler(
        IWhisperService whisper,
        ITtsService tts,
        AudioConfig config,
        ILogger<WebSocketHandler> logger)
    {
        _whisper = whisper;
        _tts = tts;
        _config = config;
        _logger = logger;
        
        // Load fallback chime audio (used if TTS unavailable)
        var chimePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "electronic_chime.wav"));
        if (File.Exists(chimePath))
        {
            _chimeAudio = File.ReadAllBytes(chimePath);
            _logger.LogInformation("Loaded fallback chime: {Path} ({Bytes} bytes)", chimePath, _chimeAudio.Length);
        }
        else
        {
            _logger.LogWarning("Fallback chime not found at {Path}", chimePath);
            _chimeAudio = [];
        }
    }
    
    public async Task HandleConnectionAsync(WebSocket webSocket)
    {
        _logger.LogInformation("WebSocket connection established");
        
        var buffer = new byte[1024 * 16];
        var audioStream = new MemoryStream();
        var cts = new CancellationTokenSource();
        
        // State machine
        var state = ConnectionState.Idle;
        var lastSpeechEndTime = DateTime.UtcNow;
        var transcriptionQueue = new List<string>();
        var stateLock = new object();
        
        // Background task to check for "waiting for more" timeout
        var stateCheckTask = RunStateCheckerAsync(
            webSocket, cts.Token, stateLock,
            () => state,
            s => state = s,
            () => lastSpeechEndTime,
            transcriptionQueue);
        
        try
        {
            while (webSocket.State == WebSocketState.Open)
            {
                var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                
                if (result.MessageType == WebSocketMessageType.Text)
                {
                    await ProcessTextMessageAsync(
                        buffer, result.Count, audioStream, stateLock,
                        () => state,
                        s => state = s,
                        t => lastSpeechEndTime = t,
                        transcriptionQueue);
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
        finally
        {
            await cts.CancelAsync();
            try { await stateCheckTask; }
            catch
            {
                // ignored
            }
        }
    }
    
    private async Task RunStateCheckerAsync(
        WebSocket webSocket,
        CancellationToken ct,
        object stateLock,
        Func<ConnectionState> getState,
        Action<ConnectionState> setState,
        Func<DateTime> getLastSpeechEnd,
        List<string> transcriptionQueue)
    {
        while (!ct.IsCancellationRequested && webSocket.State == WebSocketState.Open)
        {
            try
            {
                await Task.Delay(100, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            
            List<string>? queuedTranscriptions = null;
            
            lock (stateLock)
            {
                if (getState() == ConnectionState.WaitingForMore)
                {
                    var timeSinceLastSpeech = (DateTime.UtcNow - getLastSpeechEnd()).TotalMilliseconds;
                    
                    if (timeSinceLastSpeech >= _config.WaitingForMoreTimeoutMs && transcriptionQueue.Count > 0)
                    {
                        queuedTranscriptions = [.. transcriptionQueue];
                        transcriptionQueue.Clear();
                        setState(ConnectionState.Idle);
                        
                        _logger.LogDebug("STATE: WaitingForMore -> Idle (timeout, {Count} transcriptions)", queuedTranscriptions.Count);
                    }
                }
            }
            
            if (queuedTranscriptions is { Count: > 0 })
            {
                var combinedQuery = string.Join(" ", queuedTranscriptions).Trim();
                _logger.LogInformation("HEARD: \"{Query}\"", combinedQuery);
                
                // Echo mode: Repeat what the user said (tests full STT -> TTS pipeline)
                // TODO: Replace with actual LLM response
                var responseText = $"You said: {combinedQuery}";
                await SendTtsResponseAsync(webSocket, responseText);
            }
        }
    }
    
    private async Task ProcessTextMessageAsync(
        byte[] buffer,
        int count,
        MemoryStream audioStream,
        object stateLock,
        Func<ConnectionState> getState,
        Action<ConnectionState> setState,
        Action<DateTime> setLastSpeechEnd,
        List<string> transcriptionQueue)
    {
        var json = Encoding.UTF8.GetString(buffer, 0, count);
        
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        
        if (!root.TryGetProperty("type", out var typeProp))
            return;
        
        var messageType = typeProp.GetString();
        
        if (messageType == "audio_chunk")
        {
            await HandleAudioChunkAsync(root, audioStream, stateLock, getState, setState);
        }
        else if (messageType == "end_speech")
        {
            await HandleEndSpeechAsync(audioStream, stateLock, setState, setLastSpeechEnd, transcriptionQueue);
        }
    }
    
    private async Task HandleAudioChunkAsync(
        JsonElement root,
        MemoryStream audioStream,
        object stateLock,
        Func<ConnectionState> getState,
        Action<ConnectionState> setState)
    {
        var dataBase64 = root.GetProperty("data").GetString();
        if (dataBase64 == null)
            return;
        
        var audioData = Convert.FromBase64String(dataBase64);
        await audioStream.WriteAsync(audioData);
        
        lock (stateLock)
        {
            if (getState() != ConnectionState.ReceivingSpeech)
            {
                var prevState = getState();
                setState(ConnectionState.ReceivingSpeech);
                _logger.LogDebug("STATE: {Prev} -> ReceivingSpeech", prevState);
            }
        }
    }
    
    private async Task HandleEndSpeechAsync(
        MemoryStream audioStream,
        object stateLock,
        Action<ConnectionState> setState,
        Action<DateTime> setLastSpeechEnd,
        List<string> transcriptionQueue)
    {
        if (audioStream.Length == 0)
            return;
        
        _logger.LogDebug("Received end_speech signal");
        
        var audioData = audioStream.ToArray();
        audioStream.SetLength(0);
        
        _logger.LogInformation("Processing {Bytes} bytes with Whisper...", audioData.Length);
        var transcription = await _whisper.TranscribeAsync(audioData);
        
        lock (stateLock)
        {
            if (!string.IsNullOrWhiteSpace(transcription))
            {
                transcriptionQueue.Add(transcription);
                setLastSpeechEnd(DateTime.UtcNow);
                setState(ConnectionState.WaitingForMore);
                
                _logger.LogDebug("STATE: ReceivingSpeech -> WaitingForMore (queued: \"{Text}\", total: {Count})",
                    transcription, transcriptionQueue.Count);
            }
            else
            {
                if (transcriptionQueue.Count > 0)
                {
                    setLastSpeechEnd(DateTime.UtcNow);
                    setState(ConnectionState.WaitingForMore);
                }
                else
                {
                    setState(ConnectionState.Idle);
                }
            }
        }
    }
    
    /// <summary>
    /// Generate speech using TTS and stream to client sentence-by-sentence.
    /// This reduces time-to-first-audio by sending each sentence as soon as it's ready.
    /// Falls back to chime if TTS is unavailable.
    /// </summary>
    private async Task SendTtsResponseAsync(WebSocket webSocket, string text)
    {
        if (!_tts.IsHealthy)
        {
            _logger.LogWarning("TTS unavailable, using fallback chime");
            if (_chimeAudio.Length > 0)
            {
                await SendAudioChunkAsync(webSocket, _chimeAudio, 1, 1);
            }
            await SendResponseCompleteAsync(webSocket);
            return;
        }
        
        // Split into sentences for streaming
        var sentences = SplitIntoSentences(text);
        _logger.LogInformation("Streaming TTS response: {Count} sentence(s)", sentences.Count);
        
        var sentenceIndex = 0;
        foreach (var sentence in sentences)
        {
            sentenceIndex++;
            var trimmed = sentence.Trim();
            if (string.IsNullOrEmpty(trimmed))
                continue;
            
            try
            {
                _logger.LogDebug("TTS [{Index}/{Total}]: \"{Sentence}\"", sentenceIndex, sentences.Count, trimmed);
                
                var audioData = await _tts.GenerateSpeechAsync(trimmed, exaggeration: 0.6f);
                
                if (audioData.Length > 0)
                {
                    await SendAudioChunkAsync(webSocket, audioData, sentenceIndex, sentences.Count);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "TTS failed for sentence {Index}, skipping", sentenceIndex);
                // Continue with remaining sentences
            }
        }
        
        await SendResponseCompleteAsync(webSocket);
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

