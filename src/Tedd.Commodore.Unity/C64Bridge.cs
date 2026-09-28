using System;
using System.Threading;
using Tedd.Commodore.Emulator.C128;
using Tedd.Commodore.Emulator.C64;
using Tedd.Commodore.Emulator.Machines;
using Tedd.Commodore.Emulator.Video;
using Tedd.Commodore.Hosting;

namespace Tedd.Commodore.Unity;

/// <summary>
/// Facade over <see cref="EmulatorSession"/> shaped for game engines: the engine calls <see cref="Update"/> once
/// per rendered frame with its delta time and the bridge runs however many PAL frames are due (at most
/// <see cref="MaxFramesPerUpdate"/>), keeps a copy of the last picture for <see cref="CopyFrameRgba"/> /
/// <see cref="CopyFrameArgb"/>, and queues the SID output in a ring buffer that an audio callback drains with
/// <see cref="ReadAudio"/>.
/// <para>
/// Threading: everything except <see cref="ReadAudio"/> must be called from one thread (the engine's main/update
/// thread). <see cref="ReadAudio"/> may be called concurrently from the engine's audio thread (Unity's
/// <c>OnAudioFilterRead</c>); the ring buffer is single-producer/single-consumer and lock free.
/// </para>
/// </summary>
public sealed class C64Bridge
{
    public const int DefaultSampleRate = 44100;

    private static readonly double FrameSeconds = 1.0 / CommodoreMachine.FrameRate;

    private readonly FrameStore _frames;
    private readonly AudioRing _audio;
    private double _accumulator;
    private int _maxFramesPerUpdate = 3;

    /// <summary>Creates a bridge around a C64 ROM set (with a 1541 drive when the set contains a drive ROM).</summary>
    public C64Bridge(RomSet roms, int sampleRate = DefaultSampleRate)
        : this(new EmulatorSession(roms ?? throw new ArgumentNullException(nameof(roms)), sampleRate))
    {
    }

    /// <summary>Creates a bridge around a C128 ROM set; <paramref name="columns80"/> boots on the 80 column VDC screen.</summary>
    public C64Bridge(C128RomSet roms, int sampleRate = DefaultSampleRate, bool columns80 = false)
        : this(new EmulatorSession(roms ?? throw new ArgumentNullException(nameof(roms)), sampleRate, columns80: columns80))
    {
    }

    private C64Bridge(EmulatorSession session)
    {
        Session = session;
        int sampleRate = session.SampleRate;
        _frames = new FrameStore();
        _audio = new AudioRing(sampleRate, Math.Max(1024, sampleRate / 4));
        Session.Video = _frames;
        Session.Audio = _audio;
        Session.Command += cmd => Command?.Invoke(cmd);
    }

    /// <summary>
    /// Creates a bridge from raw ROM images: BASIC (8192 bytes), KERNAL (8192 bytes), character generator
    /// (4096 bytes) and optionally the 1541 DOS ROM (16384 bytes; without it no disk drive is attached).
    /// <paramref name="sampleRate"/> should be the engine's audio output rate.
    /// </summary>
    public static C64Bridge Create(byte[] basic, byte[] kernal, byte[] chargen, byte[]? driveRom, int sampleRate = DefaultSampleRate)
    {
        var roms = new RomSet(basic, kernal, chargen, driveRom, "", driveRom is null ? "in-memory (no drive)" : "in-memory");
        return new C64Bridge(roms, sampleRate);
    }

    /// <summary>The underlying session (for anything the facade does not expose).</summary>
    public EmulatorSession Session { get; }

    /// <summary>The machine itself (memory, chips, drive) for debuggers and tooling: a <see cref="C64"/> or a <see cref="C128"/>.</summary>
    public CommodoreMachine Machine => Session.Machine;

    /// <summary>The machine model.</summary>
    public MachineModel Model => Session.Model;

    /// <summary>
    /// Width of the picture produced by the copy methods: 384 for the VIC-II, 768 when a C128 shows its VDC screen.
    /// It can change between frames on a C128 (40/80 key), so size textures from it after <see cref="Update"/>.
    /// </summary>
    public int FrameWidth => _frames.Width;

    /// <summary>Height of the picture produced by the copy methods (272).</summary>
    public int FrameHeight => _frames.Height;

    /// <summary>Which picture a C128 shows (ignored on a C64).</summary>
    public DisplayOutput Display
    {
        get => Session.Display;
        set => Session.Display = value;
    }

    /// <summary>The C128's 40/80 DISPLAY key (true = 40 columns); always true on a C64.</summary>
    public bool Display40Columns
    {
        get => Session.Display40Columns;
        set => Session.Display40Columns = value;
    }

    /// <summary>Sample rate of the audio delivered by <see cref="ReadAudio"/>.</summary>
    public int AudioSampleRate => Session.SampleRate;

    /// <summary>Mono samples currently queued for <see cref="ReadAudio"/>.</summary>
    public int AudioBuffered => _audio.Available;

    /// <summary>Sample frames that had to be filled with silence because the emulator had not produced them yet.</summary>
    public long AudioUnderruns => _audio.Underruns;

    /// <summary>Samples dropped because the audio callback did not drain the ring buffer fast enough.</summary>
    public long AudioOverruns => _audio.Overruns;

    /// <summary>Number of PAL frames executed through this bridge.</summary>
    public long FramesRun { get; private set; }

    /// <summary>Frame counter (since power-on) of the picture held by the copy methods.</summary>
    public long FrameNumber => _frames.FrameNumber;

    /// <summary>
    /// Upper bound on the frames a single <see cref="Update"/> call may run (default 3). When the engine falls
    /// further behind than this, the backlog is dropped instead of being caught up.
    /// </summary>
    public int MaxFramesPerUpdate
    {
        get => _maxFramesPerUpdate;
        set => _maxFramesPerUpdate = value < 1 ? 1 : value;
    }

    /// <summary>When true, <see cref="Update"/> runs nothing and the audio queue is flushed.</summary>
    public bool Paused
    {
        get => Session.Paused;
        set
        {
            Session.Paused = value;
            if (value)
            {
                _accumulator = 0;
                _audio.Clear();
            }
        }
    }

    /// <summary>Warp: every <see cref="Update"/> runs <see cref="MaxFramesPerUpdate"/> frames regardless of time; audio is muted.</summary>
    public bool Warp
    {
        get => Session.Warp;
        set => Session.Warp = value;
    }

    /// <summary>The 25 x 40 character screen as text (for tests, tooling and "is the game loaded yet" checks).</summary>
    public string ScreenText => Session.Machine.GetScreenText();

    /// <summary>Human readable description of the attached media.</summary>
    public string MediaDescription => Session.MediaDescription;

    /// <summary>Physical key (W3C code) to C64 action table used by <see cref="KeyDown"/>/<see cref="KeyUp"/>.</summary>
    public KeyBindings Bindings
    {
        get => Session.Bindings;
        set => Session.Bindings = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>Raised (from <see cref="Update"/> or the key methods) when a bound key requests a host command.</summary>
    public event Action<SystemCommand>? Command;

    #region Running

    /// <summary>
    /// Advances the emulator by <paramref name="deltaSeconds"/> of wall-clock time, running as many whole PAL
    /// frames (1/50.125 s) as are due, at most <see cref="MaxFramesPerUpdate"/>. Returns true when at least one
    /// frame was produced, i.e. when the picture obtained from the copy methods changed.
    /// </summary>
    public bool Update(double deltaSeconds)
    {
        if (Session.Paused)
        {
            _accumulator = 0;
            return false;
        }
        if (double.IsNaN(deltaSeconds) || deltaSeconds < 0)
            deltaSeconds = 0;

        int cap = _maxFramesPerUpdate;
        int frames = 0;
        if (Session.Warp)
        {
            _accumulator = 0;
            for (; frames < cap; frames++)
                RunOne();
        }
        else
        {
            _accumulator += deltaSeconds;
            while (_accumulator >= FrameSeconds && frames < cap)
            {
                RunOne();
                _accumulator -= FrameSeconds;
                frames++;
            }
            if (_accumulator >= FrameSeconds)
                _accumulator = 0; // still behind after the cap: drop the backlog rather than spiral
        }
        return frames > 0;
    }

    /// <summary>Runs exactly one PAL frame regardless of time (stepping, tests). Returns false when paused.</summary>
    public bool RunFrame()
    {
        if (Session.Paused) return false;
        RunOne();
        return true;
    }

    private void RunOne()
    {
        Session.RunFrame();
        FramesRun++;
    }

    /// <summary>Resets the machine (hard = power cycle, RAM cleared) and releases all input.</summary>
    public void Reset(bool hard)
    {
        Session.Reset(hard);
        _accumulator = 0;
        _audio.Clear();
    }

    #endregion

    #region Video

    /// <summary>
    /// Copies the last presented picture as RGBA bytes (R, G, B, A; A = 255) into <paramref name="destination"/>,
    /// which must hold at least <see cref="FrameWidth"/> * <see cref="FrameHeight"/> * 4 bytes. With
    /// <paramref name="flipVertically"/> the bottom row comes first, which is what Unity's
    /// <c>Texture2D.LoadRawTextureData</c> expects for an upright picture.
    /// </summary>
    public void CopyFrameRgba(byte[] destination, bool flipVertically = false) => _frames.CopyRgba(destination, flipVertically);

    /// <summary>
    /// Copies the last presented picture as 0xAARRGGBB pixels into <paramref name="destination"/>, which must hold
    /// at least <see cref="FrameWidth"/> * <see cref="FrameHeight"/> entries.
    /// </summary>
    public void CopyFrameArgb(uint[] destination, bool flipVertically = false) => _frames.CopyArgb(destination, flipVertically);

    #endregion

    #region Audio

    /// <summary>
    /// Fills <paramref name="interleaved"/> (length = frames * <paramref name="channels"/>) with audio in the
    /// range -1..1; the mono SID signal is duplicated to every channel. Frames the emulator has not produced yet
    /// are written as silence. Returns the number of sample frames that carried emulator audio. Safe to call from
    /// an audio thread (Unity's <c>OnAudioFilterRead(float[] data, int channels)</c> maps onto it directly).
    /// </summary>
    public int ReadAudio(float[] interleaved, int channels) => _audio.Read(interleaved, channels);

    #endregion

    #region Input

    /// <summary>A physical key went down; <paramref name="code"/> is a W3C KeyboardEvent.code name (see <see cref="UnityKeyCodes"/>).</summary>
    public void KeyDown(string code) => Session.KeyDown(code);

    /// <summary>A physical key went up.</summary>
    public void KeyUp(string code) => Session.KeyUp(code);

    /// <summary>Drives a joystick input on port 1 or 2 directly (game pads, touch controls).</summary>
    public void SetJoystick(int port, JoystickInput input, bool pressed) => Session.SetJoystick(port, input, pressed);

    /// <summary>Presses or releases a C64 key directly, bypassing the bindings (on-screen keyboards).</summary>
    public void PressKey(C64Key key, bool pressed) => Session.SetKey(key, pressed);

    /// <summary>Releases every key and joystick input (call when the engine loses focus).</summary>
    public void ReleaseAll() => Session.ReleaseAllInput();

    /// <summary>Types text through the KERNAL keyboard buffer ('\n' = RETURN).</summary>
    public void TypeText(string text) => Session.TypeText(text);

    #endregion

    #region Media

    /// <summary>
    /// Attaches a disk (.d64), tape/program (.t64, .prg) or cartridge (.crt, .bin) image; the type is detected from
    /// <paramref name="fileName"/>'s extension or the content. With <paramref name="autostart"/> the first program is
    /// loaded and run as soon as BASIC is ready.
    /// </summary>
    public void AttachMedia(byte[] data, string fileName, bool autostart) => Session.AttachAuto(data, fileName, autostart);

    /// <summary>Removes the disk from the drive.</summary>
    public void EjectDisk() => Session.EjectDisk();

    /// <summary>Removes the cartridge and resets.</summary>
    public void DetachCartridge() => Session.DetachCartridge();

    #endregion

    /// <summary>Keeps a copy of the visible area of the last presented frame (its size follows the frame).</summary>
    private sealed class FrameStore : IVideoSink
    {
        private int _width = VicII.VisibleArea.Width;
        private int _height = VicII.VisibleArea.Height;
        private uint[] _argb;

        public FrameStore() => _argb = new uint[Vdc8563.FrameWidth * Vdc8563.FrameHeight];

        public long FrameNumber { get; private set; }
        public int Width => _width;
        public int Height => _height;

        public void PresentFrame(in VideoFrame frame)
        {
            if (_argb.Length < frame.Width * frame.Height)
                _argb = new uint[frame.Width * frame.Height];
            _width = frame.Width;
            _height = frame.Height;
            frame.CopyVisible(_argb);
            FrameNumber = frame.FrameNumber;
        }

        public void CopyRgba(byte[] destination, bool flip)
        {
            if (destination is null) throw new ArgumentNullException(nameof(destination));
            if (destination.Length < _width * _height * 4)
                throw new ArgumentException($"Destination must hold at least {_width * _height * 4} bytes", nameof(destination));
            int o = 0;
            for (int y = 0; y < _height; y++)
            {
                int src = (flip ? _height - 1 - y : y) * _width;
                for (int x = 0; x < _width; x++)
                {
                    uint p = _argb[src + x];
                    destination[o++] = (byte)(p >> 16);
                    destination[o++] = (byte)(p >> 8);
                    destination[o++] = (byte)p;
                    destination[o++] = 0xFF;
                }
            }
        }

        public void CopyArgb(uint[] destination, bool flip)
        {
            if (destination is null) throw new ArgumentNullException(nameof(destination));
            if (destination.Length < _width * _height)
                throw new ArgumentException($"Destination must hold at least {_width * _height} pixels", nameof(destination));
            if (!flip)
            {
                Array.Copy(_argb, destination, _width * _height);
                return;
            }
            for (int y = 0; y < _height; y++)
                Array.Copy(_argb, (_height - 1 - y) * _width, destination, y * _width, _width);
        }
    }

    /// <summary>
    /// Single-producer (emulation thread, <see cref="Write"/>) / single-consumer (audio thread, <see cref="Read"/>)
    /// ring buffer of 16-bit mono samples. <see cref="Clear"/> is requested by the producer and honoured by the
    /// consumer on its next read so the two never touch the same cursor.
    /// </summary>
    private sealed class AudioRing : IAudioSink
    {
        private readonly short[] _buffer;
        private long _written;
        private long _read;
        private int _clearRequested;
        private long _underruns;
        private long _overruns;

        public AudioRing(int sampleRate, int capacity)
        {
            SampleRate = sampleRate;
            _buffer = new short[capacity];
        }

        public int SampleRate { get; }
        public int Available => (int)(Volatile.Read(ref _written) - Volatile.Read(ref _read));
        public long Underruns => Volatile.Read(ref _underruns);
        public long Overruns => Volatile.Read(ref _overruns);

        public void Write(ReadOnlySpan<short> samples)
        {
            long w = _written;
            long r = Volatile.Read(ref _read);
            int free = _buffer.Length - (int)(w - r);
            int n = Math.Min(free, samples.Length);
            if (n < samples.Length)
                Volatile.Write(ref _overruns, _overruns + (samples.Length - n));
            int len = _buffer.Length;
            for (int i = 0; i < n; i++)
                _buffer[(int)((w + i) % len)] = samples[i];
            Volatile.Write(ref _written, w + n);
        }

        public void Clear() => Volatile.Write(ref _clearRequested, 1);

        public int Read(float[] destination, int channels)
        {
            if (destination is null) throw new ArgumentNullException(nameof(destination));
            if (channels < 1) throw new ArgumentOutOfRangeException(nameof(channels));
            int frames = destination.Length / channels;
            long r = _read;
            if (Interlocked.Exchange(ref _clearRequested, 0) != 0)
                r = Volatile.Read(ref _written);
            long w = Volatile.Read(ref _written);
            int n = (int)Math.Min(w - r, frames);
            int len = _buffer.Length;
            int o = 0;
            for (int i = 0; i < n; i++)
            {
                float v = _buffer[(int)((r + i) % len)] * (1f / 32768f);
                for (int c = 0; c < channels; c++)
                    destination[o++] = v;
            }
            if (o < destination.Length)
                Array.Clear(destination, o, destination.Length - o);
            if (n < frames)
                Volatile.Write(ref _underruns, _underruns + (frames - n));
            Volatile.Write(ref _read, r + n);
            return n;
        }
    }
}
