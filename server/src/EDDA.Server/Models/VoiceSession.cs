using System.Diagnostics;
using EDDA.Server.Services;

namespace EDDA.Server.Models;

/// <summary>
/// Encapsulates all state for a single voice interaction session.
/// Manages the input pipeline (STT) and will manage the output pipeline (LLM + TTS) in the future.
/// 
/// Design notes:
/// - Fully self-contained: no static state, can be instantiated per-connection
/// - Thread-safe: internal lock protects state transitions
/// - Event-driven timeouts: uses CancellationTokenSource instead of polling
/// - Horizontally scalable: session isolation allows future multi-instance deployment
/// </summary>
public sealed class VoiceSession : IDisposable
{
    private readonly IWhisperService _whisper;
    private readonly AudioConfig _config;
    private readonly ILogger _logger;
    
    private readonly object _lock = new();
    private readonly List<string> _transcriptionQueue = [];
    private readonly MemoryStream _audioBuffer = new();
    
    private InputState _inputState = InputState.Idle;
    private OutputState _outputState = OutputState.Idle;
    private CancellationTokenSource? _waitTimeoutCts;
    private Stopwatch? _pipelineTimer;
    private bool _disposed;

    /// <summary>
    /// Fired when the WaitingForMore timeout expires and we have transcriptions ready.
    /// The handler receives the combined transcription text and pipeline timer.
    /// </summary>
    public event Func<string, Stopwatch?, Task>? ResponseReady;

    /// <summary>
    /// Fired when the input state changes. Useful for logging/debugging.
    /// </summary>
    public event Action<InputState, InputState>? InputStateChanged;

    /// <summary>
    /// Current input pipeline state.
    /// </summary>
    public InputState CurrentInputState
    {
        get { lock (_lock) return _inputState; }
    }

    /// <summary>
    /// Current output pipeline state.
    /// </summary>
    public OutputState CurrentOutputState
    {
        get { lock (_lock) return _outputState; }
    }

    /// <summary>
    /// Number of transcriptions currently queued.
    /// </summary>
    public int QueuedTranscriptionCount
    {
        get { lock (_lock) return _transcriptionQueue.Count; }
    }

    public VoiceSession(
        IWhisperService whisper,
        AudioConfig config,
        ILogger logger)
    {
        _whisper = whisper;
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// Handle incoming audio chunk from the client.
    /// Transitions to Listening state if not already there.
    /// </summary>
    public async Task HandleAudioChunkAsync(byte[] audioData)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        
        await _audioBuffer.WriteAsync(audioData);

        lock (_lock)
        {
            if (_inputState != InputState.Listening)
            {
                var prev = _inputState;
                _inputState = InputState.Listening;
                
                // Cancel any pending timeout (user started speaking again)
                _waitTimeoutCts?.Cancel();
                
                _logger.LogDebug("INPUT: {Prev} -> Listening", prev);
                InputStateChanged?.Invoke(prev, _inputState);
            }
        }
    }

    /// <summary>
    /// Handle end-of-speech signal from the client.
    /// Performs STT transcription and transitions to WaitingForMore.
    /// </summary>
    public async Task HandleEndSpeechAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        
        if (_audioBuffer.Length == 0)
            return;

        // Start pipeline timer when user finishes speaking
        _pipelineTimer = Stopwatch.StartNew();
        
        // Extract audio and reset buffer
        var audioData = _audioBuffer.ToArray();
        _audioBuffer.SetLength(0);

        var audioBytes = audioData.Length;
        var audioSeconds = audioBytes / (double)_config.BytesPerSecond;

        var sttStart = _pipelineTimer.ElapsedMilliseconds;
        var transcription = await _whisper.TranscribeAsync(audioData);
        var sttMs = _pipelineTimer.ElapsedMilliseconds - sttStart;
        var rtf = audioSeconds > 0 ? audioSeconds * 1000.0 / sttMs : 0;

        _logger.LogInformation("STT: {AudioSec:F1}s audio -> {Ms}ms ({Rtf:F0}x RT)",
            audioSeconds, sttMs, rtf);

        lock (_lock)
        {
            if (!string.IsNullOrWhiteSpace(transcription))
            {
                _transcriptionQueue.Add(transcription);
            }

            var prev = _inputState;
            
            if (_transcriptionQueue.Count > 0)
            {
                _inputState = InputState.WaitingForMore;
                _logger.LogDebug("INPUT: {Prev} -> WaitingForMore (queued: {Count})", prev, _transcriptionQueue.Count);
                
                // Start the timeout - event-driven, not polling!
                StartWaitTimeout();
            }
            else
            {
                _inputState = InputState.Idle;
                _logger.LogDebug("INPUT: {Prev} -> Idle (empty transcription, queue empty)", prev);
            }
            
            if (prev != _inputState)
                InputStateChanged?.Invoke(prev, _inputState);
        }
    }

    /// <summary>
    /// Start the WaitingForMore timeout. If it expires without interruption,
    /// fires the ResponseReady event with queued transcriptions.
    /// </summary>
    private void StartWaitTimeout()
    {
        // Cancel any existing timeout
        _waitTimeoutCts?.Cancel();
        _waitTimeoutCts = new CancellationTokenSource();
        
        var token = _waitTimeoutCts.Token;
        var timer = _pipelineTimer;
        
        // Fire-and-forget the timeout task
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay((int)_config.WaitingForMoreTimeoutMs, token);
                
                // Timeout expired naturally - process the queue
                await OnWaitTimeoutExpiredAsync(timer);
            }
            catch (OperationCanceledException)
            {
                // Timeout was cancelled (user spoke again) - do nothing
            }
        }, token);
    }

    /// <summary>
    /// Called when the WaitingForMore timeout expires.
    /// Combines queued transcriptions and fires ResponseReady.
    /// </summary>
    private async Task OnWaitTimeoutExpiredAsync(Stopwatch? timer)
    {
        string? combinedText = null;
        
        lock (_lock)
        {
            if (_inputState != InputState.WaitingForMore || _transcriptionQueue.Count == 0)
                return;
            
            combinedText = string.Join(" ", _transcriptionQueue).Trim();
            _transcriptionQueue.Clear();
            
            var prev = _inputState;
            _inputState = InputState.Idle;
            
            _logger.LogDebug("INPUT: {Prev} -> Idle (timeout, firing response)", prev);
            InputStateChanged?.Invoke(prev, _inputState);
        }

        if (!string.IsNullOrEmpty(combinedText) && ResponseReady != null)
        {
            _logger.LogInformation("QUERY: \"{Query}\"", 
                combinedText.Length > 80 ? combinedText[..80] + "..." : combinedText);
            await ResponseReady.Invoke(combinedText, timer);
        }
    }

    /// <summary>
    /// Cancel any pending timeout. Call this when the session is being disposed
    /// or when you need to interrupt the wait.
    /// </summary>
    public void CancelPendingTimeout()
    {
        _waitTimeoutCts?.Cancel();
    }

    /// <summary>
    /// Reset the session to idle state, clearing all buffers and queues.
    /// </summary>
    public void Reset()
    {
        lock (_lock)
        {
            _waitTimeoutCts?.Cancel();
            _transcriptionQueue.Clear();
            _audioBuffer.SetLength(0);
            _inputState = InputState.Idle;
            _outputState = OutputState.Idle;
            _pipelineTimer = null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        
        _waitTimeoutCts?.Cancel();
        _waitTimeoutCts?.Dispose();
        _audioBuffer.Dispose();
    }
}
