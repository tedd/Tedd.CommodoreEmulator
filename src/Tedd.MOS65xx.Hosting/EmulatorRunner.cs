using System;
using System.Diagnostics;
using System.Threading;
using Tedd.MOS65xx.Emulator.Machines;

namespace Tedd.MOS65xx.Hosting;

/// <summary>
/// Runs an <see cref="EmulatorSession"/> on a dedicated thread at real-time (PAL) speed, or as fast as possible
/// in warp mode. Hosts that have their own frame loop (browsers, game engines) call
/// <see cref="EmulatorSession.RunFrame"/> themselves instead of using this class.
/// </summary>
public sealed class EmulatorRunner : IDisposable
{
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _resume = new(true);
    private readonly object _sync;
    private volatile bool _running = true;
    private volatile bool _paused;
    private double _measuredFps;

    public EmulatorRunner(EmulatorSession session, string threadName = "Commodore emulation")
        : this(session, new object(), threadName)
    {
    }

    /// <summary>
    /// A runner whose frames and <see cref="Invoke(Action)"/> calls hold <paramref name="syncRoot"/>, for a host that
    /// also drives the session itself (between runs, or instead of this thread): locking the same object keeps its
    /// own calls from overlapping a frame.
    /// </summary>
    public EmulatorRunner(EmulatorSession session, object syncRoot, string threadName = "Commodore emulation")
    {
        Session = session;
        _sync = syncRoot ?? throw new ArgumentNullException(nameof(syncRoot));
        _thread = new Thread(Run) { IsBackground = true, Name = threadName };
    }

    public EmulatorSession Session { get; }

    /// <summary>Frames per second measured over the last second.</summary>
    public double MeasuredFps => _measuredFps;

    public bool IsRunning => _running && _thread.IsAlive;

    public void Start()
    {
        if (!_thread.IsAlive) _thread.Start();
    }

    /// <summary>true = frozen (no cycles executed); the machine can then be inspected safely through <see cref="Invoke"/>.</summary>
    public bool Paused
    {
        get => _paused;
        set
        {
            if (_paused == value) return;
            _paused = value;
            if (value) _resume.Reset(); else _resume.Set();
            if (!value) Session.Audio.Clear();
        }
    }

    public bool Warp
    {
        get => Session.Warp;
        set => Session.Warp = value;
    }

    /// <summary>Runs an action with exclusive access to the machine (between frames, or immediately when frozen).</summary>
    public void Invoke(Action action)
    {
        if (Thread.CurrentThread == _thread)
        {
            action();
            return;
        }
        lock (_sync)
        {
            action();
        }
    }

    /// <summary>
    /// <see cref="Invoke(Action)"/> for something that produces a value. The inner lambda has a statement body on
    /// purpose: an expression bodied one is a <see cref="Func{T}"/> as well and binds to this overload again,
    /// which recurses until the stack runs out.
    /// </summary>
    public T Invoke<T>(Func<T> func)
    {
        T result = default!;
        Invoke(() => { result = func(); });
        return result;
    }

    private void Run()
    {
        var clock = Stopwatch.StartNew();
        double nextFrame = 0;
        const double frameSeconds = 1.0 / CommodoreMachine.FrameRate;
        int fpsFrames = 0;
        double fpsStart = 0;

        while (_running)
        {
            if (_paused)
            {
                _resume.Wait(50);
                if (_paused)
                {
                    clock.Restart();
                    nextFrame = 0;
                    fpsStart = 0;
                    fpsFrames = 0;
                    continue;
                }
            }

            lock (_sync)
            {
                Session.RunFrame();
            }

            fpsFrames++;
            double now = clock.Elapsed.TotalSeconds;
            if (now - fpsStart >= 1.0)
            {
                _measuredFps = fpsFrames / (now - fpsStart);
                fpsFrames = 0;
                fpsStart = now;
            }

            if (Session.Warp)
            {
                nextFrame = now;
                continue;
            }

            nextFrame += frameSeconds;
            double ahead = nextFrame - clock.Elapsed.TotalSeconds;
            if (ahead > 0.002)
                Thread.Sleep((int)((ahead - 0.001) * 1000));
            while (clock.Elapsed.TotalSeconds < nextFrame)
                Thread.SpinWait(50);
            if (clock.Elapsed.TotalSeconds - nextFrame > 0.25)
                nextFrame = clock.Elapsed.TotalSeconds;
        }
    }

    public void Dispose()
    {
        _running = false;
        _paused = false;
        _resume.Set();
        if (_thread.IsAlive && Thread.CurrentThread != _thread)
            _thread.Join(1000);
    }
}
