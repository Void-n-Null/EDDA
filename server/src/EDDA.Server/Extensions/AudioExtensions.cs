using System.Text;

namespace EDDA.Server.Extensions;

/// <summary>
/// Audio utility extensions for PCM/WAV handling.
/// </summary>
public static class AudioExtensions
{
    /// <summary>
    /// Writes a WAV header and PCM data to the stream.
    /// Assumes 16-bit mono PCM format.
    /// Used to convert raw audio data to a WAV file for Whisper.net.
    /// </summary>
    public static void WriteWavWithHeader(this Stream stream, byte[] pcmData, int sampleRate)
    {
        using var bw = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);

        bw.Write(Encoding.ASCII.GetBytes("RIFF"));
        bw.Write(36 + pcmData.Length);
        bw.Write(Encoding.ASCII.GetBytes("WAVE"));
        bw.Write(Encoding.ASCII.GetBytes("fmt "));
        bw.Write(16);
        bw.Write((short)1);  // PCM format
        bw.Write((short)1);  // Mono
        bw.Write(sampleRate);
        bw.Write(sampleRate * 2);  // Byte rate (sample rate * block align)
        bw.Write((short)2);  // Block align (channels * bytes per sample)
        bw.Write((short)16); // Bits per sample
        bw.Write(Encoding.ASCII.GetBytes("data"));
        bw.Write(pcmData.Length);
        bw.Write(pcmData);
    }
}

