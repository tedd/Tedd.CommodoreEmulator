using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Tedd.Commodore.Emulator.C128;
using Tedd.Commodore.Emulator.C64;
using Tedd.Commodore.Emulator.Machines;
using Tedd.Commodore.Emulator.Media;
using Tedd.Commodore.Emulator.Tools;
using Tedd.Commodore.Hosting;
using TeddBitmap = Tedd.WriteableBitmap;

namespace Tedd.Commodore.GUI;

/// <summary>
/// The emulator window. The machine (a C64 or a C128) lives in an <see cref="EmulatorSession"/> driven by an
/// <see cref="EmulatorRunner"/> thread; everything that touches it from the UI thread goes through
/// <see cref="EmulatorRunner.Invoke(Action)"/>. Key events are translated to W3C codes and fed to the session,
/// which applies the user's <see cref="KeyBindings"/>.
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>Where the key bindings are persisted (%AppData%\Tedd.Commodore\keybindings.json).</summary>
    public static readonly string BindingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Tedd.Commodore", "keybindings.json");

    /// <summary>Where the chosen machine is remembered (%AppData%\Tedd.Commodore\machine.txt: "c64" or "c128").</summary>
    public static readonly string MachinePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Tedd.Commodore", "machine.txt");

    private EmulatorSession? _session;
    private EmulatorRunner? _runner;
    private WpfVideoSink? _videoSink;
    private AudioOutput? _audioOutput;
    private AudioTap? _audioTap;
    private TeddBitmap? _bitmap;
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private MemoryViewerWindow? _memoryViewer;
    private SpriteViewerWindow? _spriteViewer;
    private AudioVisualizerWindow? _audioVisualizer;
    private CharsetViewerWindow? _charsetViewer;
    private string? _diskPath;
    private string _romDescription = "";
    private MachineModel _model = MachineModel.C64;

    public MainWindow()
    {
        InitializeComponent();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _model = LoadMachineChoice();
        if (!StartMachine(_model))
        {
            // Fall back to whatever ROM set is available.
            var other = _model == MachineModel.C64 ? MachineModel.C128 : MachineModel.C64;
            if (!StartMachine(other))
            {
                MessageBox.Show(this,
                    "No ROM images found.\n\n" + RomHelpText,
                    "ROMs missing", MessageBoxButton.OK, MessageBoxImage.Error);
                Close();
                return;
            }
        }
        Focus();
    }

    private const string RomHelpText =
        "Commodore 64: put basic.901226-01.bin (or 64c.251913-01.bin), a KERNAL and a character ROM into a 'roms' folder next to the executable, " +
        "or point the C64_ROMS environment variable at them.\n\n" +
        "Commodore 128: additionally basic-4000.318018-04.bin + basic-8000.318019-04.bin (or basic.318022-02.bin), kernal.318020-05.bin " +
        "(or complete.318023-02.bin, which also holds the C64 mode ROMs), and the 8K characters.390059-01.bin; the C128_ROMS variable is checked first.\n\n" +
        "Optional 1541 ROM: 1541-II.251968-03.bin or the 1541-c000/1541-e000 pair. The images are at " +
        "https://www.zimmers.net/anonftp/pub/cbm/firmware/computers/ and are not distributed with the emulator.";

    #region Machine lifecycle

    private static MachineModel LoadMachineChoice()
    {
        try
        {
            if (File.Exists(MachinePath) && File.ReadAllText(MachinePath).Trim().Equals("c128", StringComparison.OrdinalIgnoreCase))
                return MachineModel.C128;
        }
        catch (Exception)
        {
            // defaults
        }
        return MachineModel.C64;
    }

    private static void SaveMachineChoice(MachineModel model)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(MachinePath)!);
            File.WriteAllText(MachinePath, model == MachineModel.C128 ? "c128" : "c64");
        }
        catch (Exception)
        {
            // not fatal
        }
    }

    /// <summary>Creates the session for <paramref name="model"/> and starts the emulation thread. Returns false when its ROMs are missing.</summary>
    private bool StartMachine(MachineModel model)
    {
        EmulatorSession session;
        var bindings = File.Exists(BindingsPath) ? KeyBindings.LoadOrDefault(BindingsPath) : KeyBindings.CreateDefault(model);
        if (model == MachineModel.C128)
        {
            var roms = C128RomSet.TryLoadDefault();
            if (roms is null) return false;
            session = new EmulatorSession(roms, sampleRate: 44100, attachDrive: true, bindings: bindings);
        }
        else
        {
            var roms = RomSet.TryLoadDefault();
            if (roms is null) return false;
            session = new EmulatorSession(roms, sampleRate: 44100, attachDrive: true, bindings: bindings);
        }

        StopMachine();
        _model = model;
        _session = session;

        _videoSink = new WpfVideoSink();
        session.Video = _videoSink;
        AdoptBitmap();

        try
        {
            _audioOutput = new AudioOutput(session.SampleRate);
        }
        catch (Exception ex)
        {
            Debug.WriteLine("Audio disabled: " + ex.Message);
        }
        _audioTap = new AudioTap(_audioOutput, session.SampleRate);
        session.Audio = _audioTap;

        session.Command += OnSessionCommand;
        session.Bindings.Changed += OnBindingsChanged;

        _runner = new EmulatorRunner(session);
        DriveMenu.IsEnabled = session.HasDriveRom;
        DriveMenu.IsChecked = session.Machine.Drive is not null;
        _romDescription = session.RomDescription;
        MediaText.Text = _romDescription;
        _diskPath = null;
        Title = model == MachineModel.C128 ? "Tedd.CommodoreEmulator - Commodore 128" : "Tedd.CommodoreEmulator - Commodore 64";
        UpdateMachineMenus();
        UpdateMenuGestures();

        CompositionTarget.Rendering += OnRendering;
        _statusTimer.Tick += StatusTimer_Tick;
        _statusTimer.Start();
        _runner.Start();
        return true;
    }

    private void StopMachine()
    {
        CompositionTarget.Rendering -= OnRendering;
        _statusTimer.Stop();
        _statusTimer.Tick -= StatusTimer_Tick;
        _memoryViewer?.Close();
        _spriteViewer?.Close();
        _audioVisualizer?.Close();
        _charsetViewer?.Close();
        if (_session is not null)
        {
            _session.Command -= OnSessionCommand;
            _session.Bindings.Changed -= OnBindingsChanged;
        }
        _runner?.Dispose();
        _runner = null;
        // After the runner: the emulator thread writes into the sink's bitmap until it stops.
        Screen.Source = null;
        _bitmap = null;
        _videoSink?.Dispose();
        _videoSink = null;
        _audioOutput?.Dispose();
        _audioOutput = null;
        _session = null;
    }

    private void StatusTimer_Tick(object? sender, EventArgs e) => UpdateStatus();

    /// <summary>Creates the bitmap for the sink's current picture size and shows it.</summary>
    private void AdoptBitmap()
    {
        if (_videoSink is null) return;
        _bitmap = _videoSink.CreateBitmap();
        Screen.Source = _bitmap.BitmapSource;
        Screen.Width = _videoSink.Width;
        Screen.Height = _videoSink.Height;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (_bitmap is null || _videoSink is null) return;
        if (_videoSink.SizeChanged)
            AdoptBitmap();   // the C128 switched between its 40 and 80 column pictures
        _videoSink.Present();
    }

    private void UpdateStatus()
    {
        if (_runner is null || _session is null) return;
        FpsText.Text = $"{_runner.MeasuredFps:0.0} fps";
        StateText.Text = _runner.Paused ? "Frozen" : _runner.Warp ? "Warp" : "Running";
        var drive = _session.Machine.Drive;
        if (drive is null)
        {
            DriveLed.Fill = Brushes.DimGray;
            DriveText.Text = "No drive";
        }
        else
        {
            DriveLed.Fill = drive.Led ? Brushes.Red : Brushes.DarkRed;
            string disk = drive.Disk.Disk is null ? "no disk" : Path.GetFileName(_diskPath ?? "disk");
            DriveText.Text = $"8: {disk}  T{drive.Disk.Track:0.#}{(drive.MotorOn ? " *" : "")}";
        }
        MachineText.Text = _session.Machine is C128 c128
            ? $"C128 {(c128.C64Mode ? "(64 mode)" : c128.Z80Active ? "Z80" : "8502")}{(c128.Vic.FastMode ? " 2MHz" : "")} {(_session.ShowingVdc ? "80" : "40")} col"
            : "C64";
        string media = _session.MediaDescription;
        MediaText.Text = media.Length == 0 ? _romDescription : media;
    }

    private void UpdateMachineMenus()
    {
        bool c128 = _model == MachineModel.C128;
        C64Menu.IsChecked = !c128;
        C128Menu.IsChecked = c128;
        C128Separator.Visibility = ColumnsMenu.Visibility = CapsLockMenu.Visibility = DisplayMenu.Visibility = c128 ? Visibility.Visible : Visibility.Collapsed;
        if (_session is not null)
        {
            ColumnsMenu.IsChecked = !_session.Display40Columns;
            CapsLockMenu.IsChecked = _session.CapsLock;
            DisplayAutoMenu.IsChecked = _session.Display == DisplayOutput.Auto;
            DisplayVicMenu.IsChecked = _session.Display == DisplayOutput.VicII;
            DisplayVdcMenu.IsChecked = _session.Display == DisplayOutput.Vdc;
        }
    }

    #endregion

    #region Session commands and bindings

    /// <summary>Raised by the session when a key bound to a host command is pressed (may be on any thread).</summary>
    private void OnSessionCommand(SystemCommand command) => Dispatcher.InvokeAsync(() => HandleCommand(command));

    private void HandleCommand(SystemCommand command)
    {
        if (_runner is null) return;
        switch (command)
        {
            case SystemCommand.Reset: DoReset(hard: false); break;
            case SystemCommand.HardReset: DoReset(hard: true); break;
            case SystemCommand.Pause: SetPaused(!_runner.Paused); break;
            case SystemCommand.Warp: SetWarp(!_runner.Warp); break;
            case SystemCommand.Screenshot: SaveScreenshot(); break;
            case SystemCommand.MemoryViewer: ShowMemoryViewer(); break;
            case SystemCommand.SpriteViewer: ShowSpriteViewer(); break;
            case SystemCommand.ToggleColumns:
            case SystemCommand.CapsLock:
                UpdateMachineMenus();   // the session already flipped the key
                break;
        }
    }

    private void OnBindingsChanged()
    {
        if (Dispatcher.CheckAccess()) UpdateMenuGestures();
        else Dispatcher.InvokeAsync(UpdateMenuGestures);
    }

    /// <summary>Shows the key bound to each host command next to its menu item.</summary>
    private void UpdateMenuGestures()
    {
        if (_session is null) return;
        var b = _session.Bindings;
        string Gesture(SystemCommand cmd, string fallback)
        {
            var code = b.CodesFor(InputAction.ForSystem(cmd)).FirstOrDefault();
            return code is null ? fallback : KeyCodes.Display(code);
        }
        ResetMenu.InputGestureText = Gesture(SystemCommand.Reset, "");
        HardResetMenu.InputGestureText = Gesture(SystemCommand.HardReset, "");
        PauseMenu.InputGestureText = Gesture(SystemCommand.Pause, "");
        WarpMenu.InputGestureText = Gesture(SystemCommand.Warp, "Alt+W");
        ScreenshotMenu.InputGestureText = Gesture(SystemCommand.Screenshot, "");
        MemoryViewerMenu.InputGestureText = Gesture(SystemCommand.MemoryViewer, "Alt+M");
        SpriteViewerMenu.InputGestureText = Gesture(SystemCommand.SpriteViewer, "Alt+S");
        ColumnsMenu.InputGestureText = Gesture(SystemCommand.ToggleColumns, "");
        CapsLockMenu.InputGestureText = Gesture(SystemCommand.CapsLock, "");
    }

    private void SetPaused(bool paused)
    {
        if (_runner is null) return;
        _runner.Paused = paused;
        PauseMenu.IsChecked = paused;
        _memoryViewer?.OnFreezeChanged();
        _spriteViewer?.OnFreezeChanged();
    }

    private void SetWarp(bool warp)
    {
        if (_runner is null) return;
        _runner.Warp = warp;
        WarpMenu.IsChecked = warp;
    }

    private void DoReset(bool hard)
    {
        if (_runner is null || _session is null) return;
        var session = _session;
        _runner.Invoke(() => session.Reset(hard));
    }

    private void TrySaveBindings()
    {
        try
        {
            _session?.Bindings.Save(BindingsPath);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Cannot save key bindings", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    #endregion

    #region Keyboard / joystick

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (_session is null || _runner is null) return;
        var key = WpfKeyCodes.ResolveKey(e);
        if (e.IsRepeat) { e.Handled = true; return; }

        bool alt = e.KeyboardDevice.Modifiers.HasFlag(ModifierKeys.Alt);
        if (alt)
        {
            switch (key)
            {
                case Key.W: SetWarp(!_runner.Warp); e.Handled = true; return;
                case Key.M: ShowMemoryViewer(); e.Handled = true; return;
                case Key.S: ShowSpriteViewer(); e.Handled = true; return;
                case Key.F4: return; // let WPF close the window
            }
        }
        switch (key)
        {
            case Key.F9: AttachDisk(autostart: false); e.Handled = true; return;
            case Key.F10: AttachTape(); e.Handled = true; return;
        }

        if (!WpfKeyCodes.TryGetCode(key, out var code)) return;
        if (!_session.Bindings.TryGet(code, out _)) return;
        var session = _session;
        _runner.Invoke(() => session.KeyDown(code));
        e.Handled = true;
    }

    private void Window_KeyUp(object sender, KeyEventArgs e)
    {
        if (_session is null || _runner is null) return;
        var key = WpfKeyCodes.ResolveKey(e);
        if (!WpfKeyCodes.TryGetCode(key, out var code)) return;
        var session = _session;
        _runner.Invoke(() => session.KeyUp(code));
        if (session.Bindings.TryGet(code, out _))
            e.Handled = true;
    }

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        if (_session is null || _runner is null) return;
        var session = _session;
        _runner.Invoke(session.ReleaseAllInput);
    }

    #endregion

    #region Menu handlers

    private void SelectC64_Click(object sender, RoutedEventArgs e) => SwitchMachine(MachineModel.C64);
    private void SelectC128_Click(object sender, RoutedEventArgs e) => SwitchMachine(MachineModel.C128);

    private void SwitchMachine(MachineModel model)
    {
        if (model == _model)
        {
            UpdateMachineMenus();
            return;
        }
        if (!StartMachine(model))
        {
            MessageBox.Show(this, $"The {(model == MachineModel.C128 ? "C128" : "C64")} ROM images were not found.\n\n" + RomHelpText,
                "ROMs missing", MessageBoxButton.OK, MessageBoxImage.Warning);
            UpdateMachineMenus();
            return;
        }
        SaveMachineChoice(model);
        Focus();
    }

    private void Columns_Click(object sender, RoutedEventArgs e)
    {
        if (_session is null || _runner is null) return;
        var session = _session;
        bool down = ColumnsMenu.IsChecked;
        _runner.Invoke(() => session.Display40Columns = !down);
    }

    private void CapsLock_Click(object sender, RoutedEventArgs e)
    {
        if (_session is null || _runner is null) return;
        var session = _session;
        bool down = CapsLockMenu.IsChecked;
        _runner.Invoke(() => session.CapsLock = down);
    }

    private void DisplayAuto_Click(object sender, RoutedEventArgs e) => SetDisplay(DisplayOutput.Auto);
    private void DisplayVic_Click(object sender, RoutedEventArgs e) => SetDisplay(DisplayOutput.VicII);
    private void DisplayVdc_Click(object sender, RoutedEventArgs e) => SetDisplay(DisplayOutput.Vdc);

    private void SetDisplay(DisplayOutput display)
    {
        if (_session is null || _runner is null) return;
        var session = _session;
        _runner.Invoke(() => session.Display = display);
        UpdateMachineMenus();
    }

    private void AttachDisk_Click(object sender, RoutedEventArgs e) => AttachDisk(autostart: false);
    private void AttachDiskAutostart_Click(object sender, RoutedEventArgs e) => AttachDisk(autostart: true);

    private void AttachDisk(bool autostart)
    {
        if (_session is null || _runner is null) return;
        if (!_session.HasDriveRom)
        {
            MessageBox.Show(this, "No 1541 ROM available, the drive cannot be enabled.", "Drive", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var dlg = new OpenFileDialog { Filter = "Disk images (*.d64)|*.d64|All files|*.*", Title = "Attach disk image" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var data = File.ReadAllBytes(dlg.FileName);
            var name = Path.GetFileName(dlg.FileName);
            var session = _session;
            _runner.Invoke(() => session.AttachDisk(data, name, autostart));
            _diskPath = dlg.FileName;
            DriveMenu.IsChecked = session.Machine.Drive is not null;
            MediaText.Text = session.MediaDescription;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Cannot attach disk", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void EjectDisk_Click(object sender, RoutedEventArgs e)
    {
        if (_session is null || _runner is null) return;
        var session = _session;
        _runner.Invoke(session.EjectDisk);
        _diskPath = null;
        MediaText.Text = _romDescription;
    }

    private void SaveDisk_Click(object sender, RoutedEventArgs e)
    {
        if (_session?.Machine.Drive?.Disk.Disk is null || _runner is null) return;
        var dlg = new SaveFileDialog { Filter = "Disk images (*.d64)|*.d64", FileName = _diskPath is null ? "disk.d64" : Path.GetFileName(_diskPath) };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var session = _session;
            var d64 = _runner.Invoke(session.SaveDisk);
            d64?.Save(dlg.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Cannot save disk", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void AttachTape_Click(object sender, RoutedEventArgs e) => AttachTape();

    private void AttachTape()
    {
        if (_session is null || _runner is null) return;
        var dlg = new OpenFileDialog { Filter = "Tape images and programs (*.t64;*.prg)|*.t64;*.prg|All files|*.*", Title = "Attach tape image / program" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var data = File.ReadAllBytes(dlg.FileName);
            int index = 0;
            if (T64Image.IsT64(data))
            {
                var t64 = T64Image.Load(data);
                if (t64.Entries.Count == 0) throw new InvalidDataException("The tape image contains no files");
                if (t64.Entries.Count > 1)
                {
                    var chooser = new SelectEntryWindow(t64) { Owner = this };
                    if (chooser.ShowDialog() != true) return;
                    index = chooser.SelectedIndex;
                }
            }
            var name = Path.GetFileName(dlg.FileName);
            var session = _session;
            _runner.Invoke(() => session.AttachProgram(data, name, index, run: true));
            MediaText.Text = session.MediaDescription;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Cannot load program", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void AttachCartridge_Click(object sender, RoutedEventArgs e)
    {
        if (_session is null || _runner is null) return;
        var dlg = new OpenFileDialog { Filter = "Cartridge images (*.crt;*.bin)|*.crt;*.bin|All files|*.*", Title = "Attach cartridge" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var data = File.ReadAllBytes(dlg.FileName);
            var name = Path.GetFileNameWithoutExtension(dlg.FileName);
            var session = _session;
            _runner.Invoke(() => session.AttachCartridge(data, name));
            MediaText.Text = session.MediaDescription;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Cannot attach cartridge", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void DetachCartridge_Click(object sender, RoutedEventArgs e)
    {
        if (_session is null || _runner is null) return;
        var session = _session;
        _runner.Invoke(session.DetachCartridge);
        MediaText.Text = _romDescription;
    }

    private void Screenshot_Click(object sender, RoutedEventArgs e) => SaveScreenshot();

    private void SaveScreenshot()
    {
        if (_session is null || _runner is null) return;
        string prefix = _model == MachineModel.C128 ? "c128" : "c64";
        var dlg = new SaveFileDialog { Filter = "PNG image (*.png)|*.png", FileName = $"{prefix}-{DateTime.Now:yyyyMMdd-HHmmss}.png" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var session = _session;
            int width = 0, height = 0;
            uint[] pixels = Array.Empty<uint>();
            _runner.Invoke(() =>
            {
                var frame = session.CurrentFrame;
                width = frame.Width;
                height = frame.Height;
                pixels = new uint[width * height];
                frame.CopyVisible(pixels);
            });
            PngWriter.Save(dlg.FileName, width, height, pixels);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Cannot save screenshot", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private void Reset_Click(object sender, RoutedEventArgs e) => DoReset(hard: false);
    private void HardReset_Click(object sender, RoutedEventArgs e) => DoReset(hard: true);

    private void Pause_Click(object sender, RoutedEventArgs e) => SetPaused(PauseMenu.IsChecked);

    private void Warp_Click(object sender, RoutedEventArgs e) => SetWarp(WarpMenu.IsChecked);

    private void Drive_Click(object sender, RoutedEventArgs e)
    {
        if (_session is null || _runner is null) return;
        bool enable = DriveMenu.IsChecked;
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
    }

    private void SwapJoystickPorts_Click(object sender, RoutedEventArgs e)
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
        TrySaveBindings();
    }

    private void MemoryViewer_Click(object sender, RoutedEventArgs e) => ShowMemoryViewer();

    private void ShowMemoryViewer()
    {
        if (_runner is null) return;
        if (_memoryViewer is null || !_memoryViewer.IsLoaded)
        {
            _memoryViewer = new MemoryViewerWindow(_runner, SetPaused) { Owner = this };
            _memoryViewer.Closed += (_, _) => _memoryViewer = null;
        }
        _memoryViewer.Show();
        _memoryViewer.Activate();
    }

    private void SpriteViewer_Click(object sender, RoutedEventArgs e) => ShowSpriteViewer();

    private void ShowSpriteViewer()
    {
        if (_runner is null) return;
        if (_spriteViewer is null || !_spriteViewer.IsLoaded)
        {
            _spriteViewer = new SpriteViewerWindow(_runner, SetPaused) { Owner = this };
            _spriteViewer.Closed += (_, _) => _spriteViewer = null;
        }
        _spriteViewer.Show();
        _spriteViewer.Activate();
    }

    private void KeyBindings_Click(object sender, RoutedEventArgs e)
    {
        if (_runner is null) return;
        var editor = new KeyBindingsWindow(_runner, BindingsPath) { Owner = this };
        editor.ShowDialog();
        UpdateMenuGestures();
        Focus();
    }

    private void AudioVisualizer_Click(object sender, RoutedEventArgs e)
    {
        if (_runner is null || _audioTap is null) return;
        if (_audioVisualizer is null || !_audioVisualizer.IsLoaded)
        {
            _audioVisualizer = new AudioVisualizerWindow(_runner, _audioTap) { Owner = this };
            _audioVisualizer.Closed += (_, _) => _audioVisualizer = null;
        }
        _audioVisualizer.Show();
        _audioVisualizer.Activate();
    }

    private void CharsetViewer_Click(object sender, RoutedEventArgs e)
    {
        if (_runner is null) return;
        if (_charsetViewer is null || !_charsetViewer.IsLoaded)
        {
            _charsetViewer = new CharsetViewerWindow(_runner) { Owner = this };
            _charsetViewer.Closed += (_, _) => _charsetViewer = null;
        }
        _charsetViewer.Show();
        _charsetViewer.Activate();
    }

    private void TypeText_Click(object sender, RoutedEventArgs e)
    {
        if (_session is null || _runner is null) return;
        var w = new TypeTextWindow { Owner = this };
        if (w.ShowDialog() == true)
        {
            var session = _session;
            var text = w.Text;
            _runner.Invoke(() => session.TypeText(text));
        }
    }

    private void KeyboardHelp_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(this,
            "Every PC key can be bound to a C64/C128 key, a joystick input or a command with Tools > Key Bindings...\n" +
            "(the Joystick panel there picks the keys that make up joystick 1 and 2). Bindings are stored in\n" +
            BindingsPath + "\n\n" +
            "Default layout (positional where possible):\n" +
            "Escape = RUN/STOP, Tab = C=, Backspace = INST/DEL, Home/End = CLR/HOME, Page Up = RESTORE\n" +
            "Cursor keys = CRSR keys, F1-F8 = function keys, Ctrl = CTRL\n" +
            "[ = @, ] = *, ` = <-, \\ = £\n" +
            "C128 keys: Numpad 1/3/7/9 = keypad, Page Down = HELP, Scroll Lock = NO SCROLL, Left Alt = ALT, Caps Lock = CAPS LOCK.\n" +
            "When the bindings file does not exist yet, a C128 gets a layout of its own: Escape = ESC, Tab = TAB, End = RUN/STOP,\n" +
            "Right Ctrl = C=, the whole numeric keypad = keypad, cursor keys = the C128 cursor keys, F9 = 40/80 DISPLAY.\n\n" +
            "Joystick (port 2): numeric keypad 8/2/4/6, 0 / 5 / Right Alt = fire. Machine > Swap Joystick Ports moves it to port 1.\n\n" +
            "Bound commands (default): F11 reset, F12 screenshot, Pause = freeze.\n" +
            "Window shortcuts: F9 attach disk, F10 attach program, Alt+W warp, Alt+M memory viewer, Alt+S sprite viewer.",
            "Keyboard", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void RomHelp_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(this, RomHelpText + "\n\nCurrent set: " + _romDescription, "ROM images", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void About_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(this, "Tedd.CommodoreEmulator\nA cycle-exact Commodore 64 and Commodore 128 emulator in C#.\n\nhttps://github.com/tedd/Tedd.CommodoreEmulator",
            "About", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    #endregion

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        StopMachine();
    }
}
