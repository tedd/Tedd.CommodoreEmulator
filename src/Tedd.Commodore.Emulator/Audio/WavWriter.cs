using System;
using System.IO;

namespace Tedd.Commodore.Emulator.Audio;

/// <summary>
/// Minimal writer for 16-bit PCM RIFF/WAVE files (canonical 44-byte header followed by little-endian samples).
/// Used by tests to dump SID output for listening; no dependencies.
/// </summary>
public static class WavWriter
{
    /// <summary>Size of the canonical RIFF/WAVE header in bytes.</summary>
    public const int HeaderSize = 44;

    /// <summary>Encodes interleaved 16-bit samples as a complete WAV file.</summary>
    public static byte[] Encode(int sampleRate, ReadOnlySpan<short> samples, int channels = 1)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels));

        int dataBytes = samples.Length * 2;
        var bytes = new byte[HeaderSize + dataBytes];
        var span = bytes.AsSpan();

        WriteAscii(span, 0, "RIFF");
        WriteInt32(span, 4, 36 + dataBytes);
        WriteAscii(span, 8, "WAVE");
        WriteAscii(span, 12, "fmt ");
        WriteInt32(span, 16, 16);                       // fmt chunk size
        WriteInt16(span, 20, 1);                        // PCM
        WriteInt16(span, 22, (short)channels);
        WriteInt32(span, 24, sampleRate);
        WriteInt32(span, 28, sampleRate * channels * 2); // byte rate
        WriteInt16(span, 32, (short)(channels * 2));    // block align
        WriteInt16(span, 34, 16);                       // bits per sample
        WriteAscii(span, 36, "data");
        WriteInt32(span, 40, dataBytes);

        int o = HeaderSize;
        for (int i = 0; i < samples.Length; i++, o += 2)
        {
            bytes[o] = (byte)samples[i];
            bytes[o + 1] = (byte)(samples[i] >> 8);
        }
        return bytes;
    }

    /// <summary>Writes a WAV file to <paramref name="path"/>, creating the directory if needed.</summary>
    public static void Save(string path, int sampleRate, ReadOnlySpan<short> samples, int channels = 1)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllBytes(path, Encode(sampleRate, samples, channels));
    }

    /// <summary>Writes a WAV file to a stream.</summary>
    public static void Write(Stream stream, int sampleRate, ReadOnlySpan<short> samples, int channels = 1)
    {
        if (stream is null) throw new ArgumentNullException(nameof(stream));
        stream.Write(Encode(sampleRate, samples, channels));
    }

    private static void WriteAscii(Span<byte> span, int offset, string text)
    {
        for (int i = 0; i < text.Length; i++)
            span[offset + i] = (byte)text[i];
    }

    private static void WriteInt32(Span<byte> span, int offset, int value)
    {
        span[offset] = (byte)value;
        span[offset + 1] = (byte)(value >> 8);
        span[offset + 2] = (byte)(value >> 16);
        span[offset + 3] = (byte)(value >> 24);
    }

    private static void WriteInt16(Span<byte> span, int offset, short value)
    {
        span[offset] = (byte)value;
        span[offset + 1] = (byte)(value >> 8);
    }
}
