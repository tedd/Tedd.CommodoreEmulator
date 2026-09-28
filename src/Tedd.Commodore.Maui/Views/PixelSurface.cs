using SkiaSharp;
using Tedd.Maui;

namespace Tedd.Commodore.Maui.Views;

/// <summary>
/// A picture made of C64 pixels: the machine's own pixels in a <see cref="Tedd.Maui.WriteableBitmap"/>, handed to
/// the GPU as a texture and magnified by a <see cref="WriteableBitmapView"/> with nearest neighbour sampling.
/// That is the path the emulator screen takes, and the reason a repaint costs what the C64 has - 24 x 21 pixels
/// for a sprite, 256 x 128 for a 512 character set - rather than what the screen shows at the current zoom.
/// <para>
/// Everything that turns emulator pixels into a picture goes through one of these. What is not made of C64
/// pixels - grids, hover and selection boxes, gutter labels - stays on a transparent <see cref="GraphicsView"/>
/// stacked over it, because those are drawn in screen pixels and want the vector canvas.
/// </para>
/// </summary>
public sealed class PixelSurface : IDisposable
{
    /// <summary>Writes one picture. <paramref name="stride"/> is the pixels per bitmap row, padding included.</summary>
    public delegate void Painter(Span<uint> pixels, int stride);

    /// <summary>How long to wait before trying again when Skia still holds every back buffer.</summary>
    private static readonly TimeSpan Retry = TimeSpan.FromMilliseconds(16);

    private WriteableBitmap? _bitmap;
    private Painter? _pending;
    private bool _retryQueued;

    public PixelSurface()
    {
        View = new WriteableBitmapView
        {
            // Callers size the view to a whole multiple of the picture, so Fill scales by whole pixels and keeps
            // the picture registered with whatever is drawn over it instead of letterboxing away from it.
            Aspect = Aspect.Fill,
            FilterMode = SKFilterMode.Nearest,
            InputTransparent = true,
            IsVisible = false,
        };
    }

    /// <summary>The view to put in the layout. Size it to a whole multiple of the picture.</summary>
    public WriteableBitmapView View { get; }

    /// <summary>
    /// Paints <paramref name="width"/> x <paramref name="height"/> C64 pixels, reallocating only when the picture
    /// changes size. The lease is nonblocking: a paint that arrives while Skia still holds every back buffer is
    /// repeated on the next tick rather than leaving the previous picture on screen.
    /// </summary>
    public void Paint(int width, int height, Painter painter)
    {
        if (width <= 0 || height <= 0)
        {
            View.IsVisible = false;
            return;
        }
        if (_bitmap is null || _bitmap.Width != width || _bitmap.Height != height)
        {
            _bitmap?.Dispose();
            _bitmap = new WriteableBitmap(width, height);
            View.Source = _bitmap;
        }
        if (!_bitmap.TryBeginWrite(out var write))
        {
            _pending = painter;
            QueueRetry();
            return;
        }
        try
        {
            // A leased buffer may still hold an older picture, so a painter writes every pixel of its own.
            painter(write.Pixels, _bitmap.Stride / sizeof(uint));
        }
        finally
        {
            // Publishes the buffer as the newest frame and asks the view for one redraw.
            write.Dispose();
        }
        _pending = null;
        View.IsVisible = true;
    }

    /// <summary>Releases the native pixel buffers.</summary>
    public void Dispose()
    {
        _pending = null;
        View.Source = null;
        _bitmap?.Dispose();
        _bitmap = null;
    }

    private void QueueRetry()
    {
        if (_retryQueued) return;
        _retryQueued = true;
        View.Dispatcher.DispatchDelayed(Retry, () =>
        {
            _retryQueued = false;
            if (_pending is { } painter && _bitmap is { } bitmap)
                Paint(bitmap.Width, bitmap.Height, painter);
        });
    }
}
