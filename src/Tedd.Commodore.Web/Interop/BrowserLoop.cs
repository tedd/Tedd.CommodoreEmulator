using System.Diagnostics;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using Tedd.Commodore.Emulator.C64;
using Tedd.Commodore.Hosting;

namespace Tedd.Commodore.Web.Interop;

/// <summary>
/// Paces the emulation from the browser's <c>requestAnimationFrame</c>. <c>c64.js</c> calls the exported
/// <see cref="Tick"/> once per animation frame; the loop accumulates wall-clock time and runs as many PAL
/// frames (1/50.125 s each) as are due, at most <see cref="MaxFramesPerTick"/> to avoid a spiral of death
/// when the machine is slower than real time, then draws the last frame through the
/// <see cref="BrowserVideoSink"/>. In warp mode it runs frames for a fixed time budget instead.
/// </summary>
[SupportedOSPlatform("browser")]
public static partial class BrowserLoop
{
    public const double FrameSeconds = 1.0 / C64.FrameRate;
    public const int MaxFramesPerTick = 3;
    /// <summary>Wall-clock budget per animation frame in warp mode (leaves time for the browser to paint).</summary>
    public const double WarpBudgetMs = 12.0;

    private static EmulatorSession? _session;
    private static BrowserVideoSink? _video;
    private static readonly Action<double> TickDelegate = Tick;
    private static double _lastTime = -1;
    private static double _accumulator;
    private static int _fpsFrames;
    private static double _fpsStart;
    private static double _busyMs;
    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    /// <summary>Emulated frames per second measured over the last half second (50.1 = real time).</summary>
    public static double MeasuredFps { get; private set; }

    /// <summary>Share of wall-clock time spent inside <c>RunFrame</c> (1.0 = the machine cannot keep up).</summary>
    public static double Load { get; private set; }

    public static long TotalFrames { get; private set; }

    /// <summary>Set when the emulator threw; the loop is stopped.</summary>
    public static Exception? Error { get; private set; }

    public static bool IsRunning { get; private set; }

    /// <summary>Raised about twice a second from the tick (use it to refresh status displays).</summary>
    public static event Action? StatusTick;

    public static EmulatorSession? Session => _session;

    public static void Attach(EmulatorSession session, BrowserVideoSink video)
    {
        _session = session;
        _video = video;
        ResetPacing();
    }

    public static void Detach()
    {
        _session = null;
        _video = null;
    }

    /// <summary>Forgets accumulated time (after pause/resume or a long stall).</summary>
    public static void ResetPacing()
    {
        _lastTime = -1;
        _accumulator = 0;
        _fpsFrames = 0;
        _busyMs = 0;
    }

    public static async Task StartAsync()
    {
        if (IsRunning) return;
        Error = null;
        ResetPacing();
        await C64Js.StartLoop(TickDelegate);
        IsRunning = true;
    }

    public static void Stop()
    {
        C64Js.StopLoop();
        IsRunning = false;
    }

    /// <summary>Called by <c>c64.js</c> from requestAnimationFrame with the DOMHighResTimeStamp in ms.</summary>
    [JSExport]
    public static void Tick(double timestampMs)
    {
        var session = _session;
        if (session is null || Error is not null) return;

        double t = timestampMs / 1000.0;
        if (_lastTime < 0)
        {
            _lastTime = t;
            _fpsStart = t;
        }
        double dt = t - _lastTime;
        _lastTime = t;
        if (dt > 0.25) dt = 0.25; // the tab was hidden or the browser stalled: do not try to catch up

        int frames = 0;
        double start = Clock.Elapsed.TotalMilliseconds;
        try
        {
            if (session.Paused)
            {
                _accumulator = 0;
            }
            else if (session.Warp)
            {
                _accumulator = 0;
                while (Clock.Elapsed.TotalMilliseconds - start < WarpBudgetMs && frames < 100)
                {
                    session.RunFrame();
                    frames++;
                }
            }
            else
            {
                _accumulator += dt;
                while (_accumulator >= FrameSeconds && frames < MaxFramesPerTick)
                {
                    session.RunFrame();
                    _accumulator -= FrameSeconds;
                    frames++;
                }
                if (_accumulator > FrameSeconds * MaxFramesPerTick)
                    _accumulator = 0; // we are behind; drop the backlog instead of stuttering forever
            }
            if (frames > 0) _video?.Flush();
        }
        catch (Exception ex)
        {
            Error = ex;
            C64Js.StopLoop();
            IsRunning = false;
            StatusTick?.Invoke();
            return;
        }
        _busyMs += Clock.Elapsed.TotalMilliseconds - start;

        _fpsFrames += frames;
        TotalFrames += frames;
        double span = t - _fpsStart;
        if (span >= 0.5)
        {
            MeasuredFps = _fpsFrames / span;
            Load = Math.Min(1.0, _busyMs / (span * 1000.0));
            _fpsFrames = 0;
            _busyMs = 0;
            _fpsStart = t;
            StatusTick?.Invoke();
        }
    }
}
