using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Tedd.Commodore.Emulator.Video;
using Tedd.Commodore.Hosting;

namespace Tedd.Commodore.Embedding;

/// <summary>
/// One emulator picture, to be written into memory the host owns: a mapped Vulkan staging buffer or linear
/// image, a locked Avalonia <c>WriteableBitmap</c>, a texture upload buffer, a plain byte array.
/// </summary>
/// <remarks>
/// <para>
/// The picture is the visible area only (384 x 272 for the VIC-II, 768 x 272 for the C128's VDC), top row first,
/// held as 0xAARRGGBB words. <see cref="CopyTo(Span{byte}, int, FramePixelFormat, bool)"/> converts while copying,
/// honouring the destination's row pitch, so it can go straight into a texture's memory.
/// </para>
/// <para>
/// A frame is a view, not a copy. One from <see cref="VideoOutput.TryAcquire"/> stays valid until the renderer's
/// next <see cref="VideoOutput.TryAcquire"/>. One made from a <see cref="VideoFrame"/> (inside your own
/// <see cref="IVideoSink"/>) looks at the emulator's raster and is only valid until the sink returns.
/// </para>
/// </remarks>
public readonly struct RawFrame
{
    private readonly uint[]? _pixels;
    private readonly int _offset;
    private readonly int _stride;

    /// <summary>A view of the visible part of <paramref name="frame"/> (nothing is copied).</summary>
    public RawFrame(in VideoFrame frame)
        : this(frame.Pixels, frame.VisibleY * frame.FullWidth + frame.VisibleX, frame.FullWidth,
            frame.Width, frame.Height, frame.FrameNumber, frame.Source)
    {
    }

    internal RawFrame(uint[] pixels, int offset, int stride, int width, int height, long frameNumber, VideoSource source)
    {
        _pixels = pixels;
        _offset = offset;
        _stride = stride;
        Width = width;
        Height = height;
        FrameNumber = frameNumber;
        Source = source;
    }

    /// <summary>True for <c>default(RawFrame)</c>, i.e. no picture has been acquired yet.</summary>
    public bool IsEmpty => _pixels is null;

    /// <summary>Width in pixels: 384 for the VIC-II, 768 for the VDC.</summary>
    public int Width { get; }

    /// <summary>Height in pixels (272).</summary>
    public int Height { get; }

    /// <summary>The emulator's frame counter (since power-on) for this picture.</summary>
    public long FrameNumber { get; }

    /// <summary>The chip that drew the picture; a C128 switches between the two with its 40/80 key.</summary>
    public VideoSource Source { get; }

    /// <summary>
    /// The 16 colours (0xAARRGGBB) of the chip that drew the picture: the VIC-II's (Pepto's PAL palette) or the
    /// VDC's RGBI colours. <see cref="FramePixelFormat.Indexed8"/> pixels are indexes into this table.
    /// </summary>
    public ReadOnlySpan<uint> Palette => Source == VideoSource.Vdc ? Vdc8563.Palette : VicII.Palette;

    /// <summary>Bytes in one tightly packed row of <paramref name="format"/>.</summary>
    public int GetRowBytes(FramePixelFormat format) => Width * format.BytesPerPixel();

    /// <summary>Bytes of the whole picture in <paramref name="format"/> without row padding (what a staging buffer needs).</summary>
    public int GetSizeInBytes(FramePixelFormat format) => GetRowBytes(format) * Height;

    /// <summary>Row <paramref name="y"/> (0 = top) as 0xAARRGGBB words.</summary>
    public ReadOnlySpan<uint> GetRow(int y)
    {
        if (_pixels is null) throw NoPicture();
        if ((uint)y >= (uint)Height) throw new ArgumentOutOfRangeException(nameof(y), y, $"The picture has {Height} rows");
        return new ReadOnlySpan<uint>(_pixels, _offset + y * _stride, Width);
    }

    /// <summary>Writes the picture tightly packed (row pitch = <see cref="GetRowBytes"/>) into <paramref name="destination"/>.</summary>
    public void CopyTo(Span<byte> destination, FramePixelFormat format, bool flipVertically = false) =>
        CopyTo(destination, GetRowBytes(format), format, flipVertically);

    /// <summary>
    /// Writes the picture into <paramref name="destination"/> in <paramref name="format"/>, row <c>y</c> starting at
    /// byte <c>y * <paramref name="rowPitch"/></c>. Bytes between the end of a row and the next row's start are
    /// left alone, so the picture can go into a larger surface or a texture whose rows are padded.
    /// </summary>
    /// <param name="destination">At least <c>rowPitch * (Height - 1) + GetRowBytes(format)</c> bytes.</param>
    /// <param name="rowPitch">Distance between the starts of two rows in bytes: Vulkan's
    /// <c>VkSubresourceLayout.rowPitch</c> (or <c>bufferRowLength</c> x 4), Avalonia's <c>ILockedFramebuffer.RowBytes</c>.</param>
    /// <param name="format">The byte layout to write.</param>
    /// <param name="flipVertically">Write the bottom row first (OpenGL and Unity textures are bottom-up; Vulkan, Direct3D and Avalonia are not).</param>
    public void CopyTo(Span<byte> destination, int rowPitch, FramePixelFormat format, bool flipVertically = false)
    {
        if (_pixels is null) throw NoPicture();
        int rowBytes = GetRowBytes(format);
        if (rowPitch < rowBytes)
            throw new ArgumentOutOfRangeException(nameof(rowPitch), rowPitch, $"A row of {Width} pixels in {format} needs {rowBytes} bytes");
        long required = (long)rowPitch * (Height - 1) + rowBytes;
        if (destination.Length < required)
            throw new ArgumentException($"{Width} x {Height} {format} at a row pitch of {rowPitch} needs {required} bytes, the destination has {destination.Length}", nameof(destination));

        var palette = Palette;
        uint lastColor = palette[0];
        byte lastIndex = 0;
        for (int y = 0; y < Height; y++)
        {
            var source = new ReadOnlySpan<uint>(_pixels, _offset + (flipVertically ? Height - 1 - y : y) * _stride, Width);
            var target = destination.Slice(y * rowPitch, rowBytes);
            switch (format)
            {
                case FramePixelFormat.Bgra8888:
                    WriteBgra(source, target);
                    break;
                case FramePixelFormat.Rgba8888:
                    WriteRgba(source, target);
                    break;
                default:
                    WriteIndexed(source, target, palette, ref lastColor, ref lastIndex);
                    break;
            }
        }
    }

    /// <summary>
    /// <see cref="CopyTo(Span{byte}, int, FramePixelFormat, bool)"/> for memory known by address: the pointer from
    /// <c>vkMapMemory</c>, Avalonia's <c>ILockedFramebuffer.Address</c>, a pinned buffer.
    /// </summary>
    /// <param name="destination">Start of the destination.</param>
    /// <param name="destinationLength">Bytes that may be written from <paramref name="destination"/> on (e.g. <c>RowBytes * Size.Height</c>).</param>
    /// <param name="rowPitch">Distance between the starts of two rows in bytes.</param>
    /// <param name="format">The byte layout to write.</param>
    /// <param name="flipVertically">Write the bottom row first.</param>
    public unsafe void CopyTo(IntPtr destination, int destinationLength, int rowPitch, FramePixelFormat format, bool flipVertically = false)
    {
        if (destination == IntPtr.Zero) throw new ArgumentNullException(nameof(destination));
        if (destinationLength < 0) throw new ArgumentOutOfRangeException(nameof(destinationLength));
        CopyTo(new Span<byte>((void*)destination, destinationLength), rowPitch, format, flipVertically);
    }

    private static InvalidOperationException NoPicture() => new("The frame is empty: no picture has been acquired yet");

    private static void WriteBgra(ReadOnlySpan<uint> source, Span<byte> target)
    {
        if (BitConverter.IsLittleEndian)
        {
            // 0xAARRGGBB stored little-endian is B, G, R, A already.
            MemoryMarshal.AsBytes(source).CopyTo(target);
            return;
        }
        for (int x = 0; x < source.Length; x++)
            BinaryPrimitives.WriteUInt32LittleEndian(target.Slice(x * 4), source[x]);
    }

    private static void WriteRgba(ReadOnlySpan<uint> source, Span<byte> target)
    {
        if (BitConverter.IsLittleEndian)
        {
            var words = MemoryMarshal.Cast<byte, uint>(target);
            for (int x = 0; x < source.Length; x++)
                words[x] = SwapRedAndBlue(source[x]);
            return;
        }
        for (int x = 0; x < source.Length; x++)
            BinaryPrimitives.WriteUInt32LittleEndian(target.Slice(x * 4), SwapRedAndBlue(source[x]));
    }

    /// <summary>0xAARRGGBB to 0xAABBGGRR: the bytes R, G, B, A once stored little-endian.</summary>
    private static uint SwapRedAndBlue(uint p) => (p & 0xFF00FF00u) | ((p >> 16) & 0xFFu) | ((p & 0xFFu) << 16);

    /// <summary>
    /// Maps each pixel back to its colour number. Pictures are long runs of one colour (border, background), so the
    /// last lookup is remembered across pixels and rows; a colour that is not in the palette becomes 0.
    /// </summary>
    private static void WriteIndexed(ReadOnlySpan<uint> source, Span<byte> target, ReadOnlySpan<uint> palette, ref uint lastColor, ref byte lastIndex)
    {
        for (int x = 0; x < source.Length; x++)
        {
            uint p = source[x];
            if (p != lastColor)
            {
                lastColor = p;
                int index = palette.IndexOf(p);
                lastIndex = index < 0 ? (byte)0 : (byte)index;
            }
            target[x] = lastIndex;
        }
    }
}
