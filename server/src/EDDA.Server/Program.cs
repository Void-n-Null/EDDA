using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Whisper.net;
using Whisper.net.Ggml;
using System;
using System.Diagnostics;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

// #region agent log helper
void LogDebug(string location, string message, object? data = null, string? hypothesisId = null)
{
    try
    {
        var logEntry = JsonSerializer.Serialize(new
        {
            sessionId = "debug-session",
            runId = "initial",
            hypothesisId = hypothesisId ?? "",
            location,
            message,
            data,
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        });
        // Log to both console (for immediate visibility) and file
        // Console.WriteLine($"[DEBUG] {logEntry}");
        File.AppendAllText("/tmp/edda-debug.log", logEntry + "\n");
    }
    catch (Exception ex) 
    { 
        Console.WriteLine($"[DEBUG-ERROR] Failed to log: {ex.Message}");
    }
}
// #endregion

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// Tunables (env overrides)
var whisperThreads = ParseIntEnv("WHISPER_THREADS", defaultValue: Math.Max(1, Environment.ProcessorCount));
var silenceSeconds = ParseDoubleEnv("WHISPER_SILENCE_SECONDS", defaultValue: 1.5);
var maxAudioSeconds = ParseDoubleEnv("WHISPER_MAX_AUDIO_SECONDS", defaultValue: 8.0);
var sampleRate = ParseIntEnv("WHISPER_SAMPLE_RATE", defaultValue: 16000);
var bytesPerSecond = sampleRate * 2; // 16-bit mono PCM
var maxAudioBytes = (long)(maxAudioSeconds * bytesPerSecond);
var minAudioBytes = bytesPerSecond; // ~1s minimum

// Whisper model setup
WhisperFactory? _whisperFactory = null;
string? _whisperModelPath = null;

async Task InitializeWhisper()
{
    var envPath = Environment.GetEnvironmentVariable("WHISPER_MODEL_PATH");
    var isCustomModelPath = !string.IsNullOrEmpty(envPath);
    var absoluteModelPath = !string.IsNullOrEmpty(envPath) 
        ? envPath 
        : Path.GetFullPath("models/ggml-large-v3-turbo.bin");

    var modelDir = Path.GetDirectoryName(absoluteModelPath);
    if (!string.IsNullOrEmpty(modelDir))
    {
        Directory.CreateDirectory(modelDir);
    }

    if (!File.Exists(absoluteModelPath))
    {
        if (isCustomModelPath)
        {
            Console.WriteLine($"[SRV] CRITICAL: WHISPER_MODEL_PATH was set but model file does not exist: {absoluteModelPath}");
            return;
        }

        Console.WriteLine($"[SRV] Model not found at {absoluteModelPath}. Attempting download...");
        try 
        {
             // Fallback to manual download URL if not available via GgmlType enum yet (v1.9.0 might support it)
             // But large-v3-turbo might be GgmlType.LargeV3Turbo or similar. 
             // To be safe and since we are downloading it manually in the plan anyway, 
             // we will trust the file exists or fail if it doesn't.
             // But the code snippet in plan suggested using WhisperGgmlDownloader.
             // I'll implement a robust check.
             
             // Check if we can download it via library, if not, rely on script.
             // Given the manual download in the plan, I'll assume it might be there.
             // But let's add a downloader fallback just in case script failed.
             
             using var modelStream = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(GgmlType.LargeV3Turbo);
             using var fileWriter = File.OpenWrite(absoluteModelPath);
             await modelStream.CopyToAsync(fileWriter);
             Console.WriteLine("[SRV] Model downloaded successfully.");
        }
        catch (Exception ex)
        {
             Console.WriteLine($"[SRV] Failed to download model: {ex.Message}. Ensure it is present at {absoluteModelPath}");
        }
    }

    if (File.Exists(absoluteModelPath))
    {
        try 
        {
            _whisperFactory = WhisperFactory.FromPath(absoluteModelPath);
            _whisperModelPath = absoluteModelPath;
            Console.WriteLine($"[SRV] Whisper initialized with model: {absoluteModelPath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SRV] Failed to initialize Whisper factory: {ex.Message}");
        }
    }
    else
    {
         Console.WriteLine("[SRV] CRITICAL: Whisper model missing!");
    }
}

// Initialize on startup
await InitializeWhisper();

app.UseWebSockets();

app.Map("/ws", async context =>
{
    if (context.WebSockets.IsWebSocketRequest)
    {
        using var webSocket = await context.WebSockets.AcceptWebSocketAsync();
        Console.WriteLine("[SRV] WebSocket connection established.");
        // #region agent log
        LogDebug("Program.cs:WS", "WebSocket connection established", new { clientIP = context.Connection.RemoteIpAddress?.ToString() }, "A");
        // #endregion
        await HandleConnection(webSocket);
    }
    else
    {
        context.Response.StatusCode = 400;
    }
});

async Task HandleConnection(WebSocket webSocket)
{
    var buffer = new byte[1024 * 16];
    var audioStream = new MemoryStream();
    var lastChunkTime = DateTime.UtcNow;
    var lastSpeechTime = DateTime.UtcNow; // Track when we last detected speech (not just any audio)
    var cts = new CancellationTokenSource();

    // Background task to check for silence and process audio
    var silenceCheckTask = Task.Run(async () =>
    {
        // #region agent log
        LogDebug("Program.cs:silenceCheck", "Silence checker started", null, "C");
        // #endregion
        while (!cts.Token.IsCancellationRequested && webSocket.State == WebSocketState.Open)
        {
            await Task.Delay(100, cts.Token); // Check every 100ms
            
            // #region agent log
            var timeSinceLastSpeech = (DateTime.UtcNow - lastSpeechTime).TotalSeconds;
            LogDebug("Program.cs:silenceCheck", "Silence check iteration", new { bufferSize = audioStream.Length, timeSinceLastSpeech, threshold = silenceSeconds, maxAudioBytes }, "C");
            // #endregion
            
            // Trigger transcription when:
            // - We have enough audio AND no speech detected for N seconds
            // - OR we have buffered too much audio (prevents huge, slow transcriptions)
            var shouldFlushOnSilence = audioStream.Length > minAudioBytes && timeSinceLastSpeech > silenceSeconds;
            var shouldFlushOnMax = audioStream.Length >= maxAudioBytes;

            if (shouldFlushOnSilence || shouldFlushOnMax)
            {
                var audioData = audioStream.ToArray();
                audioStream.SetLength(0); // Clear buffer immediately
                
                // #region agent log
                LogDebug("Program.cs:silenceCheck", "Triggering transcription", new { audioBytes = audioData.Length, shouldFlushOnSilence, shouldFlushOnMax }, "C");
                // #endregion
                
                Console.WriteLine($"[SRV] Processing {audioData.Length} bytes of audio with Whisper...");
                await TranscribeAudio(audioData);
            }
        }
        // #region agent log
        LogDebug("Program.cs:silenceCheck", "Silence checker stopped", null, "C");
        // #endregion
    }, cts.Token);

    try
    {
        while (webSocket.State == WebSocketState.Open)
        {
            var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
            
            if (result.MessageType == WebSocketMessageType.Text)
            {
                var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                // #region agent log
                LogDebug("Program.cs:HandleConnection", "Received text message", new { jsonLength = json.Length }, "A,B");
                // #endregion
                
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                
                if (root.TryGetProperty("type", out var typeProp) && typeProp.GetString() == "audio_chunk")
                {
                    var dataBase64 = root.GetProperty("data").GetString();
                    if (dataBase64 != null)
                    {
                        var audioData = Convert.FromBase64String(dataBase64);
                        
                        // Client now handles VAD - if we receive audio, it's speech
                        await audioStream.WriteAsync(audioData, 0, audioData.Length);
                        lastChunkTime = DateTime.UtcNow;
                        lastSpeechTime = DateTime.UtcNow; // All received audio is speech from client's VAD
                        
                        // #region agent log
                        LogDebug("Program.cs:HandleConnection", "Audio chunk processed", new { decodedBytes = audioData.Length, totalBufferSize = audioStream.Length }, "B");
                        // #endregion
                    }
                }
            }
            else if (result.MessageType == WebSocketMessageType.Close)
            {
                await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                Console.WriteLine("[SRV] WebSocket closed by client.");
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[SRV] Connection error: {ex.Message}");
    }
    finally
    {
        cts.Cancel(); // Stop silence checker
        try { await silenceCheckTask; } catch { }
    }
}

async Task TranscribeAudio(byte[] audioData)
{
    // #region agent log
    LogDebug("Program.cs:TranscribeAudio", "TranscribeAudio called", new { audioBytes = audioData.Length, factoryNull = _whisperFactory == null }, "D");
    // #endregion
    
    if (_whisperFactory == null)
    {
        Console.WriteLine("[SRV] Whisper not initialized, skipping transcription.");
        // #region agent log
        LogDebug("Program.cs:TranscribeAudio", "Whisper factory is null, aborting", null, "D");
        // #endregion
        return;
    }

    try
    {
        // #region agent log
        LogDebug("Program.cs:TranscribeAudio", "Creating Whisper processor", null, "E");
        // #endregion
        
        var whisperBuilder = _whisperFactory.CreateBuilder()
            .WithLanguage("en");

        whisperBuilder = ConfigureWhisperThreads(whisperBuilder, whisperThreads);

        using var processor = whisperBuilder.Build();

        // #region agent log
        LogDebug("Program.cs:TranscribeAudio", "Processor created, preparing WAV", null, "E");
        // #endregion

        // Prepare memory stream for WAV data
        using var memoryStream = new MemoryStream();
        WriteWavHeader(memoryStream, audioData, sampleRate);
        memoryStream.Position = 0;

        // #region agent log
        LogDebug("Program.cs:TranscribeAudio", "Starting ProcessAsync", new { wavStreamSize = memoryStream.Length }, "E,F");
        // #endregion

        Console.Write("[SRV] TRANSCRIPTION: ");
        var fullText = new StringBuilder();
        var segmentCount = 0;
        var sw = Stopwatch.StartNew();

        await foreach (var segment in processor.ProcessAsync(memoryStream))
        {
            segmentCount++;
            Console.Write(segment.Text);
            fullText.Append(segment.Text);
            
            // #region agent log
            LogDebug("Program.cs:TranscribeAudio", "Segment received", new { segmentNum = segmentCount, text = segment.Text, start = segment.Start, end = segment.End }, "F");
            // #endregion
        }

        sw.Stop();
        Console.WriteLine(); // Newline after segments

        // Telemetry: how long did STT take vs how much audio we fed it?
        var elapsedMs = Math.Max(1, sw.ElapsedMilliseconds);
        var audioSeconds = audioData.Length / (double)bytesPerSecond;
        var msPerSecondAudio = audioSeconds > 0 ? (elapsedMs / audioSeconds) : double.PositiveInfinity;
        var realtimeFactor = audioSeconds > 0 ? ((audioSeconds * 1000.0) / elapsedMs) : 0.0;
        var bytesPerMs = audioData.Length / (double)elapsedMs;

        var telemetry = new
        {
            audioBytes = audioData.Length,
            audioSeconds = Math.Round(audioSeconds, 3),
            sampleRate,
            whisperThreads,
            modelPath = _whisperModelPath ?? "(unknown)",
            elapsedMs,
            realtimeFactor = Math.Round(realtimeFactor, 3),
            msPerSecondAudio = Math.Round(msPerSecondAudio, 1),
            bytesPerMs = Math.Round(bytesPerMs, 1),
            segments = segmentCount,
            textChars = fullText.Length
        };

        Console.WriteLine($"[SRV] STT telemetry: bytes={telemetry.audioBytes} audio_s={telemetry.audioSeconds} ms={telemetry.elapsedMs} xRT={telemetry.realtimeFactor} ms_per_s={telemetry.msPerSecondAudio} bytes_per_ms={telemetry.bytesPerMs} threads={telemetry.whisperThreads} sr={telemetry.sampleRate} segs={telemetry.segments}");
        LogDebug("Program.cs:TranscribeAudio", "STT telemetry", telemetry, "T");
        
        // #region agent log
        LogDebug("Program.cs:TranscribeAudio", "Transcription complete", new { totalSegments = segmentCount, fullText = fullText.ToString() }, "F");
        // #endregion
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[SRV] Whisper error: {ex.Message}");
        // #region agent log
        LogDebug("Program.cs:TranscribeAudio", "Exception in transcription", new { error = ex.Message, stackTrace = ex.StackTrace }, "D,E");
        // #endregion
    }
}

void WriteWavHeader(Stream stream, byte[] pcmData, int sampleRate)
{
    // Write WAV header directly to the stream
    using var bw = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);

    bw.Write(Encoding.ASCII.GetBytes("RIFF"));
    bw.Write(36 + pcmData.Length);
    bw.Write(Encoding.ASCII.GetBytes("WAVE"));
    bw.Write(Encoding.ASCII.GetBytes("fmt "));
    bw.Write(16);
    bw.Write((short)1); // PCM
    bw.Write((short)1); // Mono
    bw.Write(sampleRate);
    bw.Write(sampleRate * 2); // Byte rate
    bw.Write((short)2); // Block align
    bw.Write((short)16); // Bits per sample
    bw.Write(Encoding.ASCII.GetBytes("data"));
    bw.Write(pcmData.Length);
    bw.Write(pcmData);
}

Console.WriteLine("EDDA Server starting...");
app.Run("http://0.0.0.0:8080");

static int ParseIntEnv(string key, int defaultValue)
{
    var v = Environment.GetEnvironmentVariable(key);
    return int.TryParse(v, out var parsed) && parsed > 0 ? parsed : defaultValue;
}

static double ParseDoubleEnv(string key, double defaultValue)
{
    var v = Environment.GetEnvironmentVariable(key);
    return double.TryParse(v, out var parsed) && parsed > 0 ? parsed : defaultValue;
}

static dynamic ConfigureWhisperThreads(dynamic builder, int threads)
{
    try
    {
        var t = (object)builder;
        var type = t.GetType();

        // Try common method names across versions without hard dependency.
        var withThreads = type.GetMethod("WithThreads", new[] { typeof(int) });
        if (withThreads != null)
        {
            return withThreads.Invoke(t, new object[] { threads })!;
        }

        var withMaxThreads = type.GetMethod("WithMaxThreads", new[] { typeof(int) });
        if (withMaxThreads != null)
        {
            return withMaxThreads.Invoke(t, new object[] { threads })!;
        }
    }
    catch
    {
        // Ignore; fall back to library defaults.
    }

    return builder;
}
