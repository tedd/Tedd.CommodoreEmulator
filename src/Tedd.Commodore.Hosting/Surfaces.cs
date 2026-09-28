using System;
using Tedd.Commodore.Emulator.Video;

namespace Tedd.Commodore.Hosting;

/// <summary>Which video chip produced a <see cref="VideoFrame"/>.</summary>
public enum VideoSource
{
    /// <summary>The VIC-II (40 column) picture: 504 x 312 raster, 384 x 272 visible.</summary>
    VicII,
    /// <summary>The C128's VDC (80 column) picture: 768 x 272, all of it visible.</summary>
    Vdc,
}

/// <summary>
/// One rendered frame. The pixel buffer is owned by the emulator and reused for the next frame, so sinks must
/// copy what they need before returning from <see cref="IVideoSink.PresentFrame"/>. The geometry travels with
/// the frame: a C128 session switches between the VIC-II's 384 x 272 picture and the VDC's 768 x 272 one, so
/// sinks should size their target from <see cref="Width"/>/<see cref="Height"/> rather than assume the C64's.
/// </summary>
public readonly struct VideoFrame
{
    /// <summary>A VIC-II frame (the C64 geometry).</summary>
    public VideoFrame(uint[] pixels, long frameNumber)
        : this(pixels, frameNumber, VideoSource.VicII, VicII.FrameWidth, VicII.FrameHeight,
            VicII.VisibleArea.X, VicII.VisibleArea.Y, VicII.VisibleArea.Width, VicII.VisibleArea.Height)
    {
    }

    public VideoFrame(uint[] pixels, long frameNumber, VideoSource source, int fullWidth, int fullHeight, int visibleX, int visibleY, int visibleWidth, int visibleHeight)
    {
        Pixels = pixels;
        FrameNumber = frameNumber;
        Source = source;
        FullWidth = fullWidth;
        FullHeight = fullHeight;
        VisibleX = visibleX;
        VisibleY = visibleY;
        Width = visibleWidth;
        Height = visibleHeight;
    }

    /// <summary>A VDC frame (the whole 768 x 272 buffer is visible).</summary>
    public static VideoFrame ForVdc(uint[] pixels, long frameNumber) =>
        new(pixels, frameNumber, VideoSource.Vdc, Vdc8563.FrameWidth, Vdc8563.FrameHeight, 0, 0, Vdc8563.FrameWidth, Vdc8563.FrameHeight);

    /// <summary>Full raster ARGB pixels, 0xAARRGGBB with alpha = 0xFF, <see cref="FullWidth"/> x <see cref="FullHeight"/>.</summary>
    public uint[] Pixels { get; }
    /// <summary>Frame counter since power-on.</summary>
    public long FrameNumber { get; }
    /// <summary>The chip that produced the frame.</summary>
    public VideoSource Source { get; }
    /// <summary>Width of the full raster buffer.</summary>
    public int FullWidth { get; }
    /// <summary>Height of the full raster buffer.</summary>
    public int FullHeight { get; }
    /// <summary>Left edge of the visible picture inside the buffer.</summary>
    public int VisibleX { get; }
    /// <summary>Top edge of the visible picture inside the buffer.</summary>
    public int VisibleY { get; }
    /// <summary>Width of the visible picture (384 for the VIC-II, 768 for the VDC).</summary>
    public int Width { get; }
    /// <summary>Height of the visible picture (272).</summary>
    public int Height { get; }

    /// <summary>Copies the visible picture (Width x Height, top-left first) into <paramref name="destination"/>.</summary>
    public void CopyVisible(Span<uint> destination)
    {
        if (destination.Length < Width * Height)
            throw new ArgumentException("Destination too small", nameof(destination));
        for (int y = 0; y < Height; y++)
            Pixels.AsSpan((VisibleY + y) * FullWidth + VisibleX, Width).CopyTo(destination.Slice(y * Width, Width));
    }

    /// <summary>Copies the visible picture as RGBA bytes (R, G, B, A order, as used by browser canvases and most GPU textures).</summary>
    public void CopyVisibleRgba(Span<byte> destination)
    {
        if (destination.Length < Width * Height * 4)
            throw new ArgumentException("Destination too small", nameof(destination));
        int o = 0;
        for (int y = 0; y < Height; y++)
        {
            int src = (VisibleY + y) * FullWidth + VisibleX;
            for (int x = 0; x < Width; x++)
            {
                uint p = Pixels[src + x];
                destination[o++] = (byte)(p >> 16);
                destination[o++] = (byte)(p >> 8);
                destination[o++] = (byte)p;
                destination[o++] = 0xFF;
            }
        }
    }

    /// <summary>Copies the visible picture as BGRA bytes (little-endian ARGB, as used by WPF/Direct3D/SDL ARGB8888).</summary>
    public void CopyVisibleBgra(Span<byte> destination)
    {
        if (destination.Length < Width * Height * 4)
            throw new ArgumentException("Destination too small", nameof(destination));
        int o = 0;
        for (int y = 0; y < Height; y++)
        {
            int src = (VisibleY + y) * FullWidth + VisibleX;
            for (int x = 0; x < Width; x++)
            {
                uint p = Pixels[src + x];
                destination[o++] = (byte)p;
                destination[o++] = (byte)(p >> 8);
                destination[o++] = (byte)(p >> 16);
                destination[o++] = 0xFF;
            }
        }
    }
}

/// <summary>Receives every rendered frame. Called on the thread that runs the emulation.</summary>
public interface IVideoSink
{
    void PresentFrame(in VideoFrame frame);
}

/// <summary>Receives 16-bit mono PCM produced by the SID. Called on the thread that runs the emulation.</summary>
public interface IAudioSink
{
    /// <summary>Sample rate of the samples that will be written (fixed for the lifetime of the session).</summary>
    int SampleRate { get; }
    void Write(ReadOnlySpan<short> samples);
    /// <summary>Drops queued audio (used when entering warp mode or after a pause).</summary>
    void Clear();
}

/// <summary>A video sink that does nothing (headless use).</summary>
public sealed class NullVideoSink : IVideoSink
{
    public static readonly NullVideoSink Instance = new();
    public void PresentFrame(in VideoFrame frame) { }
}

/// <summary>An audio sink that discards everything.</summary>
public sealed class NullAudioSink : IAudioSink
{
    public NullAudioSink(int sampleRate = 44100) => SampleRate = sampleRate;
    public int SampleRate { get; }
    public void Write(ReadOnlySpan<short> samples) { }
    public void Clear() { }
}

/// <summary>
/// An audio sink decorator that keeps the most recent samples in a ring buffer (for visualizers) and forwards
/// everything to an optional inner sink.
/// </summary>
public sealed class AudioTap : IAudioSink
{
    private readonly IAudioSink? _inner;
    private readonly short[] _ring;
    private int _write;
    private long _total;
    private readonly object _lock = new();

    public AudioTap(IAudioSink? inner, int sampleRate, int capacity = 16384)
    {
        _inner = inner;
        SampleRate = inner?.SampleRate ?? sampleRate;
        _ring = new short[capacity];
    }

    public int SampleRate { get; }
    public int Capacity => _ring.Length;
    /// <summary>Total number of samples that passed through.</summary>
    public long TotalSamples => _total;

    public void Write(ReadOnlySpan<short> samples)
    {
        lock (_lock)
        {
            foreach (var s in samples)
            {
                _ring[_write] = s;
                _write = (_write + 1) % _ring.Length;
            }
            _total += samples.Length;
        }
        _inner?.Write(samples);
    }

    public void Clear() => _inner?.Clear();

    /// <summary>Copies the latest <c>destination.Length</c> samples (oldest first) into the destination.</summary>
    public void CopyLatest(Span<short> destination)
    {
        lock (_lock)
        {
            int n = Math.Min(destination.Length, _ring.Length);
            int start = (_write - n + _ring.Length) % _ring.Length;
            for (int i = 0; i < n; i++)
                destination[destination.Length - n + i] = _ring[(start + i) % _ring.Length];
            if (n < destination.Length)
                destination[..(destination.Length - n)].Clear();
        }
    }
}
