using System.Runtime.Versioning;
using Tedd.Commodore.Emulator.Video;
using Tedd.Commodore.Hosting;

namespace Tedd.Commodore.Web.Interop;

/// <summary>
/// <see cref="IVideoSink"/> that draws on an HTML canvas through <c>c64.js</c>.
/// <see cref="PresentFrame"/> only remembers the frame (the chip's buffer stays valid until the next
/// <c>RunFrame</c>); <see cref="Flush"/> converts the visible area to RGBA and pushes it with
/// <c>putImageData</c>. The host calls <see cref="Flush"/> once per animation frame, so when several
/// emulated frames run in one tick only the last one is drawn. The canvas is resized whenever the picture
/// size changes (384 x 272 for the VIC-II, 768 x 272 for the C128's VDC).
/// </summary>
[SupportedOSPlatform("browser")]
public sealed class BrowserVideoSink : IVideoSink
{
    public static readonly int DefaultWidth = VicII.VisibleArea.Width;
    public static readonly int DefaultHeight = VicII.VisibleArea.Height;

    private byte[] _rgba = new byte[Vdc8563.FrameWidth * Vdc8563.FrameHeight * 4];
    private VideoFrame _pending;
    private bool _hasPending;

    /// <param name="canvasId">DOM id of the canvas; it is sized to the picture in device pixels (CSS does the scaling).</param>
    public BrowserVideoSink(string canvasId)
    {
        CanvasId = canvasId;
        Width = DefaultWidth;
        Height = DefaultHeight;
        C64Js.AttachCanvas(canvasId, Width, Height);
    }

    public string CanvasId { get; }

    /// <summary>Current canvas width in pixels.</summary>
    public int Width { get; private set; }
    /// <summary>Current canvas height in pixels.</summary>
    public int Height { get; private set; }

    /// <summary>Number of frames actually drawn.</summary>
    public long PresentedFrames { get; private set; }

    public void PresentFrame(in VideoFrame frame)
    {
        _pending = frame;
        _hasPending = true;
    }

    /// <summary>Draws the most recently presented frame, if any new one arrived since the last flush.</summary>
    public void Flush()
    {
        if (!_hasPending) return;
        _hasPending = false;
        var frame = _pending;
        if (frame.Width != Width || frame.Height != Height)
        {
            Width = frame.Width;
            Height = frame.Height;
            C64Js.AttachCanvas(CanvasId, Width, Height);
        }
        int bytes = Width * Height * 4;
        if (_rgba.Length < bytes) _rgba = new byte[bytes];
        frame.CopyVisibleRgba(_rgba);
        C64Js.PresentFrame(_rgba.AsSpan(0, bytes));
        PresentedFrames++;
    }
}
