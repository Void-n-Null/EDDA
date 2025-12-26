using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Whisper.net;
using Whisper.net.Ggml;
using System;
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

// Whisper model setup
WhisperFactory? _whisperFactory = null;

async Task InitializeWhisper()
{
    var envPath = Environment.GetEnvironmentVariable("WHISPER_MODEL_PATH");
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
        Console.WriteLine($"[SRV] Model not found at {absoluteModelPath}. Downloading...");
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
            LogDebug("Program.cs:silenceCheck", "Silence check iteration", new { bufferSize = audioStream.Length, timeSinceLastSpeech, threshold = 1.5 }, "C");
            // #endregion
            
            // Trigger transcription when: we have audio AND no speech detected for 1.5 seconds
            if (audioStream.Length > 16000 && timeSinceLastSpeech > 1.5) // Min 1 second of audio at 16kHz
            {
                var audioData = audioStream.ToArray();
                audioStream.SetLength(0); // Clear buffer immediately
                
                // #region agent log
                LogDebug("Program.cs:silenceCheck", "Silence detected, triggering transcription", new { audioBytes = audioData.Length }, "C");
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
                        
                        // Simple energy-based Voice Activity Detection
                        var energy = CalculateAudioEnergy(audioData);
                        var isSpeech = energy > 500; // Threshold for speech detection (tune as needed)
                        
                        await audioStream.WriteAsync(audioData, 0, audioData.Length);
                        lastChunkTime = DateTime.UtcNow;
                        
                        if (isSpeech)
                        {
                            lastSpeechTime = DateTime.UtcNow; // Update only when we detect actual speech
                        }
                        
                        // #region agent log
                        LogDebug("Program.cs:HandleConnection", "Audio chunk processed", new { decodedBytes = audioData.Length, totalBufferSize = audioStream.Length, energy, isSpeech }, "B");
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
        
        using var processor = _whisperFactory.CreateBuilder()
            .WithLanguage("en")
            .Build();

        // #region agent log
        LogDebug("Program.cs:TranscribeAudio", "Processor created, preparing WAV", null, "E");
        // #endregion

        // Prepare memory stream for WAV data
        using var memoryStream = new MemoryStream();
        await WriteWavHeader(memoryStream, audioData, 16000);
        memoryStream.Position = 0;

        // #region agent log
        LogDebug("Program.cs:TranscribeAudio", "Starting ProcessAsync", new { wavStreamSize = memoryStream.Length }, "E,F");
        // #endregion

        Console.Write("[SRV] TRANSCRIPTION: ");
        var fullText = new StringBuilder();
        var segmentCount = 0;

        await foreach (var segment in processor.ProcessAsync(memoryStream))
        {
            segmentCount++;
            Console.Write(segment.Text);
            fullText.Append(segment.Text);
            
            // #region agent log
            LogDebug("Program.cs:TranscribeAudio", "Segment received", new { segmentNum = segmentCount, text = segment.Text, start = segment.Start, end = segment.End }, "F");
            // #endregion
        }
        Console.WriteLine(); // Newline after segments
        
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

double CalculateAudioEnergy(byte[] audioData)
{
    // Calculate RMS energy of 16-bit PCM audio
    double sum = 0;
    for (int i = 0; i < audioData.Length - 1; i += 2)
    {
        short sample = (short)(audioData[i] | (audioData[i + 1] << 8));
        sum += sample * sample;
    }
    return Math.Sqrt(sum / (audioData.Length / 2));
}

async Task WriteWavHeader(Stream stream, byte[] pcmData, int sampleRate)
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
