using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
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
    private readonly AudioConfig _config;
    private readonly ILogger<WebSocketHandler> _logger;
    private readonly byte[] _chimeAudio;
    
    public WebSocketHandler(IWhisperService whisper, AudioConfig config, ILogger<WebSocketHandler> logger)
    {
        _whisper = whisper;
        _config = config;
        _logger = logger;
        
        // Load the chime audio file (from bin/Debug|Release/net8.0/ go up 4 levels to project root)
        var chimePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "electronic_chime.wav"));
        if (File.Exists(chimePath))
        {
            _chimeAudio = File.ReadAllBytes(chimePath);
            _logger.LogInformation("Loaded chime audio: {Path} ({Bytes} bytes)", chimePath, _chimeAudio.Length);
        }
        else
        {
            _logger.LogWarning("Chime audio not found at {Path}", chimePath);
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
                _logger.LogInformation("READY TO RESPOND: \"{Query}\"", combinedQuery);
                
                // Send chime to indicate we're about to respond
                await SendAudioPlaybackAsync(webSocket);
                
                // TODO: Wire this into LLM response pipeline
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
    
    private async Task SendAudioPlaybackAsync(WebSocket webSocket)
    {
        if (_chimeAudio.Length == 0)
        {
            _logger.LogWarning("No chime audio loaded, skipping playback");
            return;
        }
        
        var message = new
        {
            type = "audio_playback",
            data = Convert.ToBase64String(_chimeAudio),
            format = "wav"
        };
        
        var json = JsonSerializer.Serialize(message);
        var bytes = Encoding.UTF8.GetBytes(json);
        
        await webSocket.SendAsync(
            new ArraySegment<byte>(bytes),
            WebSocketMessageType.Text,
            endOfMessage: true,
            CancellationToken.None);
        
        _logger.LogInformation("Sent audio playback ({Bytes} bytes)", _chimeAudio.Length);
    }
}

