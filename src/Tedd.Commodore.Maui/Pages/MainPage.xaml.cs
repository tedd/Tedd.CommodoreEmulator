using Tedd.Commodore.Emulator.C64;
using Tedd.Commodore.Emulator.Media;
using Tedd.Commodore.Emulator.Tools;
using Tedd.Commodore.Hosting;
using Tedd.Commodore.Maui.Controls;
using Tedd.Commodore.Maui.Services;

namespace Tedd.Commodore.Maui.Pages;

/// <summary>
/// The emulator window. The machine lives in an <see cref="EmulatorSession"/> driven by an
/// <see cref="EmulatorRunner"/> thread; everything that touches it from the UI thread goes through
/// <see cref="EmulatorRunner.Invoke(Action)"/>. Key events are translated to W3C codes by the
/// <see cref="KeyboardHook"/> and fed to the session, which applies the user's <see cref="KeyBindings"/>.
/// </summary>
public partial class MainPage : ContentPage
{
    /// <summary>Where the key bindings are persisted (%AppData%\Tedd.Commodore\keybindings.json).</summary>
    public static readonly string BindingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Tedd.Commodore", "keybindings.json");

    /// <summary>
    /// How often the emulator thread is invited to publish a frame at normal speed: a little shorter than the
    /// 20 ms of a PAL frame, so every frame the machine draws finds an invitation waiting and none is dropped.
    /// </summary>
    private static readonly TimeSpan NormalFrameInterval = TimeSpan.FromMilliseconds(16);

    /// <summary>
    /// The same while warping. The machine then draws frames several hundred times a second and there is no
    /// point showing them all: a redraw is a swap chain present, and asking for one as often as the display
    /// refreshes leaves this thread with no time between presents for the mouse - and the compositor with none
    /// for the rest of the desktop, which is why warp used to slow down windows belonging to other programs.
    /// Fifteen a second is plenty to watch a machine race, and leaves the window as responsive as ever.
    /// </summary>
    private static readonly TimeSpan WarpFrameInterval = TimeSpan.FromMilliseconds(66);

    private readonly ToolWindow<MemoryViewerPage> _memoryViewer = new();
    private readonly ToolWindow<SpriteViewerPage> _spriteViewer = new();
    private readonly ToolWindow<AudioVisualizerPage> _audioVisualizer = new();
    private readonly ToolWindow<CharsetViewerPage> _charsetViewer = new();

    private EmulatorSession? _session;
    private EmulatorRunner? _runner;
    private MauiVideoSink? _videoSink;
    private AudioOutput? _audioOutput;
    private AudioTap? _audioTap;
    private KeyboardHook? _keyboard;
    private IDispatcherTimer? _statusTimer;
    private IDispatcherTimer? _frameTimer;
    private Window? _window;
    private string? _diskPath;
    private string _romDescription = "";
    private bool _started;

    public MainPage()
    {
        InitializeComponent();
    }

    private async void Page_Loaded(object? sender, EventArgs e)
    {
        if (_started) return;
        _started = true;

        var roms = RomSet.TryLoadDefault();
        if (roms is null)
        {
            await DisplayAlertAsync("ROMs missing",
                "No ROM images found.\n\nPut basic.901226-01.bin (or 64c.251913-01.bin), a KERNAL and a character ROM " +
                "into a 'roms' folder next to the executable, or point the C64_ROMS environment variable at them.",
                "OK");
            Application.Current?.Quit();
            return;
        }

        var bindings = KeyBindings.LoadOrDefault(BindingsPath);
        var session = new EmulatorSession(roms, sampleRate: 44100, attachDrive: true, bindings: bindings);
        _session = session;

        _videoSink = new MauiVideoSink();
        Screen.Source = _videoSink.Bitmap;
        session.Video = _videoSink;

        _audioOutput = AudioOutput.TryCreate(session.SampleRate);
        _audioTap = new AudioTap(_audioOutput, session.SampleRate);
        session.Audio = _audioTap;

        session.Command += OnSessionCommand;
        session.Bindings.Changed += OnBindingsChanged;

        _runner = new EmulatorRunner(session);
        DriveMenu.IsEnabled = roms.Drive1541 is not null;
        _romDescription = roms.Description;
        MediaText.Text = _romDescription;
        UpdateMenuGestures();

        _window = Window;
        if (_window is not null)
        {
            _window.Deactivated += OnWindowDeactivated;
            _keyboard = KeyboardHook.Attach(_window);
            if (_keyboard is not null)
            {
                _keyboard.KeyDown += OnKeyDown;
                _keyboard.KeyUp += OnKeyUp;
            }
        }

        _statusTimer = Dispatcher.CreateTimer();
        _statusTimer.Interval = TimeSpan.FromMilliseconds(250);
        _statusTimer.Tick += (_, _) => UpdateStatus();
        _statusTimer.Start();

        // This thread, not the machine, decides how often the picture is drawn: the emulator thread publishes a
        // frame only once this has asked for one, and it asks from its own message loop, so a thread that falls
        // behind simply asks later. See MauiVideoSink.RequestFrame.
        _frameTimer = Dispatcher.CreateTimer();
        _frameTimer.Interval = NormalFrameInterval;
        _frameTimer.Tick += (_, _) => _videoSink?.RequestFrame();
        _frameTimer.Start();
        _runner.Start();
    }

    private void Page_Unloaded(object? sender, EventArgs e) => Shutdown();

    private void Shutdown()
    {
        _statusTimer?.Stop();
        _statusTimer = null;
        _frameTimer?.Stop();
        _frameTimer = null;
        if (_window is not null)
        {
            _window.Deactivated -= OnWindowDeactivated;
            _window = null;
        }
        if (_keyboard is not null)
        {
            _keyboard.KeyDown -= OnKeyDown;
            _keyboard.KeyUp -= OnKeyUp;
            _keyboard.Dispose();
            _keyboard = null;
        }
        _memoryViewer.Close();
        _spriteViewer.Close();
        _audioVisualizer.Close();
        _charsetViewer.Close();
        if (_session is not null)
        {
            _session.Command -= OnSessionCommand;
            _session.Bindings.Changed -= OnBindingsChanged;
            _session = null;
        }
        _runner?.Dispose();
        _runner = null;
        // After the runner: the emulator thread writes into the sink's buffers until it stops.
        Screen.Source = null;
        _videoSink?.Dispose();
        _videoSink = null;
        _audioOutput?.Dispose();
        _audioOutput = null;
    }

    private void UpdateStatus()
    {
        if (_runner is null || _session is null) return;
        FpsText.Text = $"{_runner.MeasuredFps:0.0} fps";
        StateText.Text = _runner.Paused ? "Frozen" : _runner.Warp ? "Warp" : "Running";
        var drive = _session.Machine.Drive;
        if (drive is null)
        {
            DriveLed.Fill = new SolidColorBrush(Colors.DimGray);
            DriveText.Text = "No drive";
        }
        else
        {
            DriveLed.Fill = new SolidColorBrush(drive.Led ? Colors.Red : Colors.DarkRed);
            string disk = drive.Disk.Disk is null ? "no disk" : Path.GetFileName(_diskPath ?? "disk");
            DriveText.Text = $"8: {disk}  T{drive.Disk.Track:0.#}{(drive.MotorOn ? " *" : "")}";
        }
        string media = _session.MediaDescription;
        MediaText.Text = media.Length == 0 ? _romDescription : media;
    }

    #region Session commands and bindings

    /// <summary>Raised by the session when a key bound to a host command is pressed (may be on any thread).</summary>
    private void OnSessionCommand(SystemCommand command) => Dispatcher.Dispatch(() => HandleCommand(command));

    private void HandleCommand(SystemCommand command)
    {
        if (_runner is null) return;
        switch (command)
        {
            case SystemCommand.Reset: DoReset(hard: false); break;
            case SystemCommand.HardReset: DoReset(hard: true); break;
            case SystemCommand.Pause: SetPaused(!_runner.Paused); break;
            case SystemCommand.Warp: SetWarp(!_runner.Warp); break;
            case SystemCommand.Screenshot: _ = SaveScreenshotAsync(); break;
            case SystemCommand.MemoryViewer: ShowMemoryViewer(); break;
            case SystemCommand.SpriteViewer: ShowSpriteViewer(); break;
        }
    }

    private void OnBindingsChanged() => Dispatcher.Dispatch(UpdateMenuGestures);

    /// <summary>
    /// Shows the key bound to each host command next to its menu item. MAUI menu items have no separate
    /// gesture column, so the key is appended to the text.
    /// </summary>
    private void UpdateMenuGestures()
    {
        if (_session is null) return;
        var b = _session.Bindings;
        string Gesture(SystemCommand cmd, string fallback)
        {
            var code = b.CodesFor(InputAction.ForSystem(cmd)).FirstOrDefault();
            return code is null ? fallback : KeyCodes.Display(code);
        }
        ResetMenu.Text = Label("Reset", Gesture(SystemCommand.Reset, ""));
        HardResetMenu.Text = Label("Hard Reset (power cycle)", Gesture(SystemCommand.HardReset, ""));
        PauseMenu.Text = Label(_runner?.Paused == true ? "✓ Pause / Freeze" : "Pause / Freeze", Gesture(SystemCommand.Pause, ""));
        WarpMenu.Text = Label(_runner?.Warp == true ? "✓ Warp Speed" : "Warp Speed", Gesture(SystemCommand.Warp, "Alt+W"));
        ScreenshotMenu.Text = Label("Save Screenshot...", Gesture(SystemCommand.Screenshot, ""));
        MemoryViewerMenu.Text = Label("Memory Viewer / Editor...", Gesture(SystemCommand.MemoryViewer, "Alt+M"));
        SpriteViewerMenu.Text = Label("Sprite Viewer...", Gesture(SystemCommand.SpriteViewer, "Alt+S"));
        DriveMenu.Text = _session.Machine.Drive is not null ? "✓ 1541 Drive (device 8)" : "1541 Drive (device 8)";
    }

    private static string Label(string text, string gesture) => gesture.Length == 0 ? text : text + "    " + gesture;

    private void SetPaused(bool paused)
    {
        if (_runner is null) return;
        _runner.Paused = paused;
        UpdateMenuGestures();
        _memoryViewer.Page?.OnFreezeChanged();
        _spriteViewer.Page?.OnFreezeChanged();
    }

    private void SetWarp(bool warp)
    {
        if (_runner is null) return;
        _runner.Warp = warp;
        if (_frameTimer is not null)
        {
            // Ask for the picture less often while the machine races; see WarpFrameInterval.
            _frameTimer.Stop();
            _frameTimer.Interval = warp ? WarpFrameInterval : NormalFrameInterval;
            _frameTimer.Start();
        }
        UpdateMenuGestures();
    }

    private void DoReset(bool hard)
    {
        if (_runner is null || _session is null) return;
        var session = _session;
        _runner.Invoke(() => session.Reset(hard));
    }

    private async Task TrySaveBindingsAsync()
    {
        try
        {
            _session?.Bindings.Save(BindingsPath);
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Cannot save key bindings", ex.Message, "OK");
        }
    }

    #endregion

    #region Keyboard / joystick

    private void OnKeyDown(PhysicalKeyEvent e)
    {
        if (_session is null || _runner is null || _keyboard is null) return;
        if (e.IsRepeat) { e.Handled = true; return; }

        if (_keyboard.AltDown)
        {
            switch (e.Code)
            {
                case "KeyW": SetWarp(!_runner.Warp); e.Handled = true; return;
                case "KeyM": ShowMemoryViewer(); e.Handled = true; return;
                case "KeyS": ShowSpriteViewer(); e.Handled = true; return;
                case "F4": return;   // let the platform close the window
            }
        }
        switch (e.Code)
        {
            case "F9": _ = AttachDiskAsync(autostart: false); e.Handled = true; return;
            case "F10": _ = AttachTapeAsync(); e.Handled = true; return;
        }

        if (!_session.Bindings.TryGet(e.Code, out _)) return;
        var session = _session;
        var code = e.Code;
        _runner.Invoke(() => session.KeyDown(code));
        e.Handled = true;
    }

    private void OnKeyUp(PhysicalKeyEvent e)
    {
        if (_session is null || _runner is null) return;
        var session = _session;
        var code = e.Code;
        _runner.Invoke(() => session.KeyUp(code));
        if (session.Bindings.TryGet(code, out _))
            e.Handled = true;
    }

    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        if (_session is null || _runner is null) return;
        var session = _session;
        _runner.Invoke(session.ReleaseAllInput);
    }

    #endregion

    #region Menu handlers

    private void AttachDisk_Clicked(object? sender, EventArgs e) => _ = AttachDiskAsync(autostart: false);
    private void AttachDiskAutostart_Clicked(object? sender, EventArgs e) => _ = AttachDiskAsync(autostart: true);

    private async Task AttachDiskAsync(bool autostart)
    {
        if (_session is null || _runner is null) return;
        if (_session.Roms.Drive1541 is null)
        {
            await DisplayAlertAsync("Drive", "No 1541 ROM available, the drive cannot be enabled.", "OK");
            return;
        }
        var path = await FileDialogs.OpenAsync("Attach disk image", ".d64");
        if (path is null) return;
        try
        {
            var data = File.ReadAllBytes(path);
            var name = Path.GetFileName(path);
            var session = _session;
            _runner.Invoke(() => session.AttachDisk(data, name, autostart));
            _diskPath = path;
            MediaText.Text = session.MediaDescription;
            UpdateMenuGestures();
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Cannot attach disk", ex.Message, "OK");
        }
    }

    private void EjectDisk_Clicked(object? sender, EventArgs e)
    {
        if (_session is null || _runner is null) return;
        var session = _session;
        _runner.Invoke(session.EjectDisk);
        _diskPath = null;
        MediaText.Text = _romDescription;
    }

    private async void SaveDisk_Clicked(object? sender, EventArgs e)
    {
        if (_session?.Machine.Drive?.Disk.Disk is null || _runner is null) return;
        var path = await FileDialogs.SaveAsync("Disk image", ".d64", _diskPath is null ? "disk.d64" : Path.GetFileName(_diskPath));
        if (path is null) return;
        try
        {
            var session = _session;
            var d64 = _runner.Invoke(session.SaveDisk);
            d64?.Save(path);
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Cannot save disk", ex.Message, "OK");
        }
    }

    private void AttachTape_Clicked(object? sender, EventArgs e) => _ = AttachTapeAsync();

    private async Task AttachTapeAsync()
    {
        if (_session is null || _runner is null) return;
        var path = await FileDialogs.OpenAsync("Attach tape image / program", ".t64", ".prg");
        if (path is null) return;
        try
        {
            var data = File.ReadAllBytes(path);
            int index = 0;
            if (T64Image.IsT64(data))
            {
                var t64 = T64Image.Load(data);
                if (t64.Entries.Count == 0) throw new InvalidDataException("The tape image contains no files");
                if (t64.Entries.Count > 1)
                {
                    int? chosen = await SelectEntryPage.ShowAsync(this, t64);
                    if (chosen is null) return;
                    index = chosen.Value;
                }
            }
            var name = Path.GetFileName(path);
            var session = _session;
            _runner.Invoke(() => session.AttachProgram(data, name, index, run: true));
            MediaText.Text = session.MediaDescription;
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Cannot load program", ex.Message, "OK");
        }
    }

    private async void AttachCartridge_Clicked(object? sender, EventArgs e)
    {
        if (_session is null || _runner is null) return;
        var path = await FileDialogs.OpenAsync("Attach cartridge", ".crt", ".bin");
        if (path is null) return;
        try
        {
            var data = File.ReadAllBytes(path);
            var name = Path.GetFileNameWithoutExtension(path);
            var session = _session;
            _runner.Invoke(() => session.AttachCartridge(data, name));
            MediaText.Text = session.MediaDescription;
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Cannot attach cartridge", ex.Message, "OK");
        }
    }

    private void DetachCartridge_Clicked(object? sender, EventArgs e)
    {
        if (_session is null || _runner is null) return;
        var session = _session;
        _runner.Invoke(session.DetachCartridge);
        MediaText.Text = _romDescription;
    }

    private void Screenshot_Clicked(object? sender, EventArgs e) => _ = SaveScreenshotAsync();

    private async Task SaveScreenshotAsync()
    {
        if (_session is null || _runner is null) return;
        var path = await FileDialogs.SaveAsync("PNG image", ".png", $"c64-{DateTime.Now:yyyyMMdd-HHmmss}.png");
        if (path is null) return;
        try
        {
            var session = _session;
            int width = _videoSink?.Width ?? 384, height = _videoSink?.Height ?? 272;
            var pixels = new uint[width * height];
            _runner.Invoke(() => new VideoFrame(session.Machine.Vic.Frame, session.Machine.Frames).CopyVisible(pixels));
            PngWriter.Save(path, width, height, pixels);
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Cannot save screenshot", ex.Message, "OK");
        }
    }

    private void Exit_Clicked(object? sender, EventArgs e) => Application.Current?.Quit();

    private void Reset_Clicked(object? sender, EventArgs e) => DoReset(hard: false);
    private void HardReset_Clicked(object? sender, EventArgs e) => DoReset(hard: true);

    private void Pause_Clicked(object? sender, EventArgs e) => SetPaused(_runner?.Paused != true);

    private void Warp_Clicked(object? sender, EventArgs e) => SetWarp(_runner?.Warp != true);

    private void Drive_Clicked(object? sender, EventArgs e)
    {
        if (_session is null || _runner is null) return;
        bool enable = _session.Machine.Drive is null;
        var session = _session;
        _runner.Invoke(() =>
        {
            if (enable)
            {
                if (session.Machine.Drive is null) session.Machine.AttachDrive(8);
            }
            else
            {
                session.EjectDisk();
                session.Machine.DetachDrive();
            }
        });
        if (!enable) _diskPath = null;
        UpdateMenuGestures();
    }

    private async void SwapJoystickPorts_Clicked(object? sender, EventArgs e)
    {
        if (_session is null || _runner is null) return;
        var bindings = _session.Bindings;
        var swapped = new List<(string Code, InputAction Action)>();
        foreach (var (code, action) in bindings.All)
            if (action.Kind == InputActionKind.Joystick)
                swapped.Add((code, InputAction.ForJoystick(action.JoystickPort == 1 ? 2 : 1, action.Joystick)));
        var session = _session;
        _runner.Invoke(() =>
        {
            session.ReleaseAllInput();
            foreach (var (code, action) in swapped)
                bindings.Set(code, action);
        });
        await TrySaveBindingsAsync();
    }

    private void MemoryViewer_Clicked(object? sender, EventArgs e) => ShowMemoryViewer();

    private void ShowMemoryViewer()
    {
        if (_runner is null) return;
        var runner = _runner;
        _memoryViewer.Show(() => new MemoryViewerPage(runner, SetPaused), "Memory Viewer / Editor", 1000, 640);
    }

    private void SpriteViewer_Clicked(object? sender, EventArgs e) => ShowSpriteViewer();

    private void ShowSpriteViewer()
    {
        if (_runner is null) return;
        var runner = _runner;
        _spriteViewer.Show(() => new SpriteViewerPage(runner, SetPaused), "Sprite Viewer", 1200, 780);
    }

    private async void KeyBindings_Clicked(object? sender, EventArgs e)
    {
        if (_runner is null) return;
        await KeyBindingsPage.ShowAsync(this, _runner, BindingsPath);
        UpdateMenuGestures();
    }

    private void AudioVisualizer_Clicked(object? sender, EventArgs e)
    {
        if (_runner is null || _audioTap is null) return;
        var runner = _runner;
        var tap = _audioTap;
        _audioVisualizer.Show(() => new AudioVisualizerPage(runner, tap), "Audio Visualizer (SID)", 960, 680);
    }

    private void CharsetViewer_Clicked(object? sender, EventArgs e)
    {
        if (_runner is null) return;
        var runner = _runner;
        _charsetViewer.Show(() => new CharsetViewerPage(runner), "Character Set Viewer", 1320, 800);
    }

    private async void TypeText_Clicked(object? sender, EventArgs e)
    {
        if (_session is null || _runner is null) return;
        var text = await TypeTextPage.ShowAsync(this);
        if (text is null) return;
        var session = _session;
        _runner.Invoke(() => session.TypeText(text));
    }

    private async void KeyboardHelp_Clicked(object? sender, EventArgs e)
    {
        await DisplayAlertAsync("Keyboard",
            "Every PC key can be bound to a C64 key, a joystick input or a command with Tools > Key Bindings...\n" +
            "(the Joystick panel there picks the keys that make up joystick 1 and 2). Bindings are stored in\n" +
            BindingsPath + "\n\n" +
            "Default layout (positional where possible):\n" +
            "Escape = RUN/STOP, Tab = C=, Backspace = INST/DEL, Home/End = CLR/HOME, Page Up = RESTORE\n" +
            "Cursor keys = CRSR keys, F1-F8 = function keys, Ctrl = CTRL\n" +
            "[ = @, ] = *, ` = <-, \\ = £\n\n" +
            "Joystick (port 2): numeric keypad 8/2/4/6, 0 / 5 / Right Alt = fire. Machine > Swap Joystick Ports moves it to port 1.\n\n" +
            "Bound commands (default): F11 reset, F12 screenshot, Pause = freeze.\n" +
            "Window shortcuts: F9 attach disk, F10 attach program, Alt+W warp, Alt+M memory viewer, Alt+S sprite viewer.",
            "OK");
    }

    private async void About_Clicked(object? sender, EventArgs e)
    {
        await DisplayAlertAsync("About",
            "Tedd.CommodoreEmulator\nA cycle-exact Commodore 64 emulator in C#.\n\nhttps://github.com/tedd/Tedd.CommodoreEmulator", "OK");
    }

    #endregion
}
