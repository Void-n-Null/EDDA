using System.Diagnostics;
using System.Text;
using EDDA.Server.Extensions;
using EDDA.Server.Models;
using Whisper.net;
using Whisper.net.Ggml;

namespace EDDA.Server.Services;

/// <summary>
/// Whisper.net-based transcription service with GPU acceleration support.
/// </summary>
public class WhisperService(AudioConfig config, ILogger<WhisperService> logger) : IWhisperService, IDisposable
{
    private WhisperFactory? _factory;

    public bool IsInitialized => _factory != null;

    public async Task InitializeAsync()
    {
        var modelPath = ResolveModelPath();
        
        if (!File.Exists(modelPath))
        {
            if (!string.IsNullOrEmpty(config.ModelPath))
            {
                logger.LogCritical("WHISPER_MODEL_PATH was set but model file does not exist: {Path}", modelPath);
                return;
            }
            
            logger.LogInformation("Model not found at {Path}. Attempting download...", modelPath);
            await DownloadModelAsync(modelPath);
        }
        
        if (File.Exists(modelPath))
        {
            try
            {
                _factory = WhisperFactory.FromPath(modelPath);
                logger.LogInformation("Whisper initialized with model: {Path}", modelPath);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to initialize Whisper factory");
            }
        }
        else
        {
            logger.LogCritical("Whisper model missing after download attempt");
        }
    }
    
    public async Task<string> TranscribeAsync(byte[] pcmAudio)
    {
        if (_factory == null)
        {
            logger.LogWarning("Whisper not initialized, skipping transcription");
            return "";
        }
        
        try
        {
            var builder = _factory.CreateBuilder().WithLanguage("en");
            ConfigureThreads(builder);

            await using var processor = builder.Build();
            using var wavStream = new MemoryStream();
            
            wavStream.WriteWavWithHeader(pcmAudio, config.SampleRate);
            wavStream.Position = 0;
            
            var fullText = new StringBuilder();
            var segmentCount = 0;
            var sw = Stopwatch.StartNew();
            
            await foreach (var segment in processor.ProcessAsync(wavStream))
            {
                segmentCount++;
                fullText.Append(segment.Text);
            }
            
            sw.Stop();
            LogTelemetry(pcmAudio.Length, sw.ElapsedMilliseconds, segmentCount, fullText.Length);
            
            return fullText.ToString().Trim();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Whisper transcription error");
            return "";
        }
    }
    
    public void Dispose()
    {
        _factory?.Dispose();
    }
    
    private string ResolveModelPath()
    {
        if (!string.IsNullOrEmpty(config.ModelPath))
            return config.ModelPath;
        
        var defaultPath = Path.GetFullPath("models/ggml-large-v3-turbo.bin");
        var modelDir = Path.GetDirectoryName(defaultPath);
        if (!string.IsNullOrEmpty(modelDir))
            Directory.CreateDirectory(modelDir);
        
        return defaultPath;
    }
    
    private async Task DownloadModelAsync(string targetPath)
    {
        try
        {
            await using var modelStream = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(GgmlType.LargeV3Turbo);
            await using var fileWriter = File.OpenWrite(targetPath);
            await modelStream.CopyToAsync(fileWriter);
            logger.LogInformation("Model downloaded successfully");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to download model. Ensure it is present at {Path}", targetPath);
        }
    }
    
    private void ConfigureThreads(WhisperProcessorBuilder builder)
    {
        try
        {
            var type = builder.GetType();
            
            var withThreads = type.GetMethod("WithThreads", [typeof(int)]);
            if (withThreads != null)
            {
                withThreads.Invoke(builder, [config.WhisperThreads]);
                return;
            }
            
            var withMaxThreads = type.GetMethod("WithMaxThreads", [typeof(int)]);
            if (withMaxThreads != null)
            {
                withMaxThreads.Invoke(builder, [config.WhisperThreads]);
            }
        }
        catch
        {
            // Fall back to library defaults
        }
    }
    
    private void LogTelemetry(int audioBytes, long elapsedMs, int segments, int textChars)
    {
        var audioSeconds = audioBytes / (double)config.BytesPerSecond;
        var realtimeFactor = audioSeconds > 0 ? (audioSeconds * 1000.0 / Math.Max(1, elapsedMs)) : 0.0;
        
        logger.LogInformation(
            "STT: {AudioBytes}b {AudioSeconds:F2}s -> {TextChars}ch in {ElapsedMs}ms ({RealtimeFactor:F1}xRT, {Segments} segmnts, {Threads} threads)",
            audioBytes,
            audioSeconds,
            textChars,
            elapsedMs,
            realtimeFactor,
            segments,
            config.WhisperThreads);
    }
}

