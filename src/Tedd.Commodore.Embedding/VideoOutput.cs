using System;
using System.Threading;
using Tedd.Commodore.Emulator.Video;
using Tedd.Commodore.Hosting;

namespace Tedd.Commodore.Embedding;

/// <summary>
/// The picture side of an embedded emulator: an <see cref="IVideoSink"/> that keeps the newest frame for a
/// renderer, which takes it with <see cref="TryAcquire"/> whenever it is about to draw.
/// </summary>
/// <remarks>
/// <para>
/// Triple buffered and lock free: the emulation thread fills a back buffer and publishes it, the renderer swaps
/// the newest published buffer for the one it held. Neither side ever waits for the other, the renderer always
/// gets the latest complete frame (a frame it was too slow for is skipped and counted in
/// <see cref="FramesSkipped"/>), and the frame it holds is not touched until it asks for the next one. The
/// emulator runs at 50.125 frames per second (PAL) and the host at its own display rate, so a 60 Hz renderer
/// gets a new frame on five out of six ticks and simply redraws the last one on the sixth.
/// </para>
/// <para>
/// Threading: <see cref="PresentFrame"/> belongs to the thread that runs the emulator,
/// <see cref="TryAcquire"/> and <see cref="Current"/> to one rendering thread (which may be the same thread).
/// </para>
/// </remarks>
public sealed class VideoOutput : IVideoSink
{
    // _shared holds the index of the buffer in the middle, plus Fresh while that buffer carries a frame the
    // renderer has not taken yet. Both sides only ever exchange it, so the three indexes are always distinct.
    private const int IndexMask = 3;
    private const int Fresh = 4;

    private readonly Slot[] _slots = { new(), new(), new() };
    private int _back;
    private int _shared = 1;
    private int _front = 2;
    private long _presented;
    private long _acquired;
    private long _skipped;

    /// <summary>
    /// Raised on the emulation thread after each frame has been published, for hosts that post a redraw instead of
    /// polling. The machine is locked while it runs: post to your UI thread, never wait for it.
    /// </summary>
    public event Action? FramePresented;

    /// <summary>Frames the emulator has delivered.</summary>
    public long FramesPresented => Interlocked.Read(ref _presented);

    /// <summary>Frames the renderer has taken.</summary>
    public long FramesAcquired => Interlocked.Read(ref _acquired);

    /// <summary>Frames that were replaced by a newer one before the renderer took them (warp mode, a slow renderer).</summary>
    public long FramesSkipped => Interlocked.Read(ref _skipped);

    /// <summary>True when a frame newer than <see cref="Current"/> is waiting.</summary>
    public bool HasNewFrame => (Volatile.Read(ref _shared) & Fresh) != 0;

    /// <summary>The frame the renderer took last (empty before the first <see cref="TryAcquire"/> that returned true).</summary>
    public RawFrame Current => _slots[_front].View;

    /// <inheritdoc />
    public void PresentFrame(in VideoFrame frame)
    {
        _slots[_back].Store(frame);
        int previous = Interlocked.Exchange(ref _shared, _back | Fresh);
        if ((previous & Fresh) != 0)
            Interlocked.Increment(ref _skipped);
        _back = previous & IndexMask;
        Interlocked.Increment(ref _presented);
        FramePresented?.Invoke();
    }

    /// <summary>
    /// Takes the newest frame if one arrived since the last call. Returns false (and <see cref="Current"/> in
    /// <paramref name="frame"/>, empty until the first frame) when there is nothing new, in which case the host
    /// redraws what it already has. The frame stays valid until the next call.
    /// </summary>
    public bool TryAcquire(out RawFrame frame)
    {
        if ((Volatile.Read(ref _shared) & Fresh) == 0)
        {
            frame = Current;
            return false;
        }
        _front = Interlocked.Exchange(ref _shared, _front) & IndexMask;
        Interlocked.Increment(ref _acquired);
        frame = Current;
        return true;
    }

    /// <summary>A copy of the visible area of one frame; grows if a frame ever needs more room.</summary>
    private sealed class Slot
    {
        private uint[] _pixels = new uint[Vdc8563.FrameWidth * Vdc8563.FrameHeight];
        private int _width;
        private int _height;
        private long _frameNumber;
        private VideoSource _source;
        private bool _filled;

        public RawFrame View => _filled ? new RawFrame(_pixels, 0, _width, _width, _height, _frameNumber, _source) : default;

        public void Store(in VideoFrame frame)
        {
            int size = frame.Width * frame.Height;
            if (_pixels.Length < size)
                _pixels = new uint[size];
            frame.CopyVisible(_pixels);
            _width = frame.Width;
            _height = frame.Height;
            _frameNumber = frame.FrameNumber;
            _source = frame.Source;
            _filled = true;
        }
    }
}
