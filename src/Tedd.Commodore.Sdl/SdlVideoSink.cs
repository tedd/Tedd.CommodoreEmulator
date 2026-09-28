using System;
using System.Threading;
using Tedd.Commodore.Emulator.Video;
using Tedd.Commodore.Hosting;
using static SDL2.SDL;

namespace Tedd.Commodore.Sdl;

/// <summary>
/// An <see cref="IVideoSink"/> that keeps the latest visible picture (0xAARRGGBB) ready for upload into a streaming
/// <c>SDL_PIXELFORMAT_ARGB8888</c> texture. The picture is 384 x 272 for the VIC-II and 768 x 272 for the C128's
/// VDC; the size travels with each frame and <see cref="Width"/>/<see cref="Height"/> follow the front buffer, so
/// the renderer recreates its texture when <see cref="Acquire"/> reports a size change.
/// </summary>
/// <remarks>
/// <para>
/// Threading: the emulation thread calls <see cref="PresentFrame"/> and never touches SDL. The thread that owns the
/// SDL renderer calls <see cref="UploadTo"/> (or <see cref="Acquire"/> + <see cref="Upload"/>) whenever it wants to
/// draw. Frames are handed over with a three-buffer swap (back / shared / front) under a short lock, so neither side
/// ever waits for the other beyond a reference exchange, and the newest complete frame always wins.
/// </para>
/// <para>
/// The pixel layout is a direct copy of the emulator's 0xAARRGGBB words: on a little-endian machine that is the
/// byte order B, G, R, A that SDL calls ARGB8888, so no per-pixel conversion is needed.
/// </para>
/// </remarks>
public sealed class SdlVideoSink : IVideoSink
{
    /// <summary>Width of the C64 picture in pixels (384).</summary>
    public static readonly int DefaultWidth = VicII.VisibleArea.Width;
    /// <summary>Height of the picture in pixels (272).</summary>
    public static readonly int DefaultHeight = VicII.VisibleArea.Height;
    /// <summary>The SDL pixel format the buffers are laid out in.</summary>
    public static readonly uint PixelFormat = SDL_PIXELFORMAT_ARGB8888;

    private readonly object _lock = new();
    private uint[] _back = new uint[Vdc8563.FrameWidth * Vdc8563.FrameHeight];
    private uint[] _shared = new uint[Vdc8563.FrameWidth * Vdc8563.FrameHeight];
    private uint[] _front = new uint[Vdc8563.FrameWidth * Vdc8563.FrameHeight];
    private int _sharedWidth, _sharedHeight;
    private bool _hasNew;
    private bool _hasFront;
    private long _sharedFrame;
    private long _frontFrame;
    private long _received;
    private long _skipped;

    public SdlVideoSink()
    {
        Width = DefaultWidth;
        Height = DefaultHeight;
    }

    /// <summary>Width of the picture in the front buffer.</summary>
    public int Width { get; private set; }
    /// <summary>Height of the picture in the front buffer.</summary>
    public int Height { get; private set; }
    /// <summary>Bytes per row of the front buffer.</summary>
    public int Pitch => Width * sizeof(uint);

    /// <summary>Number of frames delivered by the emulator.</summary>
    public long FramesReceived => Interlocked.Read(ref _received);
    /// <summary>Frames that were replaced by a newer one before the renderer picked them up (warp mode, mostly).</summary>
    public long FramesSkipped => Interlocked.Read(ref _skipped);
    /// <summary>Emulator frame number of the picture currently in the front buffer.</summary>
    public long FrontFrameNumber => _frontFrame;
    /// <summary>True when a frame newer than the front buffer is waiting.</summary>
    public bool HasNewFrame
    {
        get { lock (_lock) return _hasNew; }
    }

    /// <inheritdoc />
    public void PresentFrame(in VideoFrame frame)
    {
        frame.CopyVisible(_back);
        lock (_lock)
        {
            (_back, _shared) = (_shared, _back);
            _sharedFrame = frame.FrameNumber;
            _sharedWidth = frame.Width;
            _sharedHeight = frame.Height;
            if (_hasNew) Interlocked.Increment(ref _skipped);
            _hasNew = true;
        }
        Interlocked.Increment(ref _received);
    }

    /// <summary>
    /// Moves the newest waiting frame into the front buffer. Returns false when nothing new has arrived since the last
    /// call; <paramref name="sizeChanged"/> tells whether the picture size differs from the previous front buffer
    /// (the texture must then be recreated). Call from the rendering thread only.
    /// </summary>
    public bool Acquire(out bool sizeChanged)
    {
        sizeChanged = false;
        lock (_lock)
        {
            if (!_hasNew) return false;
            (_front, _shared) = (_shared, _front);
            _frontFrame = _sharedFrame;
            sizeChanged = _sharedWidth != Width || _sharedHeight != Height;
            Width = _sharedWidth;
            Height = _sharedHeight;
            _hasNew = false;
            _hasFront = true;
            return true;
        }
    }

    /// <summary>Uploads the front buffer (whatever <see cref="Acquire"/> last produced) into the texture.</summary>
    public unsafe void Upload(IntPtr texture)
    {
        fixed (uint* p = _front)
        {
            if (SDL_UpdateTexture(texture, IntPtr.Zero, (IntPtr)p, Pitch) != 0)
                throw new SdlException("SDL_UpdateTexture");
        }
    }

    /// <summary>
    /// Copies the front buffer (the picture last acquired, row-major 0xAARRGGBB, <see cref="Width"/> x
    /// <see cref="Height"/>) to <paramref name="destination"/>, e.g. for screenshots. Returns false if no frame has
    /// been acquired yet. Call from the rendering thread.
    /// </summary>
    public bool CopyFront(Span<uint> destination)
    {
        if (!_hasFront) return false;
        _front.AsSpan(0, Width * Height).CopyTo(destination);
        return true;
    }

    /// <summary>Creates a streaming texture of the front buffer's current size.</summary>
    public IntPtr CreateTexture(IntPtr renderer)
    {
        var texture = SDL_CreateTexture(renderer, PixelFormat, (int)SDL_TextureAccess.SDL_TEXTUREACCESS_STREAMING, Width, Height);
        if (texture == IntPtr.Zero)
            throw new SdlException("SDL_CreateTexture");
        return texture;
    }
}
