using System;
using System.Collections.Generic;
using System.Text;
using Tedd.Commodore.Emulator.Audio;
using Tedd.Commodore.Emulator.C64;
using Tedd.Commodore.Emulator.Cpu;
using Tedd.Commodore.Emulator.Drive;
using Tedd.Commodore.Emulator.IO;
using Tedd.Commodore.Emulator.Media;
using Tedd.Commodore.Emulator.Video;

namespace Tedd.Commodore.Emulator.Machines;

/// <summary>The computers this emulator can be.</summary>
public enum MachineModel
{
    /// <summary>Commodore 64 (PAL).</summary>
    C64,
    /// <summary>Commodore 128 (PAL): 8502 + Z80, MMU, VIC-IIe and the 8563 VDC.</summary>
    C128,
}

/// <summary>
/// What the Commodore 64 and 128 have in common, as seen by the hosting layer and the tools: the 6502-family
/// CPU, VIC-II, two CIAs, the SID, keyboard, joysticks, the IEC bus with an optional 1541, the one-cycle
/// <see cref="Clock"/> / <see cref="RunFrame"/> stepping, typing through the KERNAL keyboard buffer, disk
/// autostart and program injection. Everything machine specific (the memory map, the boot ROMs, the C128's
/// second CPU and second video chip) lives in <see cref="C64.C64"/> and <see cref="C128.C128"/>.
/// </summary>
public abstract class CommodoreMachine
{
    /// <summary>PAL system clock in Hz (the same for both machines).</summary>
    public const double ClockFrequency = 985248.0;
    /// <summary>Cycles per PAL frame (63 cycles x 312 lines).</summary>
    public const int CyclesPerFrame = VicII.CyclesPerLine * VicII.LinesPerFrame;
    /// <summary>PAL frame rate.</summary>
    public const double FrameRate = ClockFrequency / CyclesPerFrame;
    /// <summary>The 1541's own clock in Hz.</summary>
    public const double DriveClockFrequency = 1_000_000.0;

    protected const int ClockInt = 985248;
    protected const int DriveClockInt = 1_000_000;
    protected const int TodHz = 50;

    private int _todAccumulator;
    private int _driveAccumulator;
    private readonly Queue<byte> _typeAhead = new();
    private Action? _injectWhenReady;

    /// <summary>Set by the VIC at the end of its last cycle; <see cref="RunFrame"/> runs until it is.</summary>
    protected bool FrameDone;

    public abstract MachineModel Model { get; }

    public Cpu6502 Cpu { get; protected set; } = null!;
    public VicII Vic { get; protected set; } = null!;
    public Cia6526 Cia1 { get; protected set; } = null!;
    public Cia6526 Cia2 { get; protected set; } = null!;
    public Sid6581 Sid { get; protected set; } = null!;
    public SidResampler Audio { get; protected set; } = null!;
    public Keyboard Keyboard { get; } = new();
    public Joystick Joystick1 { get; } = new();
    public Joystick Joystick2 { get; } = new();
    public IecBus Iec { get; } = new();
    public IecPort IecPort { get; protected set; } = null!;
    public Drive1541? Drive { get; private set; }

    /// <summary>System cycles executed since power-on.</summary>
    public long Cycles { get; protected set; }
    /// <summary>Frames completed since power-on.</summary>
    public long Frames { get; protected set; }

    /// <summary>The 1541 DOS ROM of the machine's ROM set, or null when there is none.</summary>
    protected abstract byte[]? DriveRom { get; }

    #region Clocking

    /// <summary>Advances the whole machine by one system cycle.</summary>
    public abstract void Clock();

    /// <summary>The parts of a cycle both machines share: CIA TOD ticks, the 1541 at its own clock, the cycle counter.</summary>
    protected void ClockPeripherals()
    {
        _todAccumulator += TodHz;
        if (_todAccumulator >= ClockInt)
        {
            _todAccumulator -= ClockInt;
            Cia1.TodTick();
            Cia2.TodTick();
        }

        if (Drive is { } drive)
        {
            _driveAccumulator += DriveClockInt;
            while (_driveAccumulator >= ClockInt)
            {
                _driveAccumulator -= ClockInt;
                drive.Clock();
            }
        }

        Cycles++;
    }

    /// <summary>Runs until the VIC completes the current frame. Returns the number of cycles executed.</summary>
    public int RunFrame()
    {
        FrameDone = false;
        int n = 0;
        while (!FrameDone)
        {
            Clock();
            n++;
        }
        Frames++;
        ServiceTypeAhead();
        ServiceAutostart();
        return n;
    }

    /// <summary>Runs the given number of cycles.</summary>
    public void RunCycles(long cycles)
    {
        for (long i = 0; i < cycles; i++)
            Clock();
    }

    /// <summary>True when the next cycle starts a new instruction on the active CPU.</summary>
    public virtual bool AtInstructionBoundary => Cpu.AtInstructionBoundary;

    /// <summary>Runs whole instructions until the active CPU is at an instruction boundary again (at least one cycle).</summary>
    public int StepInstruction()
    {
        int n = 0;
        do
        {
            Clock();
            n++;
        } while (!AtInstructionBoundary && n < 100_000);
        return n;
    }

    /// <summary>Resets the machine. A hard reset (power cycle) also clears RAM to the power-on pattern.</summary>
    public abstract void Reset(bool hard = false);

    /// <summary>Called by the machine's own reset after the chips were reset.</summary>
    protected void ResetCommon()
    {
        Drive?.Reset();
        Keyboard.ReleaseAll();
        _typeAhead.Clear();
        _injectWhenReady = null;
        Autostart = AutostartState.Idle;
    }

    #endregion

    #region Peripherals

    /// <summary>Plugs a cartridge into the expansion port (null removes it). A reset is needed to start it.</summary>
    public abstract void AttachCartridge(Cartridge? cartridge);

    /// <summary>
    /// Connects a 1541 drive (device 8 by default) using the ROM from the ROM set. Returns the drive.
    /// Throws if the ROM set has no 1541 ROM.
    /// </summary>
    public Drive1541 AttachDrive(int deviceNumber = 8)
    {
        var rom = DriveRom ?? throw new InvalidOperationException("No 1541 ROM available in the ROM set");
        Drive?.Detach();
        Drive = new Drive1541(rom, Iec, deviceNumber);
        Drive.Reset();
        return Drive;
    }

    public void DetachDrive()
    {
        Drive?.Detach();
        Drive = null;
    }

    #endregion

    #region Memory access for tools

    /// <summary>Reads the CPU address space without side effects (for debuggers).</summary>
    public abstract byte PeekMemory(ushort address);

    /// <summary>Writes through the CPU address space, with the side effects a CPU write has (memory editors).</summary>
    public abstract void WriteMemory(ushort address, byte value);

    /// <summary>The main RAM (bank 0 on the C128), for viewers and program injection.</summary>
    public abstract byte[] Ram { get; }

    /// <summary>The color RAM nibbles (1K on the C64, 2 x 1K on the C128).</summary>
    public abstract byte[] ColorRam { get; }

    /// <summary>The VIC's view of memory (the current 16K bank).</summary>
    public abstract IVicMemory VicMemory { get; }

    /// <summary>VIC bank (0..3) selected through CIA 2.</summary>
    public abstract int VicBank { get; }

    /// <summary>Address of the video matrix (screen RAM) in CPU address space, from the VIC bank and $D018.</summary>
    public int ScreenAddress => (VicBank << 14) | ((Vic.Peek(0x18) & 0xF0) << 6);

    #endregion

    #region Screen helpers

    /// <summary>Returns the 25 lines of 40 characters currently in the VIC's screen memory, converted from screen codes to ASCII.</summary>
    public virtual string GetScreenText()
    {
        int matrix = (Vic.Peek(0x18) & 0xF0) << 6;
        var memory = VicMemory;
        var sb = new StringBuilder(25 * 41);
        var line = new char[40];
        for (int row = 0; row < 25; row++)
        {
            int len = 0;
            for (int col = 0; col < 40; col++)
            {
                // Reverse-video bit ignored; trailing spaces trimmed so callers can match line by line.
                char c = Petscii.ScreenCodeToAscii((byte)(memory.PeekVic((matrix + row * 40 + col) & 0x3FFF) & 0x7F));
                line[col] = c;
                if (c != ' ') len = col + 1;
            }
            sb.Append(line, 0, len).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>True when the BASIC "READY." prompt is on screen and the KERNAL is waiting for input.</summary>
    public virtual bool IsBasicReady()
    {
        return GetScreenText().Contains("READY.", StringComparison.Ordinal) && ReadRam(KeyboardCountAddress) == 0;
    }

    /// <summary>Runs frames until BASIC is ready or the frame budget is exhausted. Returns true when ready.</summary>
    public bool WaitForBasicReady(int maxFrames = 300)
    {
        for (int i = 0; i < maxFrames; i++)
        {
            RunFrame();
            if (IsBasicReady()) return true;
        }
        return IsBasicReady();
    }

    #endregion

    #region Typing and program injection

    /// <summary>Address of the KERNAL keyboard buffer (C64: $0277; C128: $034A).</summary>
    protected abstract int KeyboardBufferAddress { get; }
    /// <summary>Address of the keyboard buffer count (C64: $C6; C128: $D0).</summary>
    protected abstract int KeyboardCountAddress { get; }
    /// <summary>Address of the keyboard buffer size (C64: $0289; C128: $0A20).</summary>
    protected abstract int KeyboardMaxAddress { get; }

    /// <summary>Reads a byte of the KERNAL's RAM (bank 0) directly.</summary>
    protected abstract byte ReadRam(int address);
    /// <summary>Writes a byte of the KERNAL's RAM (bank 0) directly.</summary>
    protected abstract void WriteRam(int address, byte value);

    /// <summary>
    /// Types text as if entered on the keyboard, using the KERNAL keyboard buffer. The text is queued and fed a
    /// few characters per frame; '\n' produces RETURN. Uppercase/lowercase are mapped to PETSCII so that BASIC
    /// commands can be typed in either case.
    /// </summary>
    public void TypeText(string text)
    {
        foreach (var b in Petscii.AsciiToKeyboardBuffer(text))
            _typeAhead.Enqueue(b);
        ServiceTypeAhead();
    }

    /// <summary>Number of characters still waiting to be typed.</summary>
    public int PendingTypeAhead => _typeAhead.Count;

    private void ServiceTypeAhead()
    {
        if (_injectWhenReady is not null && IsBasicReady())
        {
            var inject = _injectWhenReady;
            _injectWhenReady = null;
            inject();
        }
        if (_typeAhead.Count == 0) return;
        int count = ReadRam(KeyboardCountAddress);
        int max = Math.Min((int)ReadRam(KeyboardMaxAddress), 10);
        if (max == 0) max = 10;
        int buffer = KeyboardBufferAddress;
        while (count < max && _typeAhead.Count > 0)
        {
            WriteRam(buffer + count, _typeAhead.Dequeue());
            count++;
        }
        WriteRam(KeyboardCountAddress, (byte)count);
    }

    /// <summary>State of the disk autostart helper.</summary>
    public enum AutostartState { Idle, WaitingForBasic, Loading, WaitingForRun, Done, Failed }

    /// <summary>Progress of <see cref="AutostartFromDisk"/>.</summary>
    public AutostartState Autostart { get; private set; } = AutostartState.Idle;
    private string _autostartFile = "*";
    private int _autostartFrames;

    /// <summary>
    /// Types LOAD"file",8,1 as soon as BASIC is ready, waits for the load to finish (READY. after LOADING)
    /// and then types RUN. Progress can be observed through <see cref="Autostart"/>; the helper is serviced at the
    /// end of every frame.
    /// </summary>
    public void AutostartFromDisk(string file = "*")
    {
        _autostartFile = file;
        _autostartFrames = 0;
        Autostart = AutostartState.WaitingForBasic;
    }

    public void CancelAutostart() => Autostart = AutostartState.Idle;

    private void ServiceAutostart()
    {
        switch (Autostart)
        {
            case AutostartState.WaitingForBasic:
                if (IsBasicReady())
                {
                    TypeText($"LOAD\"{_autostartFile}\",8,1\n");
                    Autostart = AutostartState.Loading;
                    _autostartFrames = 0;
                }
                break;
            case AutostartState.Loading:
            {
                _autostartFrames++;
                var screen = GetScreenText();
                int loading = screen.IndexOf("LOADING", StringComparison.Ordinal);
                if (screen.Contains("?FILE NOT FOUND", StringComparison.Ordinal) || screen.Contains("DEVICE NOT PRESENT", StringComparison.Ordinal))
                {
                    Autostart = AutostartState.Failed;
                }
                else if (loading >= 0 && screen.IndexOf("READY.", loading, StringComparison.Ordinal) >= 0 && ReadRam(KeyboardCountAddress) == 0)
                {
                    Autostart = AutostartState.WaitingForRun;
                    _autostartFrames = 0;
                }
                else if (_autostartFrames > 50 * 120) // two minutes: give up
                {
                    Autostart = AutostartState.Failed;
                }
                break;
            }
            case AutostartState.WaitingForRun:
                // A few frames of grace so the KERNAL has returned to the input loop.
                if (++_autostartFrames >= 5)
                {
                    TypeText("RUN\n");
                    Autostart = AutostartState.Done;
                }
                break;
        }
    }

    /// <summary>
    /// Loads a program into RAM the way LOAD"...",8 would (BASIC pointers updated) and optionally starts it
    /// with RUN (BASIC programs) or SYS (machine code). If BASIC is not ready yet, the injection is deferred
    /// until the READY prompt appears.
    /// </summary>
    public void InjectProgram(PrgFile program, bool run = true)
    {
        void DoInject()
        {
            int load = program.LoadAddress;
            for (int i = 0; i < program.Data.Length; i++)
                WriteRam((load + i) & 0xFFFF, program.Data[i]);
            SetBasicPointers(load, load + program.Data.Length);
            if (run)
                TypeText(load == BasicStart ? "RUN\n" : $"SYS{load}\n");
        }

        if (IsBasicReady())
            DoInject();
        else
            _injectWhenReady = DoInject;
    }

    /// <summary>Start of BASIC program text (C64: $0801; C128: $1C01).</summary>
    public abstract int BasicStart { get; }

    /// <summary>Updates the BASIC pointers after a program was placed at <paramref name="start"/>..<paramref name="end"/> (exclusive).</summary>
    protected abstract void SetBasicPointers(int start, int end);

    #endregion
}
