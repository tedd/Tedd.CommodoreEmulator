using System;
using System.Runtime.InteropServices;
using NUnit.Framework;
using Tedd.Commodore.Embedding;
using Tedd.Commodore.Emulator.Video;
using Tedd.Commodore.Hosting;

namespace Tedd.Commodore.Tests.Embedding;

[TestFixture]
public class RawFrameTests
{
    private const int FullWidth = 20, FullHeight = 12, VisibleX = 3, VisibleY = 2, Width = 13, Height = 7;

    /// <summary>Colour number of visible pixel (x, y) in <see cref="Frame"/>.</summary>
    private static int ColorAt(int x, int y) => (x + 3 * y) % 16;

    /// <summary>A raster with a visible window inside it; outside the window every pixel is a colour no palette has.</summary>
    private static VideoFrame Frame(VideoSource source = VideoSource.VicII)
    {
        var palette = source == VideoSource.Vdc ? Vdc8563.Palette : VicII.Palette;
        var pixels = new uint[FullWidth * FullHeight];
        Array.Fill(pixels, 0xFF123456u);
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
                pixels[(VisibleY + y) * FullWidth + VisibleX + x] = palette[ColorAt(x, y)];
        return new VideoFrame(pixels, 42, source, FullWidth, FullHeight, VisibleX, VisibleY, Width, Height);
    }

    [Test]
    public void View_Of_A_VideoFrame_Covers_The_Visible_Area()
    {
        var frame = new RawFrame(Frame());
        Assert.That(frame.IsEmpty, Is.False);
        Assert.That((frame.Width, frame.Height, frame.FrameNumber, frame.Source), Is.EqualTo((Width, Height, 42L, VideoSource.VicII)));
        Assert.That(frame.GetRow(0)[0], Is.EqualTo(VicII.Palette[ColorAt(0, 0)]));
        Assert.That(frame.GetRow(Height - 1)[Width - 1], Is.EqualTo(VicII.Palette[ColorAt(Width - 1, Height - 1)]));
        Assert.That(frame.GetRowBytes(FramePixelFormat.Bgra8888), Is.EqualTo(Width * 4));
        Assert.That(frame.GetSizeInBytes(FramePixelFormat.Indexed8), Is.EqualTo(Width * Height));
        Assert.That(frame.Palette.ToArray(), Is.EqualTo(VicII.Palette));
        Assert.Throws<ArgumentOutOfRangeException>(() => frame.GetRow(Height));
    }

    [TestCase(FramePixelFormat.Bgra8888)]
    [TestCase(FramePixelFormat.Rgba8888)]
    [TestCase(FramePixelFormat.Indexed8)]
    public void CopyTo_Writes_The_Documented_Byte_Order(FramePixelFormat format)
    {
        var frame = new RawFrame(Frame());
        var bytes = new byte[frame.GetSizeInBytes(format)];
        frame.CopyTo(bytes, format);
        int bpp = format.BytesPerPixel();
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
            {
                int o = (y * Width + x) * bpp;
                int color = ColorAt(x, y);
                uint argb = VicII.Palette[color];
                byte r = (byte)(argb >> 16), g = (byte)(argb >> 8), b = (byte)argb;
                var actual = bytes.AsSpan(o, bpp).ToArray();
                var expected = format switch
                {
                    FramePixelFormat.Bgra8888 => new[] { b, g, r, (byte)255 },
                    FramePixelFormat.Rgba8888 => new[] { r, g, b, (byte)255 },
                    _ => new[] { (byte)color },
                };
                Assert.That(actual, Is.EqualTo(expected), $"pixel ({x}, {y})");
            }
    }

    [Test]
    public void CopyTo_Honours_The_Row_Pitch_And_Leaves_The_Padding_Alone()
    {
        var frame = new RawFrame(Frame());
        int rowBytes = frame.GetRowBytes(FramePixelFormat.Rgba8888);
        int pitch = rowBytes + 12;
        var tight = new byte[frame.GetSizeInBytes(FramePixelFormat.Rgba8888)];
        frame.CopyTo(tight, FramePixelFormat.Rgba8888);

        // The last row needs no padding after it: this is the smallest destination that fits.
        var padded = new byte[pitch * (Height - 1) + rowBytes];
        Array.Fill(padded, (byte)0xCD);
        frame.CopyTo(padded, pitch, FramePixelFormat.Rgba8888);
        for (int y = 0; y < Height; y++)
        {
            Assert.That(padded.AsSpan(y * pitch, rowBytes).ToArray(), Is.EqualTo(tight.AsSpan(y * rowBytes, rowBytes).ToArray()), $"row {y}");
            if (y < Height - 1)
                Assert.That(padded.AsSpan(y * pitch + rowBytes, pitch - rowBytes).ToArray(), Is.All.EqualTo((byte)0xCD), $"padding after row {y}");
        }

        Assert.Throws<ArgumentException>(() => frame.CopyTo(new byte[padded.Length - 1], pitch, FramePixelFormat.Rgba8888));
        Assert.Throws<ArgumentOutOfRangeException>(() => frame.CopyTo(new byte[4096], rowBytes - 1, FramePixelFormat.Rgba8888));
    }

    [TestCase(FramePixelFormat.Bgra8888)]
    [TestCase(FramePixelFormat.Indexed8)]
    public void CopyTo_Flipped_Puts_The_Bottom_Row_First(FramePixelFormat format)
    {
        var frame = new RawFrame(Frame());
        int rowBytes = frame.GetRowBytes(format);
        var upright = new byte[frame.GetSizeInBytes(format)];
        var flipped = new byte[upright.Length];
        frame.CopyTo(upright, format);
        frame.CopyTo(flipped, format, flipVertically: true);
        for (int y = 0; y < Height; y++)
            Assert.That(flipped.AsSpan(y * rowBytes, rowBytes).ToArray(), Is.EqualTo(upright.AsSpan((Height - 1 - y) * rowBytes, rowBytes).ToArray()));
    }

    [Test]
    public void Vdc_Frames_Index_The_Vdc_Palette()
    {
        var frame = new RawFrame(Frame(VideoSource.Vdc));
        Assert.That(frame.Palette.ToArray(), Is.EqualTo(Vdc8563.Palette));
        var indexes = new byte[frame.GetSizeInBytes(FramePixelFormat.Indexed8)];
        frame.CopyTo(indexes, FramePixelFormat.Indexed8);
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
                Assert.That(indexes[y * Width + x], Is.EqualTo(ColorAt(x, y)));
    }

    [Test]
    public void Indexed8_Maps_Colours_Outside_The_Palette_To_Zero()
    {
        var pixels = new[] { VicII.Palette[5], 0xFF010203u, VicII.Palette[5], VicII.Palette[15] };
        var frame = new RawFrame(new VideoFrame(pixels, 1, VideoSource.VicII, 4, 1, 0, 0, 4, 1));
        var indexes = new byte[4];
        frame.CopyTo(indexes, FramePixelFormat.Indexed8);
        Assert.That(indexes, Is.EqualTo(new byte[] { 5, 0, 5, 15 }));
    }

    [Test]
    public void CopyTo_Pointer_Writes_The_Same_Bytes_As_The_Span_Overload()
    {
        var frame = new RawFrame(Frame());
        int pitch = frame.GetRowBytes(FramePixelFormat.Bgra8888) + 4;
        int length = pitch * Height;
        var expected = new byte[length];
        frame.CopyTo(expected, pitch, FramePixelFormat.Bgra8888);

        IntPtr memory = Marshal.AllocHGlobal(length);
        try
        {
            Marshal.Copy(new byte[length], 0, memory, length);
            frame.CopyTo(memory, length, pitch, FramePixelFormat.Bgra8888);
            var actual = new byte[length];
            Marshal.Copy(memory, actual, 0, length);
            Assert.That(actual, Is.EqualTo(expected));
            Assert.Throws<ArgumentException>(() => frame.CopyTo(memory, pitch, pitch, FramePixelFormat.Bgra8888));
        }
        finally
        {
            Marshal.FreeHGlobal(memory);
        }
        Assert.Throws<ArgumentNullException>(() => frame.CopyTo(IntPtr.Zero, length, pitch, FramePixelFormat.Bgra8888));
    }

    [Test]
    public void Empty_Frame_Cannot_Be_Copied()
    {
        var frame = default(RawFrame);
        Assert.That(frame.IsEmpty, Is.True);
        Assert.Throws<InvalidOperationException>(() => frame.CopyTo(new byte[16], FramePixelFormat.Bgra8888));
        Assert.Throws<InvalidOperationException>(() => frame.GetRow(0));
    }

    [Test]
    public void Bytes_Per_Pixel()
    {
        Assert.That(FramePixelFormat.Bgra8888.BytesPerPixel(), Is.EqualTo(4));
        Assert.That(FramePixelFormat.Rgba8888.BytesPerPixel(), Is.EqualTo(4));
        Assert.That(FramePixelFormat.Indexed8.BytesPerPixel(), Is.EqualTo(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => ((FramePixelFormat)99).BytesPerPixel());
    }
}
