using System;
using Tedd.MOS65xx.Emulator.C128;
using Tedd.MOS65xx.Emulator.C64;
using Tedd.MOS65xx.Emulator.Machines;
using Tedd.MOS65xx.Hosting;

namespace Tedd.MOS65xx.Embedding;

/// <summary>
/// A Commodore 64 or 128 for a program that renders and plays it itself: a Vulkan/Direct3D/OpenGL game, an
/// Avalonia application, a video encoder. The picture comes out of <see cref="Video"/> as raw pixels the host
/// writes into its own memory (a mapped staging buffer, a locked bitmap); the sound comes out of
/// <see cref="Audio"/> as PCM for the host's audio callback.
/// </summary>
/// <remarks>
/// <para>
/// Two ways to run it. From the host's own loop: call <see cref="Update"/> once per tick with the elapsed time and
/// it runs the PAL frames that are due (50.125 per second). Or on a thread of its own: <see cref="Start"/> runs it
/// at real-time speed until <see cref="Stop"/>. Either way the renderer takes the newest picture with
/// <see cref="VideoOutput.TryAcquire"/> and the audio thread drains <see cref="AudioOutput.Fill(Span{float}, int)"/>.
/// </para>
/// <para>
/// Threading: every member may be called from any thread. Frames run under one lock, which input, media and
/// <see cref="Invoke(Action{EmulatorSession})"/> take as well, so a call waits at most for the frame in progress
/// (about 3 ms). The exceptions are the consumers: <see cref="VideoOutput.TryAcquire"/> belongs to one rendering
/// thread and <see cref="AudioOutput.Read(Span{float}, int)"/>/<see cref="AudioOutput.Fill(Span{float}, int)"/> to
/// one audio thread, and neither ever waits for the emulator.
/// </para>
/// </remarks>
public sealed class EmbeddedEmulator : IDisposable
{
    /// <summary>PAL frames per second: 985 248 Hz / (63 cycles x 312 lines).</summary>
    public const double FrameRate = CommodoreMachine.FrameRate;

    private const double FrameSeconds = 1.0 / FrameRate;

    private readonly object _sync = new();
    private EmulatorRunner? _runner;
    private double _accumulator;
    private int _maxFramesPerUpdate = 3;
    private bool _paused;
    private bool _disposed;

    /// <summary>A Commodore 64 (with a 1541 when <paramref name="roms"/> has the DOS ROM, see <see cref="EmbeddedEmulatorOptions.AttachDrive"/>).</summary>
    public EmbeddedEmulator(RomSet roms, EmbeddedEmulatorOptions? options = null)
        : this(options ?? new EmbeddedEmulatorOptions(),
            o => new EmulatorSession(roms ?? throw new ArgumentNullException(nameof(roms)), o.SampleRate, o.AttachDrive, o.Bindings))
    {
    }

    /// <summary>A Commodore 128 (<see cref="EmbeddedEmulatorOptions.Columns80"/> boots it on the 80 column screen).</summary>
    public EmbeddedEmulator(C128RomSet roms, EmbeddedEmulatorOptions? options = null)
        : this(options ?? new EmbeddedEmulatorOptions(),
            o => new EmulatorSession(roms ?? throw new ArgumentNullException(nameof(roms)), o.SampleRate, o.AttachDrive, o.Bindings, o.Columns80))
    {
    }

    private EmbeddedEmulator(EmbeddedEmulatorOptions options, Func<EmbeddedEmulatorOptions, EmulatorSession> createSession)
    {
        Session = createSession(options);
        Video = new VideoOutput();
        Audio = new AudioOutput(Session.SampleRate, options.AudioMaxLatencySeconds, options.AudioPrimeSeconds);
        Session.Video = Video;
        Session.Audio = Audio;
        Session.Command += command => Command?.Invoke(command);
    }

    /// <summary>
    /// A Commodore 64 from ROM images in memory: BASIC (8192 bytes), KERNAL (8192 bytes), character generator
    /// (4096 bytes) and optionally the 1541 DOS (16384 bytes; without it there is no disk drive). To load them from
    /// a folder use <c>new EmbeddedEmulator(RomSet.Load(directory))</c>.
    /// </summary>
    public static EmbeddedEmulator CreateC64(byte[] basic, byte[] kernal, byte[] chargen, byte[]? drive1541 = null, EmbeddedEmulatorOptions? options = null) =>
        new(new RomSet(basic, kernal, chargen, drive1541, "", drive1541 is null ? "in-memory (no drive)" : "in-memory"), options);

    /// <summary>The session underneath (machine, bindings, media). Touch it only inside <see cref="Invoke(Action{EmulatorSession})"/> while the emulator runs.</summary>
    public EmulatorSession Session { get; }

    /// <summary>The machine model.</summary>
    public MachineModel Model => Session.Model;

    /// <summary>Where the pictures come out.</summary>
    public VideoOutput Video { get; }

    /// <summary>Where the sound comes out.</summary>
    public AudioOutput Audio { get; }

    /// <summary>Sample rate of <see cref="Audio"/> in Hz.</summary>
    public int SampleRate => Session.SampleRate;

    /// <summary>True while the emulator runs on its own thread (<see cref="Start"/>).</summary>
    public bool IsRunning => _runner is not null;

    /// <summary>
    /// Upper bound on the frames one <see cref="Update"/> call runs (default 3). When the host falls further behind
    /// than this (a long hitch, a debugger break) the backlog is dropped instead of being caught up.
    /// </summary>
    public int MaxFramesPerUpdate
    {
        get => _maxFramesPerUpdate;
        set => _maxFramesPerUpdate = value < 1 ? 1 : value;
    }

    /// <summary>Freezes the machine (no cycles run) and discards queued audio.</summary>
    public bool Paused
    {
        get => _paused;
        set
        {
            lock (_sync)
            {
                if (_paused == value) return;
                _paused = value;
                _accumulator = 0;
                if (_runner is not null) _runner.Paused = value;
                Audio.Clear();
            }
        }
    }

    /// <summary>Runs as fast as possible (<see cref="Update"/>: <see cref="MaxFramesPerUpdate"/> frames per call; own thread: no pacing). Audio is muted.</summary>
    public bool Warp
    {
        get => Session.Warp;
        set { lock (_sync) Session.Warp = value; }
    }

    /// <summary>Which picture a C128 presents: the VIC-II, the VDC, or whichever the 40/80 key selects (ignored on a C64).</summary>
    public DisplayOutput Display
    {
        get => Session.Display;
        set { lock (_sync) Session.Display = value; }
    }

    /// <summary>Physical key (W3C code) to C64 action table used by <see cref="KeyDown"/>/<see cref="KeyUp"/>.</summary>
    public KeyBindings Bindings
    {
        get => Session.Bindings;
        set { lock (_sync) Session.Bindings = value ?? throw new ArgumentNullException(nameof(value)); }
    }

    /// <summary>Human readable description of the attached media.</summary>
    public string MediaDescription => Session.MediaDescription;

    /// <summary>
    /// Raised when a bound key requests a host command (reset, pause, warp, screenshot...), on the thread that
    /// called <see cref="KeyDown"/>. The 40/80 and CAPS LOCK toggles are applied before it is raised; the rest is
    /// up to the host.
    /// </summary>
    public event Action<SystemCommand>? Command;

    #region Running

    /// <summary>
    /// Advances the emulator by <paramref name="deltaSeconds"/> of wall-clock time: runs as many whole PAL frames
    /// as are due, at most <see cref="MaxFramesPerUpdate"/>, and returns how many ran (0 when paused or when less
    /// than a frame's worth of time has accumulated). Call it from the host's loop instead of <see cref="Start"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The emulator runs on its own thread.</exception>
    public int Update(double deltaSeconds)
    {
        lock (_sync)
        {
            ThrowIfRunningOrDisposed();
            if (_paused)
            {
                _accumulator = 0;
                return 0;
            }
            if (double.IsNaN(deltaSeconds) || deltaSeconds < 0)
                deltaSeconds = 0;

            int frames = 0;
            if (Session.Warp)
            {
                _accumulator = 0;
                for (; frames < _maxFramesPerUpdate; frames++)
                    Session.RunFrame();
                return frames;
            }

            _accumulator += deltaSeconds;
            while (_accumulator >= FrameSeconds && frames < _maxFramesPerUpdate)
            {
                Session.RunFrame();
                _accumulator -= FrameSeconds;
                frames++;
            }
            if (_accumulator >= FrameSeconds)
                _accumulator = 0; // still behind after the cap: drop the backlog rather than spiral
            return frames;
        }
    }

    /// <summary>Runs exactly one PAL frame regardless of time (stepping, tests, offline rendering). Returns false when paused.</summary>
    /// <exception cref="InvalidOperationException">The emulator runs on its own thread.</exception>
    public bool RunFrame()
    {
        lock (_sync)
        {
            ThrowIfRunningOrDisposed();
            if (_paused) return false;
            Session.RunFrame();
            return true;
        }
    }

    /// <summary>
    /// Runs the emulator on a thread of its own at real-time speed (50.125 frames per second) until
    /// <see cref="Stop"/> or <see cref="Dispose"/>. Does nothing when it already runs.
    /// </summary>
    public void Start()
    {
        lock (_sync)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(EmbeddedEmulator));
            if (_runner is not null) return;
            _accumulator = 0;
            _runner = new EmulatorRunner(Session, _sync, "Tedd.MOS65xx emulation") { Paused = _paused };
            _runner.Start();
        }
    }

    /// <summary>Stops the thread started by <see cref="Start"/> after the frame in progress; the machine keeps its state.</summary>
    public void Stop()
    {
        EmulatorRunner? runner;
        lock (_sync)
            runner = _runner;
        if (runner is null) return;
        // Outside the lock: the thread needs it to finish its frame before it can see the stop request.
        runner.Dispose();
        lock (_sync)
        {
            if (_runner == runner) _runner = null;
        }
    }

    /// <summary>Resets the machine (hard = power cycle, RAM cleared) and releases all input.</summary>
    public void Reset(bool hard = false)
    {
        lock (_sync)
        {
            Session.Reset(hard);
            _accumulator = 0;
            Audio.Clear();
        }
    }

    /// <summary>
    /// Runs <paramref name="action"/> with exclusive access to the session (between frames): memory pokes, chip
    /// state, anything this class does not wrap.
    /// </summary>
    public void Invoke(Action<EmulatorSession> action)
    {
        if (action is null) throw new ArgumentNullException(nameof(action));
        lock (_sync)
            action(Session);
    }

    /// <summary><see cref="Invoke(Action{EmulatorSession})"/> for something that produces a value, e.g. <c>Invoke(s => s.Machine.GetScreenText())</c>.</summary>
    public T Invoke<T>(Func<EmulatorSession, T> func)
    {
        if (func is null) throw new ArgumentNullException(nameof(func));
        lock (_sync)
            return func(Session);
    }

    /// <summary>Stops the thread (if any). The outputs stay readable.</summary>
    public void Dispose()
    {
        Stop();
        lock (_sync)
            _disposed = true;
    }

    private void ThrowIfRunningOrDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(EmbeddedEmulator));
        if (_runner is not null) throw new InvalidOperationException("The emulator runs on its own thread (Start); call Stop before driving it from Update or RunFrame.");
    }

    #endregion

    #region Input

    /// <summary>A physical key went down: a W3C <c>KeyboardEvent.code</c> name (see <see cref="AvaloniaKeyCodes"/>, <see cref="GlfwKeyCodes"/>). Null or unbound codes are ignored.</summary>
    public void KeyDown(string? code)
    {
        if (string.IsNullOrEmpty(code)) return;
        lock (_sync) Session.KeyDown(code!);
    }

    /// <summary>A physical key went up.</summary>
    public void KeyUp(string? code)
    {
        if (string.IsNullOrEmpty(code)) return;
        lock (_sync) Session.KeyUp(code!);
    }

    /// <summary>Drives joystick port 1 or 2 directly (game pads, touch controls).</summary>
    public void SetJoystick(int port, JoystickInput input, bool pressed)
    {
        lock (_sync) Session.SetJoystick(port, input, pressed);
    }

    /// <summary>Presses or releases a C64 key directly, bypassing the bindings (on-screen keyboards).</summary>
    public void SetKey(C64Key key, bool pressed)
    {
        lock (_sync) Session.SetKey(key, pressed);
    }

    /// <summary>Releases every key and joystick input (call when the host window loses focus).</summary>
    public void ReleaseAllInput()
    {
        lock (_sync) Session.ReleaseAllInput();
    }

    /// <summary>Types text through the KERNAL keyboard buffer ('\n' = RETURN).</summary>
    public void TypeText(string text)
    {
        lock (_sync) Session.TypeText(text);
    }

    #endregion

    #region Media

    /// <summary>
    /// Attaches a disk (.d64), tape/program (.t64, .prg) or cartridge (.crt, .bin) image, detected from
    /// <paramref name="fileName"/>'s extension or the content. With <paramref name="autostart"/> the first program is
    /// loaded and run as soon as BASIC is ready (a C128 boot disk is booted by a reset).
    /// </summary>
    public void AttachMedia(byte[] data, string fileName, bool autostart = true)
    {
        lock (_sync) Session.AttachAuto(data, fileName, autostart);
    }

    /// <summary>Removes the disk from the drive.</summary>
    public void EjectDisk()
    {
        lock (_sync) Session.EjectDisk();
    }

    /// <summary>Removes the cartridge and resets.</summary>
    public void DetachCartridge()
    {
        lock (_sync) Session.DetachCartridge();
    }

    #endregion
}
