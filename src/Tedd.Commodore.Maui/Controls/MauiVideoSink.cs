using Tedd.Maui;
using Tedd.Commodore.Emulator.Video;
using Tedd.Commodore.Hosting;

namespace Tedd.Commodore.Maui.Controls;

/// <summary>
/// <see cref="IVideoSink"/> for MAUI, on top of <see cref="Tedd.Maui.WriteableBitmap"/>: the emulator thread
/// leases one of the bitmap's native back buffers, writes the 384 x 272 picture into it and publishes it, which
/// asks every attached <see cref="WriteableBitmapView"/> for one redraw. Nothing else happens on either thread -
/// the view hands the buffer to Skia as a texture and the GPU does the magnification with nearest neighbour
/// sampling, so the picture stays sharp without the CPU ever touching a magnified copy of it.
///
/// Frame pixels are 0xAARRGGBB with alpha 0xFF. On a platform whose native 32-bit layout is the same (Windows,
/// where Skia is BGRA8888 little-endian) that is a row copy; elsewhere the channels are packed per pixel.
/// </summary>
public sealed class MauiVideoSink : IVideoSink, IDisposable
{
    /// <summary>
    /// True when the platform's native pixel layout is the frame's own 0xAARRGGBB, so rows copy verbatim.
    /// Asked of the package rather than assumed, because the layout differs between Windows and the mobile heads.
    /// </summary>
    private static readonly bool NativeMatchesFrame =
        WriteableBitmap.FromRgba(0x12, 0x34, 0x56, 0xFF) == 0xFF123456u;

    /// <summary>1 while the UI thread wants a frame; see <see cref="RequestFrame"/>.</summary>
    private int _frameWanted = 1;

    public MauiVideoSink()
    {
        Bitmap = new WriteableBitmap(VicII.VisibleArea.Width, VicII.VisibleArea.Height);
    }

    /// <summary>The pixels, to be handed to a <see cref="WriteableBitmapView"/>'s Source.</summary>
    public WriteableBitmap Bitmap { get; }

    public int Width => Bitmap.Width;
    public int Height => Bitmap.Height;

    /// <summary>Frames received so far.</summary>
    public long FramesPresented { get; private set; }

    /// <summary>Of those, the ones actually put on screen; the rest arrived without a token and were dropped.</summary>
    public long FramesPublished { get; private set; }

    /// <summary>
    /// The UI thread is ready for another frame. It calls this from its own timer, and that is what keeps the
    /// picture off the machine's clock: publishing a frame asks the view for a redraw, and a redraw is a swap
    /// chain present, so a machine producing frames hundreds of times a second in warp would ask for hundreds
    /// of presents a second. That does not merely outrun this window - it outruns the display and saturates the
    /// compositor, which is why warp used to leave the whole desktop, and not only this app, unable to answer
    /// the mouse. Because the token is handed out by the UI thread's own message loop, a thread that has fallen
    /// behind simply stops handing them out, so the picture can never be asked for faster than it can be drawn,
    /// whatever the machine is doing. Frames arriving without a token are dropped before they are copied, which
    /// makes warp a little faster for it.
    /// </summary>
    public void RequestFrame() => Volatile.Write(ref _frameWanted, 1);

    /// <summary>Called on the emulator thread.</summary>
    public void PresentFrame(in VideoFrame frame)
    {
        FramesPresented++;
        if (frame.Width != Width || frame.Height != Height) return;
        if (Interlocked.Exchange(ref _frameWanted, 0) == 0) return;
        // Non-blocking: a tick is dropped only while the GPU still holds every back buffer.
        if (!Bitmap.TryBeginWrite(out var write)) return;
        try
        {
            var destination = write.Pixels;
            int destinationStride = Bitmap.Stride / sizeof(uint);
            for (int y = 0; y < Height; y++)
            {
                var source = frame.Pixels.AsSpan((frame.VisibleY + y) * frame.FullWidth + frame.VisibleX, Width);
                var row = destination.Slice(y * destinationStride, Width);
                if (NativeMatchesFrame)
                {
                    source.CopyTo(row);
                }
                else
                {
                    for (int x = 0; x < Width; x++)
                    {
                        uint p = source[x];
                        row[x] = WriteableBitmap.FromRgba((byte)(p >> 16), (byte)(p >> 8), (byte)p, 0xFF);
                    }
                }
            }
        }
        finally
        {
            // Publishes the buffer as the newest frame and asks the view for one coalesced redraw.
            write.Dispose();
            FramesPublished++;
        }
    }

    public void Dispose() => Bitmap.Dispose();
}
