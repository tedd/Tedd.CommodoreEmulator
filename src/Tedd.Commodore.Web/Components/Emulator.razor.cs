using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Tedd.Commodore.Emulator.C128;
using Tedd.Commodore.Emulator.C64;
using Tedd.Commodore.Emulator.Machines;
using Tedd.Commodore.Emulator.Tools;
using Tedd.Commodore.Hosting;
using Tedd.Commodore.Web.Demo;
using Tedd.Commodore.Web.Interop;
using Tedd.Commodore.Web.Services;

namespace Tedd.Commodore.Web.Components;

/// <summary>
/// The interactive emulator: canvas + audio + keyboard/touch input + media and ROM handling, for a Commodore 64
/// or a Commodore 128. The pieces it glues together are reusable on their own: <see cref="BrowserVideoSink"/>,
/// <see cref="BrowserAudioSink"/>, <see cref="BrowserInput"/>, <see cref="BrowserLoop"/> and <see cref="RomStore"/>.
/// </summary>
public partial class Emulator : ComponentBase, IDisposable
{
    private const string CanvasId = "emu-canvas";
    private const string ScreenId = "emu-screen";
    private const string RootId = "emu-root";
    private const string MachineKey = "tedd.commodore.machine";
    private const long MaxMediaSize = 8 * 1024 * 1024;
    private const long MaxRomSize = 64 * 1024;

    private enum EmuState { Booting, NeedRoms, Ready, Running, Failed }

    private static readonly (string Label, C64Key Key)[] SoftKeys =
    {
        ("RUN/STOP", C64Key.RunStop), ("RETURN", C64Key.Return), ("SPACE", C64Key.Space),
        ("F1", C64Key.F1), ("F3", C64Key.F3), ("F5", C64Key.F5), ("F7", C64Key.F7),
    };

    private static readonly (string Label, C64Key Key)[] SoftKeys128 =
    {
        ("ESC", C64Key.Escape), ("TAB", C64Key.Tab), ("HELP", C64Key.Help), ("ALT", C64Key.Alt),
    };

    /// <summary>The CP/M disks shipped with the site (disks/c128 in the repository).</summary>
    private static readonly (string Label, string File)[] CpmDisks =
    {
        ("CP/M 3.0 system (1985)", "cpm.system.622-580745.d64"),
        ("CP/M 3.0 system (1987)", "cpm.system.622-3282252.d64"),
        ("CP/M utilities (1985)", "cpm.utilities.d64"),
        ("CP/M utilities (1987)", "cpm.utilities3.d64"),
    };

    [Inject] private HttpClient Http { get; set; } = default!;
    [Inject] private NavigationManager Nav { get; set; } = default!;

    private readonly RomStore _roms = new();
    private readonly BrowserInput _input = new();
    private readonly Action<string> _onDroppedFile;
    private EmulatorSession? _session;
    private BrowserVideoSink? _video;
    private BrowserAudioSink? _audio;
    private EmuState _state = EmuState.Booting;
    private string _bootMessage = "";
    private string? _error;
    private string? _message;
    private string? _romMessage;
    private int _sampleRate;
    private double _fps;
    private double _load;
    private bool _basicReady;
    private string _audioState = "off";
    private bool _autostart = true;
    private bool _attachDrive = true;
    private bool _rememberRoms = true;
    private bool _showRoms;
    private bool _touchDevice;
    private bool _typeReturn = true;
    private string _typeText = "";
    private int _joyPort = 2;
    private bool _statusHooked;
    private MachineModel _model = MachineModel.C64;
    private bool _columns80;
    private DisplayOutput _display = DisplayOutput.Auto;

    public Emulator()
    {
        _onDroppedFile = OnDroppedFile;
    }

    private bool IsRunning => _state == EmuState.Running && _session is not null;
    private bool IsC128 => _model == MachineModel.C128;
    private bool RomsReadyForModel => IsC128 ? _roms.IsC128Complete : _roms.IsComplete;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender) return;
        try
        {
            _bootMessage = "loading scripts";
            await C64Js.ImportAsync(new Uri(new Uri(Nav.BaseUri), "js/c64.js").ToString());
            _video = new BrowserVideoSink(CanvasId);
            _touchDevice = C64Js.IsTouchDevice();
            C64Js.AttachDrop(RootId, _onDroppedFile);
            BrowserLoop.StatusTick += OnStatusTick;
            _statusHooked = true;
            if (C64Js.StorageGet(MachineKey) == "c128") _model = MachineModel.C128;
            _bootMessage = "looking for ROM images";
            StateHasChanged();
            await LoadRomsAsync();
        }
        catch (Exception ex)
        {
            _state = EmuState.Failed;
            _error = ex.ToString();
        }
        StateHasChanged();
    }

    private async Task LoadRomsAsync()
    {
        bool found = _roms.LoadFromStorage() || await _roms.LoadFromSiteAsync(Http);
        _attachDrive = _roms.HasDrive;
        if (IsC128 && !_roms.IsC128Complete) _model = MachineModel.C64;   // the remembered C128 choice needs its ROMs
        _state = found ? EmuState.Ready : EmuState.NeedRoms;
        _showRoms = !found;
    }

    #region session lifecycle

    private async Task StartAsync()
    {
        try
        {
            if (!RomsReadyForModel)
            {
                _state = EmuState.NeedRoms;
                _showRoms = true;
                return;
            }
            // Must happen in the click handler (browser autoplay policy); returns the real device rate.
            _sampleRate = await C64Js.AudioStart();
            _audioState = C64Js.AudioState();
            if (_session is null || _session.SampleRate != _sampleRate || _session.Model != _model)
                CreateSession();
            _state = EmuState.Running;
            _error = null;
            await BrowserLoop.StartAsync();
            C64Js.FocusElement(ScreenId);
        }
        catch (Exception ex)
        {
            _state = EmuState.Failed;
            _error = ex.ToString();
        }
    }

    /// <summary>(Re)creates the session from the current ROM set and machine choice and connects the browser surfaces.</summary>
    private void CreateSession()
    {
        if (_session is not null)
        {
            _session.Command -= OnCommand;
            _input.Detach();
        }
        bool drive = _attachDrive && _roms.HasDrive;
        var session = IsC128
            ? new EmulatorSession(_roms.ToC128RomSet(drive), _sampleRate, attachDrive: drive, columns80: _columns80)
            : new EmulatorSession(_roms.ToRomSet(drive), _sampleRate, attachDrive: drive);
        session.Display = _display;
        _audio = new BrowserAudioSink(_sampleRate);
        session.Audio = _audio;
        session.Video = _video!;
        session.Command += OnCommand;
        _session = session;
        _input.Session = session;
        _input.Attach(ScreenId);
        BrowserLoop.Attach(session, _video!);
    }

    private void OnCommand(SystemCommand command)
    {
        switch (command)
        {
            case SystemCommand.Reset: Reset(); break;
            case SystemCommand.HardReset: _session?.Reset(hard: true); break;
            case SystemCommand.Pause: _ = TogglePauseAsync(); break;
            case SystemCommand.Warp: ToggleWarp(); break;
            case SystemCommand.Screenshot: Screenshot(); break;
            case SystemCommand.ToggleColumns: _columns80 = !(_session?.Display40Columns ?? true); break;
        }
        _ = InvokeAsync(StateHasChanged);
    }

    private void OnStatusTick()
    {
        _fps = BrowserLoop.MeasuredFps;
        _load = BrowserLoop.Load;
        _audioState = C64Js.AudioState();
        if (_session is not null)
            _basicReady = _session.Machine.IsBasicReady();
        if (BrowserLoop.Error is { } error && _state == EmuState.Running)
        {
            _state = EmuState.Failed;
            _error = error.ToString();
        }
        _ = InvokeAsync(StateHasChanged);
    }

    public void Dispose()
    {
        if (_statusHooked) BrowserLoop.StatusTick -= OnStatusTick;
        if (BrowserLoop.Session == _session) BrowserLoop.Detach();
        if (BrowserLoop.IsRunning) BrowserLoop.Stop();
        _input.Detach();
        if (_session is not null) _session.Command -= OnCommand;
    }

    #endregion

    #region machine choice

    private void SelectMachine(MachineModel model)
    {
        if (model == _model) return;
        if (model == MachineModel.C128 && !_roms.IsC128Complete)
        {
            _message = "The C128 needs its own ROM images (BASIC low/high, KERNAL, 8K character ROM) plus the C64 BASIC/KERNAL: load them in the ROM panel.";
            _showRoms = true;
            return;
        }
        _model = model;
        C64Js.StorageSet(MachineKey, model == MachineModel.C128 ? "c128" : "c64");
        if (_state == EmuState.Running)
        {
            CreateSession();
            BrowserLoop.ResetPacing();
            _message = $"Switched to the Commodore {(IsC128 ? "128" : "64")}.";
            FocusScreen();
        }
        else if (_state is EmuState.Ready or EmuState.NeedRoms)
        {
            _state = RomsReadyForModel ? EmuState.Ready : EmuState.NeedRoms;
        }
    }

    /// <summary>Presses/releases the 40/80 DISPLAY key (the KERNAL looks at it on reset and on ESC X).</summary>
    private void ToggleColumns()
    {
        _columns80 = !_columns80;
        if (_session is not null) _session.Display40Columns = !_columns80;
        FocusScreen();
    }

    private void SetDisplay(DisplayOutput display)
    {
        _display = display;
        if (_session is not null) _session.Display = display;
        FocusScreen();
    }

    private string CpuLabel => _session?.Machine is C128 c
        ? (c.C64Mode ? "C64 mode" : c.Z80Active ? "Z80" : "8502") + (c.Vic.FastMode ? " 2 MHz" : "")
        : "6510";

    #endregion

    #region toolbar

    private void FocusScreen() => C64Js.FocusElement(ScreenId);

    private void Reset()
    {
        _session?.Reset(hard: false);
        _audio?.Clear();
    }

    private async Task TogglePauseAsync()
    {
        if (_session is null) return;
        _session.Paused = !_session.Paused;
        if (_session.Paused)
        {
            _audio?.Clear();
            await C64Js.AudioSuspend();
        }
        else
        {
            await C64Js.AudioResume();
            BrowserLoop.ResetPacing();
        }
        _audioState = C64Js.AudioState();
    }

    private void ToggleWarp()
    {
        if (_session is null) return;
        _session.Warp = !_session.Warp;
        if (!_session.Warp) BrowserLoop.ResetPacing();
    }

    private void Screenshot() => C64Js.DownloadCanvas(CanvasId, $"{(IsC128 ? "c128" : "c64")}-{DateTime.Now:yyyyMMdd-HHmmss}.png");

    private void Fullscreen() => C64Js.RequestFullscreen(ScreenId);

    private void RunDemo()
    {
        if (_session is null) return;
        if (IsC128)
        {
            _message = "The tech demo is written for the C64: switch the machine to Commodore 64 (or type GO64 on the C128) to run it.";
            return;
        }
        try
        {
            var prg = DemoProgram.BuildPrg();
            if (!_session.Machine.IsBasicReady())
                _session.Reset(hard: false);   // the injection waits for READY.
            _session.AttachProgram(prg, "tech-demo.prg");
            _message = $"Demo assembled ({prg.Length - 2} bytes at ${DemoProgram.LoadAddress:X4}); it starts as soon as BASIC is ready.";
            FocusScreen();
        }
        catch (AssemblerException ex)
        {
            _message = $"Assembler error in line {ex.Line}: {ex.Message}";
        }
    }

    /// <summary>Fetches one of the shipped CP/M disks, presses the 40/80 key for the 80 column screen and boots it.</summary>
    private async Task BootCpmAsync(string file)
    {
        if (_session is null || !IsC128) return;
        if (!_roms.HasDrive || !_attachDrive)
        {
            _message = "CP/M boots from the 1541: the drive ROM is needed and the drive must be enabled.";
            return;
        }
        try
        {
            using var response = await Http.GetAsync("disks/c128/" + file);
            response.EnsureSuccessStatusCode();
            var data = await response.Content.ReadAsByteArrayAsync();
            _columns80 = true;
            _session.Display40Columns = false;
            _session.AttachDisk(data, file, autostart: true, writeProtected: true);
            _session.Warp = true;   // the 1541 needs about two minutes of C128 time to load CPM+.SYS
            _message = $"Booting {file} on the 80 column screen (warp is on until you turn it off; the load takes about two minutes of emulated time).";
            FocusScreen();
        }
        catch (Exception ex)
        {
            _message = $"Could not load {file}: {ex.Message}";
        }
    }

    private void Eject()
    {
        if (_session is null) return;
        if (_session.MediaDescription.StartsWith("Cartridge", StringComparison.Ordinal))
            _session.DetachCartridge();
        else
            _session.EjectDisk();
        _message = null;
    }

    private void TypeText()
    {
        if (_session is null || string.IsNullOrEmpty(_typeText)) return;
        _session.TypeText(_typeText.Replace("\r\n", "\n") + (_typeReturn ? "\n" : ""));
        FocusScreen();
    }

    #endregion

    #region touch input

    private void Joy(JoystickInput input, bool pressed) => _session?.SetJoystick(_joyPort, input, pressed);

    private void Key(C64Key key, bool pressed) => _session?.SetKey(key, pressed);

    private void Restore(bool pressed) => _session?.Machine.Keyboard.SetRestore(pressed);

    #endregion

    #region media

    private async Task OnMediaSelected(InputFileChangeEventArgs e)
    {
        try
        {
            var data = await ReadAllAsync(e.File, MaxMediaSize);
            AttachMedia(e.File.Name, data);
        }
        catch (Exception ex)
        {
            _message = "Could not read the file: " + ex.Message;
        }
    }

    private void OnDroppedFile(string name)
    {
        var data = C64Js.TakeDroppedFile(name);
        if (data is null) return;
        if (RomStore.Classify(name, data.Length) is { } slot)
        {
            _romMessage = _roms.Accept(slot, name, data);
            _showRoms = true;
            if (_romMessage is null) _message = $"{name} stored as {slot} ROM - press \"Apply & reset\" to use it.";
        }
        else if (IsRunning)
        {
            AttachMedia(name, data);
        }
        else
        {
            _message = "Start the emulator before dropping media files on it.";
        }
        _ = InvokeAsync(StateHasChanged);
    }

    private void AttachMedia(string name, byte[] data)
    {
        if (_session is null) return;
        try
        {
            if (_autostart && !_session.Machine.IsBasicReady())
                _session.Reset(hard: false);   // autostart types into BASIC; make sure we get there
            _session.AttachAuto(data, name, _autostart);
            _message = "Attached " + _session.MediaDescription;
            FocusScreen();
        }
        catch (Exception ex)
        {
            _message = $"Could not attach {name}: {ex.Message}";
        }
    }

    #endregion

    #region roms

    private async Task OnRomSelected(InputFileChangeEventArgs e, RomSlot slot)
    {
        _romMessage = null;
        try
        {
            foreach (var file in e.GetMultipleFiles(4))
            {
                var data = await ReadAllAsync(file, MaxRomSize);
                var err = _roms.Accept(slot, file.Name, data);
                if (err is not null) { _romMessage = err; break; }
            }
            if (_roms.HasDrive) _attachDrive = true;
            if (RomsReadyForModel && _state == EmuState.NeedRoms) _state = EmuState.Ready;
            if (_roms.IsComplete && _rememberRoms && !_roms.SaveToStorage())
                _romMessage = "The ROMs are loaded but could not be stored (localStorage unavailable or full).";
        }
        catch (Exception ex)
        {
            _romMessage = ex.Message;
        }
    }

    private async Task ApplyRomsAsync()
    {
        if (!RomsReadyForModel) return;
        _romMessage = null;
        if (_rememberRoms && !_roms.SaveToStorage())
            _romMessage = "Applied, but the ROMs could not be stored in localStorage.";
        if (_state == EmuState.Running)
        {
            CreateSession();
            BrowserLoop.ResetPacing();
        }
        else
        {
            _state = EmuState.Ready;
            if (_session is not null) { _session.Command -= OnCommand; _session = null; }
        }
        await Task.CompletedTask;
    }

    private async Task ForgetRomsAsync()
    {
        RomStore.ClearStorage();
        _romMessage = "Stored ROMs removed; reloading the site's ROM set.";
        var fresh = new RomStore();
        if (await fresh.LoadFromSiteAsync(Http))
        {
            foreach (var slot in new[] { RomSlot.Basic, RomSlot.Kernal, RomSlot.Chargen })
                _roms.Accept(slot, slot switch { RomSlot.Basic => fresh.BasicName, RomSlot.Kernal => fresh.KernalName, _ => fresh.ChargenName },
                    slot switch { RomSlot.Basic => fresh.Basic!, RomSlot.Kernal => fresh.Kernal!, _ => fresh.Chargen! });
            if (fresh.Drive is not null) _roms.Accept(RomSlot.Drive, fresh.DriveName, fresh.Drive); else _roms.RemoveDrive();
            _attachDrive = _roms.HasDrive;
        }
        else
        {
            _romMessage = "Stored ROMs removed; the site has no ROM images, load your own.";
        }
        if (IsC128 && !_roms.IsC128Complete) SelectMachine(MachineModel.C64);
    }

    private static async Task<byte[]> ReadAllAsync(IBrowserFile file, long maxSize)
    {
        await using var stream = file.OpenReadStream(maxSize);
        using var ms = new MemoryStream((int)Math.Min(file.Size, int.MaxValue));
        await stream.CopyToAsync(ms);
        return ms.ToArray();
    }

    #endregion
}
