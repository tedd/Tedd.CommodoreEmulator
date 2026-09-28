using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using Tedd.Commodore.Emulator.Machines;
using Tedd.Commodore.Emulator.Tools;
using Tedd.Commodore.Hosting;
using static SDL2.SDL;

namespace Tedd.Commodore.Sdl;

/// <summary>
/// The SDL window: renders the <see cref="SdlVideoSink"/> texture with integer scaling, pumps SDL events into the
/// session (keyboard, game controllers, drag-and-drop, focus), executes bound host commands and prints a status line.
/// Runs entirely on the thread that created it (SDL's main thread); the emulator runs on the
/// <see cref="EmulatorRunner"/>'s thread and is only touched through <see cref="EmulatorRunner.Invoke"/>.
/// </summary>
internal sealed class SdlHost : IDisposable
{
    private const string TitlePrefix = "Tedd.Commodore ";
    private const double MinPresentInterval = 1.0 / 60;   // never present faster than this (warp mode)
    private const double StatusInterval = 1.0;

    private readonly EmulatorSession _session;
    private readonly EmulatorRunner _runner;
    private readonly SdlVideoSink _video;
    private readonly SdlAudioSink? _audio;
    private readonly SdlGameControllers _pads;
    private IntPtr _window;
    private IntPtr _renderer;
    private IntPtr _texture;
    private volatile bool _quit;
    private bool _needsRedraw = true;
    private int _statusLength;
    private int _screenshotCounter;

    public SdlHost(EmulatorSession session, EmulatorRunner runner, SdlVideoSink video, SdlAudioSink? audio, int scale, int joyPort)
    {
        _session = session;
        _runner = runner;
        _video = video;
        _audio = audio;

        SDL_SetHint(SDL_HINT_RENDER_SCALE_QUALITY, "0"); // nearest neighbour: crisp pixels at integer scales

        string title = TitlePrefix + (session.Model == MachineModel.C128 ? "C128" : "C64");
        // The window is sized for the 40 column picture; the 80 column one gets the same height and twice the width.
        int windowWidth = (session.ShowingVdc ? Emulator.Video.Vdc8563.FrameWidth : SdlVideoSink.DefaultWidth) * scale;
        _window = SDL_CreateWindow(title, SDL_WINDOWPOS_CENTERED, SDL_WINDOWPOS_CENTERED,
            windowWidth, SdlVideoSink.DefaultHeight * scale,
            SDL_WindowFlags.SDL_WINDOW_SHOWN | SDL_WindowFlags.SDL_WINDOW_RESIZABLE | SDL_WindowFlags.SDL_WINDOW_ALLOW_HIGHDPI);
        if (_window == IntPtr.Zero)
            throw new SdlException("SDL_CreateWindow");
        SDL_SetWindowMinimumSize(_window, SdlVideoSink.DefaultWidth, SdlVideoSink.DefaultHeight);

        // No PRESENTVSYNC: presentation is paced by the emulator's frames (see Run), not by the display.
        _renderer = SDL_CreateRenderer(_window, -1, SDL_RendererFlags.SDL_RENDERER_ACCELERATED);
        if (_renderer == IntPtr.Zero)
            _renderer = SDL_CreateRenderer(_window, -1, SDL_RendererFlags.SDL_RENDERER_SOFTWARE);
        if (_renderer == IntPtr.Zero)
            throw new SdlException("SDL_CreateRenderer");

        _texture = _video.CreateTexture(_renderer);
        SDL_EventState(SDL_EventType.SDL_DROPFILE, SDL_ENABLE);

        _pads = new SdlGameControllers((port, input, pressed) => _runner.Invoke(() => _session.SetJoystick(port, input, pressed)), joyPort);
        _pads.Log += Log;
        foreach (var name in _pads.Names)
            Log($"Controller connected: {name} -> joystick port {joyPort}");

        _session.Command += OnCommand;
        Console.CancelKeyPress += OnCancelKeyPress;
    }

    /// <summary>Runs the event/render loop until the window is closed.</summary>
    public void Run()
    {
        var clock = Stopwatch.StartNew();
        double lastPresent = double.NegativeInfinity;
        double lastStatus = 0;
        int presentedSinceStatus = 0;

        while (!_quit)
        {
            while (SDL_PollEvent(out var e) != 0)
            {
                HandleEvent(in e);
                if (_quit) break;
            }
            if (_quit) break;

            double now = clock.Elapsed.TotalSeconds;
            bool fresh = now - lastPresent >= MinPresentInterval && _video.HasNewFrame;
            if (fresh || _needsRedraw)
            {
                if (fresh && _video.Acquire(out bool sizeChanged))
                {
                    if (sizeChanged)
                    {
                        // The C128 switched between its 40 and 80 column pictures: the texture must match.
                        SDL_DestroyTexture(_texture);
                        _texture = _video.CreateTexture(_renderer);
                    }
                    _video.Upload(_texture);
                }
                Render();
                lastPresent = now;
                _needsRedraw = false;
                presentedSinceStatus++;
            }
            else
            {
                SDL_Delay(1);
            }

            if (now - lastStatus >= StatusInterval)
            {
                PrintStatus(presentedSinceStatus / (now - lastStatus));
                presentedSinceStatus = 0;
                lastStatus = now;
            }
        }
        EndStatusLine();
    }

    #region Rendering

    private void Render()
    {
        SDL_GetRendererOutputSize(_renderer, out int w, out int h);
        var dst = FitRect(w, h, _video.Width, _video.Height);
        SDL_SetRenderDrawColor(_renderer, 0, 0, 0, 255);
        SDL_RenderClear(_renderer);
        SDL_RenderCopy(_renderer, _texture, IntPtr.Zero, ref dst);
        SDL_RenderPresent(_renderer);
    }

    /// <summary>Largest integer multiple of the picture that fits, centred; shrinks proportionally if the output is smaller.</summary>
    internal static SDL_Rect FitRect(int outputWidth, int outputHeight, int sw, int sh)
    {
        int scale = Math.Min(outputWidth / sw, outputHeight / sh);
        int dw, dh;
        if (scale >= 1)
        {
            dw = sw * scale;
            dh = sh * scale;
        }
        else
        {
            double f = Math.Min(outputWidth / (double)sw, outputHeight / (double)sh);
            dw = Math.Max(1, (int)(sw * f));
            dh = Math.Max(1, (int)(sh * f));
        }
        return new SDL_Rect { x = (outputWidth - dw) / 2, y = (outputHeight - dh) / 2, w = dw, h = dh };
    }

    private void ToggleFullscreen()
    {
        bool fullscreen = (SDL_GetWindowFlags(_window) & (uint)SDL_WindowFlags.SDL_WINDOW_FULLSCREEN) != 0;
        if (SDL_SetWindowFullscreen(_window, fullscreen ? 0u : (uint)SDL_WindowFlags.SDL_WINDOW_FULLSCREEN_DESKTOP) != 0)
        {
            Log("Fullscreen toggle failed: " + SDL_GetError());
            return;
        }
        SDL_ShowCursor(fullscreen ? SDL_ENABLE : SDL_DISABLE);
        _needsRedraw = true;
    }

    #endregion

    #region Events

    private void HandleEvent(in SDL_Event e)
    {
        switch (e.type)
        {
            case SDL_EventType.SDL_QUIT:
                _quit = true;
                break;
            case SDL_EventType.SDL_WINDOWEVENT:
                switch (e.window.windowEvent)
                {
                    case SDL_WindowEventID.SDL_WINDOWEVENT_CLOSE:
                        _quit = true;
                        break;
                    case SDL_WindowEventID.SDL_WINDOWEVENT_FOCUS_LOST:
                        ReleaseAllInput();
                        break;
                    case SDL_WindowEventID.SDL_WINDOWEVENT_SIZE_CHANGED:
                    case SDL_WindowEventID.SDL_WINDOWEVENT_RESIZED:
                    case SDL_WindowEventID.SDL_WINDOWEVENT_EXPOSED:
                    case SDL_WindowEventID.SDL_WINDOWEVENT_RESTORED:
                    case SDL_WindowEventID.SDL_WINDOWEVENT_SHOWN:
                        _needsRedraw = true;
                        break;
                }
                break;
            case SDL_EventType.SDL_KEYDOWN:
                OnKey(in e.key, true);
                break;
            case SDL_EventType.SDL_KEYUP:
                OnKey(in e.key, false);
                break;
            case SDL_EventType.SDL_DROPFILE:
                OnDrop(e.drop.file);
                break;
            default:
                _pads.HandleEvent(in e);
                break;
        }
    }

    private void OnKey(in SDL_KeyboardEvent key, bool down)
    {
        if (key.repeat != 0) return;
        var scancode = key.keysym.scancode;

        // Host hotkeys: left Alt + Enter / left Alt + F toggle fullscreen. Right Alt is joystick fire in the default
        // bindings, so only the left one acts as a modifier. Key-ups are always forwarded so a C64 key can never stick.
        if (down && (key.keysym.mod & SDL_Keymod.KMOD_LALT) != 0 &&
            scancode is SDL_Scancode.SDL_SCANCODE_RETURN or SDL_Scancode.SDL_SCANCODE_KP_ENTER or SDL_Scancode.SDL_SCANCODE_F)
        {
            ToggleFullscreen();
            return;
        }

        var code = SdlKeyCodes.ToCode(scancode);
        if (code is null) return;
        if (down)
            _runner.Invoke(() => _session.KeyDown(code));
        else
            _runner.Invoke(() => _session.KeyUp(code));
    }

    private void ReleaseAllInput()
    {
        _runner.Invoke(_session.ReleaseAllInput);
        _pads.Release();
    }

    private void OnDrop(IntPtr file)
    {
        if (file == IntPtr.Zero) return;
        string path = UTF8_ToManaged(file, freePtr: true);
        AttachFile(path, autostart: true);
    }

    /// <summary>Attaches a file by extension/content (used for drag-and-drop and bare command line arguments).</summary>
    public void AttachFile(string path, bool autostart)
    {
        try
        {
            var data = File.ReadAllBytes(path);
            var name = Path.GetFileName(path);
            _runner.Invoke(() => _session.AttachAuto(data, name, autostart));
            Log($"Attached {path}: {_session.MediaDescription}");
        }
        catch (Exception ex)
        {
            Log($"Could not attach {path}: {ex.Message}");
        }
    }

    private void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
    {
        e.Cancel = true;
        _quit = true;
    }

    #endregion

    #region Host commands

    // Raised by EmulatorSession.KeyDown for "sys:" bindings, i.e. on this thread while _runner.Invoke holds the
    // machine lock. Invoke is re-entrant, so the handlers below work from any context.
    private void OnCommand(SystemCommand command)
    {
        switch (command)
        {
            case SystemCommand.Reset:
                _runner.Invoke(() => _session.Reset(hard: false));
                Log("Reset");
                break;
            case SystemCommand.HardReset:
                _runner.Invoke(() => _session.Reset(hard: true));
                Log("Hard reset");
                break;
            case SystemCommand.Pause:
                _runner.Paused = !_runner.Paused;
                Log(_runner.Paused ? "Paused" : "Resumed");
                break;
            case SystemCommand.Warp:
                _runner.Warp = !_runner.Warp;
                Log(_runner.Warp ? "Warp on" : "Warp off");
                break;
            case SystemCommand.Screenshot:
                SaveScreenshot();
                break;
            case SystemCommand.MemoryViewer:
                DumpMemory();
                break;
            case SystemCommand.ToggleColumns:
                Log(_session.Display40Columns ? "40/80 key: 40 columns (VIC-II)" : "40/80 key: 80 columns (VDC)");
                break;
            case SystemCommand.CapsLock:
                Log(_session.CapsLock ? "CAPS LOCK on" : "CAPS LOCK off");
                break;
        }
    }

    private void SaveScreenshot()
    {
        try
        {
            int w = 0, h = 0;
            uint[] pixels = Array.Empty<uint>();
            // Under the runner lock the machine is between frames, so the frame buffer is a complete picture.
            _runner.Invoke(() =>
            {
                var frame = _session.CurrentFrame;
                w = frame.Width;
                h = frame.Height;
                pixels = new uint[w * h];
                frame.CopyVisible(pixels);
            });

            string dir = AppContext.BaseDirectory;
            string path;
            do
            {
                path = Path.Combine(dir, $"Tedd.Commodore-{DateTime.Now:yyyyMMdd-HHmmss}{(_screenshotCounter == 0 ? "" : "-" + _screenshotCounter)}.png");
                _screenshotCounter++;
            } while (File.Exists(path));
            PngWriter.Save(path, w, h, pixels);
            Log("Screenshot saved: " + path);
        }
        catch (Exception ex)
        {
            Log("Screenshot failed: " + ex.Message);
        }
    }

    private void DumpMemory()
    {
        var sb = new StringBuilder();
        _runner.Invoke(() =>
        {
            var m = _session.Machine;
            var cpu = m.Cpu;
            var ram = m.Ram;
            string flags = new(new[]
            {
                cpu.FlagNegative ? 'N' : '.', cpu.FlagOverflow ? 'V' : '.', '-', '.',
                cpu.FlagDecimal ? 'D' : '.', cpu.FlagInterrupt ? 'I' : '.', cpu.FlagZero ? 'Z' : '.', cpu.FlagCarry ? 'C' : '.',
            });
            sb.AppendLine($"{(m.Model == MachineModel.C128 ? "8502" : "6510")}: PC=${cpu.PC:X4} A=${cpu.A:X2} X=${cpu.X:X2} Y=${cpu.Y:X2} S=${cpu.S:X2} P=${cpu.P:X2} [{flags}]  " +
                          $"cycles={m.Cycles} frame={m.Frames} raster={m.Vic.RasterLine} screen=${m.ScreenAddress:X4}");
            if (m.Drive is { } drive)
                sb.AppendLine($"1541: PC=${drive.Cpu.PC:X4} A=${drive.Cpu.A:X2} X=${drive.Cpu.X:X2} Y=${drive.Cpu.Y:X2} " +
                              $"track={drive.Disk.Track:F1} motor={(drive.MotorOn ? "on" : "off")} LED={(drive.Led ? "on" : "off")}");
            sb.AppendLine("Zero page:");
            var ascii = new char[16];
            for (int row = 0; row < 256; row += 16)
            {
                sb.Append($"{row:X4}: ");
                for (int i = 0; i < 16; i++)
                {
                    byte b = ram[row + i];
                    sb.Append(b.ToString("X2")).Append(i == 7 ? "  " : " ");
                    ascii[i] = b is >= 0x20 and < 0x7F ? (char)b : '.';
                }
                sb.Append(' ').Append(ascii).AppendLine();
            }
        });
        Log(sb.ToString().TrimEnd());
    }

    #endregion

    #region Console output

    private void PrintStatus(double displayFps)
    {
        var m = _session.Machine;
        var s = new StringBuilder();
        s.Append(_runner.Paused ? "paused     " : $"{_runner.MeasuredFps,5:F1} fps  ");
        if (_runner.Warp) s.Append("warp  ");
        s.Append($"display {displayFps,3:F0} fps");
        s.Append(" | ").Append(string.IsNullOrEmpty(_session.MediaDescription) ? "no media" : _session.MediaDescription);
        if (m.Drive is { } drive)
        {
            s.Append($" | drive {drive.DeviceNumber}: track {drive.Disk.Track,4:F1} LED {(drive.Led ? "on " : "off")}");
            if (drive.Disk.Disk is null) s.Append(" (no disk)");
        }
        else
        {
            s.Append(" | no drive");
        }
        if (_audio is not null)
            s.Append($" | audio {_audio.QueuedSeconds * 1000,3:F0} ms");
        if (_pads.Count > 0)
            s.Append($" | {_pads.Count} controller{(_pads.Count == 1 ? "" : "s")}");

        string line = s.ToString();
        Console.Write('\r' + line.PadRight(_statusLength));
        _statusLength = line.Length;
    }

    /// <summary>Writes a message on its own line, clearing the status line first.</summary>
    private void Log(string message)
    {
        if (_statusLength > 0)
        {
            Console.Write('\r' + new string(' ', _statusLength) + '\r');
            _statusLength = 0;
        }
        Console.WriteLine(message);
    }

    private void EndStatusLine()
    {
        if (_statusLength > 0)
        {
            Console.WriteLine();
            _statusLength = 0;
        }
    }

    #endregion

    public void Dispose()
    {
        _session.Command -= OnCommand;
        Console.CancelKeyPress -= OnCancelKeyPress;
        _pads.Dispose();
        if (_texture != IntPtr.Zero) { SDL_DestroyTexture(_texture); _texture = IntPtr.Zero; }
        if (_renderer != IntPtr.Zero) { SDL_DestroyRenderer(_renderer); _renderer = IntPtr.Zero; }
        if (_window != IntPtr.Zero) { SDL_DestroyWindow(_window); _window = IntPtr.Zero; }
    }
}
