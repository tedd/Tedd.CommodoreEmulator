using System;
using System.Collections.Generic;
using System.IO;
using Tedd.Commodore.Emulator.C128;
using Tedd.Commodore.Emulator.C64;
using Tedd.Commodore.Emulator.Drive;
using Tedd.Commodore.Emulator.Machines;
using Tedd.Commodore.Emulator.Media;

namespace Tedd.Commodore.Hosting;

/// <summary>Which picture a session presents when the machine has two video chips (the C128).</summary>
public enum DisplayOutput
{
    /// <summary>Follow the 40/80 DISPLAY key: the VIC-II when it is up (40 columns), the VDC when it is down.</summary>
    Auto,
    /// <summary>Always the VIC-II (40 columns).</summary>
    VicII,
    /// <summary>Always the VDC (80 columns; C128 only, falls back to the VIC-II on a C64).</summary>
    Vdc,
}

/// <summary>
/// A running Commodore 64 or 128 with its input bindings and output surfaces, independent of any UI framework.
/// The session is single-threaded: call <see cref="RunFrame"/> from whatever loop the host has (a dedicated
/// thread via <see cref="EmulatorRunner"/>, a browser animation frame, a game engine update) and feed input
/// through <see cref="KeyDown"/>/<see cref="KeyUp"/> (W3C key codes) or the joystick/typing helpers.
/// </summary>
public sealed class EmulatorSession
{
    private readonly short[] _audioScratch = new short[8192];
    private readonly Dictionary<string, InputAction> _held = new(StringComparer.OrdinalIgnoreCase);
    private int _shiftHolders;
    private IVideoSink _video = NullVideoSink.Instance;
    private IAudioSink _audio;
    private bool _warp;

    /// <summary>A Commodore 64.</summary>
    public EmulatorSession(RomSet roms, int sampleRate = 44100, bool attachDrive = true, KeyBindings? bindings = null)
        : this(new C64(roms, sampleRate), roms, null, sampleRate, attachDrive, bindings)
    {
    }

    /// <summary>A Commodore 128 (<paramref name="columns80"/> presses the 40/80 DISPLAY key so it boots on the VDC).</summary>
    public EmulatorSession(C128RomSet roms, int sampleRate = 44100, bool attachDrive = true, KeyBindings? bindings = null, bool columns80 = false)
        : this(new C128(roms, sampleRate) { Display40Columns = !columns80 }, roms.ToC64RomSet(), roms, sampleRate, attachDrive, bindings)
    {
    }

    private EmulatorSession(CommodoreMachine machine, RomSet roms, C128RomSet? c128Roms, int sampleRate, bool attachDrive, KeyBindings? bindings)
    {
        Machine = machine;
        Roms = roms;
        C128Roms = c128Roms;
        SampleRate = sampleRate;
        _audio = new NullAudioSink(sampleRate);
        Bindings = bindings ?? KeyBindings.CreateDefault(machine.Model);
        if (attachDrive && HasDriveRom)
            Machine.AttachDrive(8);
        if (machine is C128 c128)
            c128.Reset(hard: true);
    }

    /// <summary>The machine model of this session.</summary>
    public MachineModel Model => Machine.Model;

    /// <summary>The C64 ROM set (for a C128 session: its C64 mode ROMs).</summary>
    public RomSet Roms { get; }
    /// <summary>The C128 ROM set, or null for a C64 session.</summary>
    public C128RomSet? C128Roms { get; }
    /// <summary>True when the ROM set includes a 1541 DOS, i.e. a drive can be attached.</summary>
    public bool HasDriveRom => Roms.Drive1541 is not null;
    /// <summary>Description of the ROM images in use.</summary>
    public string RomDescription => C128Roms?.Description ?? Roms.Description;

    /// <summary>The machine: a <see cref="C64"/> or a <see cref="C128"/>.</summary>
    public CommodoreMachine Machine { get; }
    /// <summary>The machine as a C64, or null.</summary>
    public C64? C64 => Machine as C64;
    /// <summary>The machine as a C128, or null.</summary>
    public C128? C128 => Machine as C128;

    public int SampleRate { get; }
    public KeyBindings Bindings { get; set; }

    /// <summary>Which picture to present on a C128 (ignored on a C64).</summary>
    public DisplayOutput Display { get; set; } = DisplayOutput.Auto;

    /// <summary>True when the frames currently presented come from the VDC.</summary>
    public bool ShowingVdc => Machine is C128 c128 && Display switch
    {
        DisplayOutput.Vdc => true,
        DisplayOutput.VicII => false,
        _ => !c128.Display40Columns,
    };

    /// <summary>The frame that <see cref="RunFrame"/> would present now (for screenshots).</summary>
    public VideoFrame CurrentFrame => ShowingVdc
        ? VideoFrame.ForVdc(((C128)Machine).Vdc.Frame, Machine.Frames)
        : new VideoFrame(Machine.Vic.Frame, Machine.Frames);

    /// <summary>Where frames go. Replace at any time (from the emulation thread or while paused).</summary>
    public IVideoSink Video
    {
        get => _video;
        set => _video = value ?? NullVideoSink.Instance;
    }

    /// <summary>Where audio goes. The sink's sample rate must match <see cref="SampleRate"/>.</summary>
    public IAudioSink Audio
    {
        get => _audio;
        set
        {
            var sink = value ?? new NullAudioSink(SampleRate);
            if (sink.SampleRate != SampleRate)
                throw new ArgumentException($"Audio sink sample rate {sink.SampleRate} does not match the session's {SampleRate}");
            _audio = sink;
        }
    }

    /// <summary>When true, <see cref="RunFrame"/> does nothing (the host decides how to idle).</summary>
    public bool Paused { get; set; }

    /// <summary>Warp: the host runs frames as fast as it can; audio is dropped.</summary>
    public bool Warp
    {
        get => _warp;
        set
        {
            if (_warp == value) return;
            _warp = value;
            if (value) _audio.Clear();
        }
    }

    /// <summary>Raised when a bound key requests a host command (reset, screenshot, pause...). The 40/80 and CAPS LOCK toggles are applied by the session first.</summary>
    public event Action<SystemCommand>? Command;

    /// <summary>Human readable description of the currently attached media (for status bars).</summary>
    public string MediaDescription { get; private set; } = "";

    #region Running

    /// <summary>Runs one PAL frame (unless paused), presents it and pushes the produced audio.</summary>
    public void RunFrame()
    {
        if (Paused) return;
        Machine.RunFrame();
        _video.PresentFrame(CurrentFrame);
        int n = Machine.Audio.Read(_audioScratch);
        if (_warp)
            _audio.Clear();
        else if (n > 0)
            _audio.Write(_audioScratch.AsSpan(0, n));
    }

    /// <summary>Executes one system cycle (for debuggers; does not present anything).</summary>
    public void StepCycle() => Machine.Clock();

    /// <summary>Executes one CPU instruction (for debuggers).</summary>
    public void StepInstruction() => Machine.StepInstruction();

    public void Reset(bool hard = false)
    {
        Machine.Reset(hard);
        ReleaseAllInput();
    }

    #endregion

    #region Input

    /// <summary>A physical key went down. <paramref name="code"/> is a W3C KeyboardEvent.code name.</summary>
    public void KeyDown(string code)
    {
        if (_held.ContainsKey(code)) return;
        if (!Bindings.TryGet(code, out var action)) return;
        _held[code] = action;
        Apply(action, true);
    }

    /// <summary>A physical key went up.</summary>
    public void KeyUp(string code)
    {
        if (!_held.Remove(code, out var action)) return;
        Apply(action, false);
    }

    /// <summary>Directly drives a joystick input (for touch controls / game pads).</summary>
    public void SetJoystick(int port, JoystickInput input, bool pressed)
    {
        var j = port == 1 ? Machine.Joystick1 : Machine.Joystick2;
        switch (input)
        {
            case JoystickInput.Up: j.Up = pressed; break;
            case JoystickInput.Down: j.Down = pressed; break;
            case JoystickInput.Left: j.Left = pressed; break;
            case JoystickInput.Right: j.Right = pressed; break;
            case JoystickInput.Fire: j.Fire = pressed; break;
        }
    }

    /// <summary>Presses or releases a key directly (on-screen keyboards).</summary>
    public void SetKey(C64Key key, bool pressed)
    {
        if (pressed) Machine.Keyboard.Press(key); else Machine.Keyboard.Release(key);
    }

    /// <summary>Releases every key and joystick input (focus lost).</summary>
    public void ReleaseAllInput()
    {
        _held.Clear();
        _shiftHolders = 0;
        Machine.Keyboard.ReleaseAll();
        Machine.Joystick1.Clear();
        Machine.Joystick2.Clear();
    }

    /// <summary>Types text through the KERNAL keyboard buffer.</summary>
    public void TypeText(string text) => Machine.TypeText(text);

    /// <summary>The C128's 40/80 DISPLAY key (a mechanical toggle): true = 40 columns. Always true on a C64.</summary>
    public bool Display40Columns
    {
        get => Machine is not C128 c128 || c128.Display40Columns;
        set { if (Machine is C128 c128) c128.Display40Columns = value; }
    }

    /// <summary>The C128's CAPS LOCK key (a mechanical toggle). Always false on a C64.</summary>
    public bool CapsLock
    {
        get => Machine is C128 c128 && c128.CapsLock;
        set { if (Machine is C128 c128) c128.CapsLock = value; }
    }

    private void Apply(InputAction action, bool pressed)
    {
        switch (action.Kind)
        {
            case InputActionKind.Key:
                if (pressed)
                {
                    Machine.Keyboard.Press(action.Key);
                    if (action.Shift && _shiftHolders++ == 0)
                        Machine.Keyboard.Press(C64Key.LeftShift);
                }
                else
                {
                    Machine.Keyboard.Release(action.Key);
                    if (action.Shift && --_shiftHolders <= 0)
                    {
                        _shiftHolders = 0;
                        Machine.Keyboard.Release(C64Key.LeftShift);
                    }
                }
                break;
            case InputActionKind.Joystick:
                SetJoystick(action.JoystickPort, action.Joystick, pressed);
                break;
            case InputActionKind.System:
                if (action.Command == SystemCommand.Restore)
                {
                    Machine.Keyboard.SetRestore(pressed);
                }
                else if (pressed)
                {
                    if (action.Command == SystemCommand.ToggleColumns) Display40Columns = !Display40Columns;
                    else if (action.Command == SystemCommand.CapsLock) CapsLock = !CapsLock;
                    Command?.Invoke(action.Command);
                }
                break;
        }
    }

    #endregion

    #region Media

    /// <summary>
    /// Attaches a D64 image to the drive (attaching a drive if needed). With <paramref name="autostart"/> the
    /// first file is loaded and run; a C128 boot disk (CP/M) is started by resetting instead, which makes the
    /// KERNAL boot it.
    /// </summary>
    public void AttachDisk(byte[] d64, string name, bool autostart, bool writeProtected = false)
    {
        var drive = Machine.Drive ?? Machine.AttachDrive(8);
        var image = new D64Image(d64);
        drive.InsertDisk(GcrDisk.FromD64(image), writeProtected);
        bool bootable = Machine is C128 && IsBootDisk(image);
        MediaDescription = $"Disk: {name} ({image.DiskName.Trim()}){(bootable ? " [boot disk]" : "")}";
        if (autostart)
        {
            if (bootable) Machine.Reset(hard: false);
            else Machine.AutostartFromDisk("*");
        }
    }

    /// <summary>True when the disk has a C128 boot sector (track 1, sector 0 starting with "CBM").</summary>
    public static bool IsBootDisk(D64Image image)
    {
        var sector = image.GetSector(1, 0);
        return sector[0] == (byte)'C' && sector[1] == (byte)'B' && sector[2] == (byte)'M';
    }

    public void AttachDiskFile(string path, bool autostart) => AttachDisk(File.ReadAllBytes(path), Path.GetFileName(path), autostart);

    public void EjectDisk()
    {
        Machine.Drive?.InsertDisk(null);
        MediaDescription = "";
    }

    /// <summary>Current disk as a D64 (with any writes the DOS made), or null.</summary>
    public D64Image? SaveDisk()
    {
        var disk = Machine.Drive?.Disk.Disk;
        if (disk is null) return null;
        var d64 = disk.ToD64();
        Machine.Drive!.Disk.MarkSaved();
        return d64;
    }

    /// <summary>Lists the programs in a T64 image (for choosers).</summary>
    public static IReadOnlyList<T64Entry> ListTape(byte[] data) => T64Image.Load(data).Entries;

    /// <summary>
    /// Loads a program (T64 entry or PRG) into memory and runs it, as soon as BASIC is ready.
    /// </summary>
    public void AttachProgram(byte[] data, string name, int entryIndex = 0, bool run = true)
    {
        PrgFile program;
        if (T64Image.IsT64(data))
        {
            var t64 = T64Image.Load(data);
            if (t64.Entries.Count == 0) throw new InvalidDataException("The tape image contains no files");
            program = t64.GetProgram(entryIndex);
            name = t64.Entries[entryIndex].Name;
        }
        else
        {
            program = PrgFile.FromBytes(data);
        }
        Machine.InjectProgram(program, run);
        MediaDescription = $"Program: {name} (${program.LoadAddress:X4}-${program.LoadAddress + program.Data.Length - 1:X4})";
    }

    public void AttachProgramFile(string path, int entryIndex = 0, bool run = true) => AttachProgram(File.ReadAllBytes(path), Path.GetFileName(path), entryIndex, run);

    /// <summary>Plugs in a cartridge (raw or CRT) and resets (a C128 then starts in C64 mode, as the real one does).</summary>
    public void AttachCartridge(byte[] data, string name)
    {
        var cart = Cartridge.IsCrt(data) ? Cartridge.FromCrt(data, name) : Cartridge.FromRaw(data, name);
        Machine.AttachCartridge(cart);
        Machine.Reset(hard: false);
        MediaDescription = $"Cartridge: {cart.Name}";
    }

    public void AttachCartridgeFile(string path) => AttachCartridge(File.ReadAllBytes(path), Path.GetFileNameWithoutExtension(path));

    public void DetachCartridge()
    {
        Machine.AttachCartridge(null);
        Machine.Reset(hard: false);
        MediaDescription = "";
    }

    /// <summary>Attaches any supported file by extension/content (d64, t64, prg, crt, bin).</summary>
    public void AttachAuto(byte[] data, string fileName, bool autostart = true)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        if (ext == ".d64" || data.Length is 174848 or 175531 or 196608 or 197376)
            AttachDisk(data, fileName, autostart);
        else if (T64Image.IsT64(data) || ext is ".t64" or ".prg" or ".p00")
            AttachProgram(data, fileName, 0, autostart);
        else if (Cartridge.IsCrt(data) || ext is ".crt" or ".bin")
            AttachCartridge(data, Path.GetFileNameWithoutExtension(fileName));
        else
            throw new NotSupportedException($"Unknown media type: {fileName}");
    }

    #endregion
}
