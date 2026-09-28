using System;
using System.Windows.Media;
using Tedd.Commodore.Emulator.Video;
using Tedd.Commodore.Hosting;
using TeddBitmap = Tedd.WriteableBitmap;

namespace Tedd.Commodore.GUI;

/// <summary>
/// <see cref="IVideoSink"/> for WPF, on top of <see cref="TeddBitmap"/>: the bitmap's pixels live in a shared
/// memory section that WPF's render thread composites straight out of, so the emulator thread writes the picture
/// into its final destination and the UI thread only has to say that it changed
/// (<c>InteropBitmap.Invalidate</c>, from <c>CompositionTarget.Rendering</c>). Nothing is copied at present time.
///
/// The frame's pixels are 0xAARRGGBB with alpha 0xFF, which is byte for byte a <see cref="PixelFormats.Bgra32"/>
/// row on a little-endian machine, so a frame is a handful of row copies rather than a per-channel swizzle.
///
/// The picture is 384 x 272 for the VIC-II and 768 x 272 for the C128's VDC. Frames of the other size are
/// dropped and <see cref="SizeChanged"/> goes true; the window then calls <see cref="CreateBitmap"/> again,
/// which has to happen on the UI thread because an <c>InteropBitmap</c> belongs to the thread that made it.
///
/// Writing while WPF composites can in principle tear, since the buffer is shared rather than double buffered.
/// One frame is a sub-millisecond memcpy against a 20 ms frame interval, so the window for it is very small.
/// </summary>
public sealed class WpfVideoSink : IVideoSink, IDisposable
{
    /// <summary>Guards <see cref="_bitmap"/>: the emulator thread writes into it, the UI thread replaces it.</summary>
    private readonly object _lock = new();
    private TeddBitmap? _bitmap;
    private volatile bool _dirty;
    private int _pendingWidth, _pendingHeight;
    private bool _disposed;

    public WpfVideoSink()
    {
        Width = _pendingWidth = VicII.VisibleArea.Width;
        Height = _pendingHeight = VicII.VisibleArea.Height;
    }

    /// <summary>Width of the picture the current bitmap has.</summary>
    public int Width { get; private set; }
    /// <summary>Height of the picture the current bitmap has.</summary>
    public int Height { get; private set; }

    /// <summary>True when frames are arriving at another size than the bitmap; call <see cref="CreateBitmap"/> again.</summary>
    public bool SizeChanged => _pendingWidth != Width || _pendingHeight != Height;

    /// <summary>Frames received so far.</summary>
    public long FramesPresented { get; private set; }

    /// <summary>
    /// Creates the bitmap the emulator thread will write into, at the size frames are currently arriving in, and
    /// releases the previous one. Must be called on the UI thread, whose dispatcher the bitmap then belongs to.
    /// </summary>
    public TeddBitmap CreateBitmap()
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Width = _pendingWidth;
            Height = _pendingHeight;
            var previous = _bitmap;
            _bitmap = new TeddBitmap(Width, Height, PixelFormats.Bgra32);
            _dirty = false;
            previous?.Dispose();
            return _bitmap;
        }
    }

    /// <summary>Called on the emulator thread.</summary>
    public void PresentFrame(in VideoFrame frame)
    {
        lock (_lock)
        {
            _pendingWidth = frame.Width;
            _pendingHeight = frame.Height;
            var bitmap = _bitmap;
            if (bitmap is not null && bitmap.Width == frame.Width && bitmap.Height == frame.Height)
            {
                frame.CopyVisible(bitmap.ToSpanUInt32());
                _dirty = true;
            }
            FramesPresented++;
        }
    }

    /// <summary>
    /// Tells WPF to re-read the bitmap if a new frame arrived since the last call. Must be called on the thread
    /// that created the bitmap. Returns true when there was something new to show.
    /// </summary>
    public bool Present()
    {
        if (!_dirty) return false;
        lock (_lock)
        {
            if (!_dirty || _bitmap is null) return false;
            _dirty = false;
            _bitmap.Invalidate();
        }
        return true;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _bitmap?.Dispose();
            _bitmap = null;
        }
    }
}
