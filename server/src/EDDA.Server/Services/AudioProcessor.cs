using System.Diagnostics;
using System.Text;

namespace EDDA.Server.Services;

/// <summary>
/// Service for WAV audio file processing operations.
/// Handles parsing, building, padding, and tempo adjustment of WAV files.
/// </summary>
public class AudioProcessor : IAudioProcessor
{
    private readonly ILogger<AudioProcessor> _logger;
    
    public AudioProcessor(ILogger<AudioProcessor> logger)
    {
        _logger = logger;
    }
    
    /// <inheritdoc />
    public bool TryParsePcmWav(byte[] wav, out WavPcm? parsed)
    {
        parsed = null;
        if (wav.Length < 44) return false;
        if (Encoding.ASCII.GetString(wav, 0, 4) != "RIFF") return false;
        if (Encoding.ASCII.GetString(wav, 8, 4) != "WAVE") return false;

        var offset = 12;
        ushort audioFormat = 0;
        ushort channels = 0;
        int sampleRate = 0;
        ushort bitsPerSample = 0;
        int? dataOffset = null;
        int? dataSize = null;

        while (offset + 8 <= wav.Length)
        {
            var chunkId = Encoding.ASCII.GetString(wav, offset, 4);
            var chunkSize = BitConverter.ToInt32(wav, offset + 4);
            offset += 8;

            if (chunkSize < 0 || offset + chunkSize > wav.Length)
                return false;

            if (chunkId == "fmt ")
            {
                if (chunkSize < 16) return false;
                audioFormat = BitConverter.ToUInt16(wav, offset + 0);
                channels = BitConverter.ToUInt16(wav, offset + 2);
                sampleRate = BitConverter.ToInt32(wav, offset + 4);
                bitsPerSample = BitConverter.ToUInt16(wav, offset + 14);
            }
            else if (chunkId == "data")
            {
                dataOffset = offset;
                dataSize = chunkSize;
                break;
            }

            // Word-aligned chunks
            offset += chunkSize + (chunkSize % 2);
        }

        if (audioFormat != 1) return false; // PCM only
        if (channels <= 0 || sampleRate <= 0) return false;
        if (bitsPerSample != 16) return false;
        if (dataOffset is null || dataSize is null) return false;
        if (dataOffset.Value + dataSize.Value > wav.Length) return false;

        var pcm = new byte[dataSize.Value];
        Buffer.BlockCopy(wav, dataOffset.Value, pcm, 0, dataSize.Value);
        parsed = new WavPcm(pcm, sampleRate, channels, bitsPerSample);
        return true;
    }

    /// <inheritdoc />
    public byte[] AddSilencePadding(byte[] wavBytes, int paddingMs = 150)
    {
        if (!TryParsePcmWav(wavBytes, out var wav) || wav == null)
            return wavBytes;
        
        // Calculate padding size in bytes
        var bytesPerMs = (wav.SampleRate * wav.Channels * (wav.BitsPerSample / 8f)) / 1000.0;
        var paddingBytes = (int)(paddingMs * bytesPerMs);
        
        // Ensure even number of bytes for 16-bit audio
        if (paddingBytes % 2 != 0)
            paddingBytes++;
        
        // Create silence (zeros)
        var silence = new byte[paddingBytes];
        
        // Combine silence + original PCM
        var paddedPcm = new byte[silence.Length + wav.Pcm.Length];
        Buffer.BlockCopy(silence, 0, paddedPcm, 0, silence.Length);
        Buffer.BlockCopy(wav.Pcm, 0, paddedPcm, silence.Length, wav.Pcm.Length);
        
        // Rebuild WAV with new PCM data
        return BuildWavFile(paddedPcm, wav.SampleRate, wav.Channels, wav.BitsPerSample);
    }
    
    /// <inheritdoc />
    public byte[] BuildWavFile(byte[] pcmData, int sampleRate, int channels, int bitsPerSample)
    {
        var blockAlign = channels * (bitsPerSample / 8);
        var byteRate = sampleRate * blockAlign;
        var dataSize = pcmData.Length;
        var fileSize = 36 + dataSize;
        
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        
        // RIFF header
        writer.Write("RIFF"u8.ToArray());
        writer.Write(fileSize);
        writer.Write("WAVE"u8.ToArray());
        
        // fmt chunk
        writer.Write("fmt "u8.ToArray());
        writer.Write(16); // fmt chunk size
        writer.Write((short)1); // PCM format
        writer.Write((short)channels);
        writer.Write(sampleRate);
        writer.Write(byteRate);
        writer.Write((short)blockAlign);
        writer.Write((short)bitsPerSample);
        
        // data chunk
        writer.Write("data"u8.ToArray());
        writer.Write(dataSize);
        writer.Write(pcmData);
        
        return ms.ToArray();
    }
    
    /// <inheritdoc />
    public async Task<byte[]> AdjustTempoAsync(byte[] wavBytes, float tempo, CancellationToken ct = default)
    {
        if (Math.Abs(tempo - 1.0f) < 0.01f)
            return wavBytes;
        
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments = $"-loglevel error -f wav -i pipe:0 -filter:a \"atempo={tempo:F3}\" -f wav pipe:1",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        process.Start();
        
        // All three streams must be handled concurrently to prevent deadlock:
        // - stdin: we write input data
        // - stdout: ffmpeg writes output data (buffer can fill if we don't read)
        // - stderr: ffmpeg writes errors (buffer can fill if we don't read)
        
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        
        using var output = new MemoryStream();
        var stdoutTask = process.StandardOutput.BaseStream.CopyToAsync(output, ct);
        
        // Write stdin in background, then close it
        var stdinTask = Task.Run(async () =>
        {
            await process.StandardInput.BaseStream.WriteAsync(wavBytes, ct);
            process.StandardInput.Close();
        }, ct);
        
        // Wait for all I/O to complete
        await Task.WhenAll(stdinTask, stdoutTask, stderrTask);
        await process.WaitForExitAsync(ct);
        
        if (process.ExitCode != 0)
        {
            var stderr = await stderrTask;
            _logger.LogWarning("ffmpeg tempo adjustment failed (exit {Code}): {Error}", process.ExitCode, stderr);
            return wavBytes; // Return original on failure
        }
        
        return output.ToArray();
    }
}
