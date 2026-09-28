using System;
using Tedd.Commodore.Emulator.Audio;
using Tedd.Commodore.Emulator.C64;
using Tedd.Commodore.Emulator.Cpu;
using Tedd.Commodore.Emulator.IO;
using Tedd.Commodore.Emulator.Machines;
using Tedd.Commodore.Emulator.Media;
using Tedd.Commodore.Emulator.Video;

namespace Tedd.Commodore.Emulator.C128;

/// <summary>
/// A complete PAL Commodore 128: 8502 (the 6502 core with the C128's port) and Z80A sharing one bus under the
/// 8722 MMU, 128K RAM, VIC-IIe with the 2 MHz mode and the extra keyboard lines, the 8563 VDC for the 80 column
/// display, two CIAs, SID, the IEC bus with an optional 1541, and the C64 mode the MMU can switch into.
/// <para>
/// Clocking: one system cycle per <see cref="Clock"/>. The 8502 gets one bus cycle per system cycle, two in
/// 2 MHz mode. The Z80 (VICE's model: 4 MHz clock, but a memory or I/O transaction every 1 MHz cycle) gets two
/// T-states of budget per system cycle and executes whole instructions whenever the budget is positive; it does
/// not run while the VIC has the bus (BA). Only one CPU runs at a time: MMU register $D505 bit 0 hands the bus
/// over at the end of the current instruction, and the machine starts with the Z80 (which runs the boot part of
/// its BIOS and then switches to the 8502).
/// </para>
/// </summary>
public sealed class C128 : CommodoreMachine
{
    public C128RomSet Roms { get; }
    public C128Memory Memory { get; }
    /// <summary>The Z80A.</summary>
    public Z80 Z80 { get; }
    /// <summary>The 80 column chip.</summary>
    public Vdc8563 Vdc { get; }
    /// <summary>The MMU (owned by <see cref="Memory"/>).</summary>
    public Mmu8722 Mmu => Memory.Mmu;

    private bool _z80Running;
    private int _z80Budget;

    public override MachineModel Model => MachineModel.C128;

    public C128(C128RomSet roms, int audioSampleRate = 44100)
    {
        Roms = roms;
        Memory = new C128Memory(roms);
        Cpu = new Cpu6502(Memory);
        Z80 = new Z80(Memory.Z80View);
        Vic = new VicII(Memory, c128: true);
        Vdc = new Vdc8563();
        Cia1 = new Cia6526("CIA1");
        Cia2 = new Cia6526("CIA2");
        Sid = new Sid6581();
        Audio = new SidResampler(Sid, audioSampleRate, ClockFrequency);
        Memory.Vic = Vic;
        Memory.Sid = Sid;
        Memory.Cia1 = Cia1;
        Memory.Cia2 = Cia2;
        Memory.Vdc = Vdc;
        IecPort = Iec.Attach("C128");

        // CIA1: port A drives the keyboard rows (and reads joystick 2), port B reads the columns (and joystick 1);
        // the VIC-IIe drives the three extra rows through $D02F.
        Cia1.PortAInput = () => (byte)(Keyboard.ReadRows((byte)(Cia1.PortBOutput & Joystick1.Levels)) & Joystick2.Levels);
        Cia1.PortBInput = () =>
        {
            Keyboard.ExtendedRowLevels = Vic.KeyboardLines;
            return (byte)(Keyboard.ReadColumns((byte)(Cia1.PortAOutput & Joystick2.Levels)) & Joystick1.Levels);
        };

        // CIA2: PA0-1 VIC bank (inverted), PA3 ATN out, PA4 CLK out, PA5 DATA out, PA6 CLK in, PA7 DATA in.
        Cia2.PortAChanged += UpdateCia2PortA;
        Cia2.PortAInput = () => (byte)(0x3F | (Iec.ClkLow ? 0 : 0x40) | (Iec.DataLow ? 0 : 0x80));
        Cia2.PortBInput = () => 0xFF;

        Vic.FrameCompleted += () => FrameDone = true;

        Reset(hard: true);
    }

    private void UpdateCia2PortA()
    {
        byte pa = Cia2.PortAOutput;
        Memory.VicBank = ~pa & 3;
        IecPort.Set(pullAtn: (pa & 0x08) != 0, pullClk: (pa & 0x10) != 0, pullData: (pa & 0x20) != 0);
    }

    /// <summary>The 40/80 DISPLAY key: true = 40 columns (key up). The KERNAL reads it at reset and on ESC X.</summary>
    public bool Display40Columns
    {
        get => Mmu.Display40Key;
        set => Mmu.Display40Key = value;
    }

    /// <summary>The CAPS LOCK key (a mechanical toggle, read through the 8502 port).</summary>
    public bool CapsLock
    {
        get => Memory.CapsLock;
        set => Memory.CapsLock = value;
    }

    /// <summary>True while the Z80 owns the bus.</summary>
    public bool Z80Active => _z80Running;

    /// <summary>True in C64 mode.</summary>
    public bool C64Mode => Mmu.C64Mode;

    /// <inheritdoc />
    public override void Reset(bool hard = false)
    {
        Memory.Expansion?.Reset(hard);
        Memory.Reset(hard);
        Vic.Reset();
        Vdc.Reset();
        Cia1.Reset();
        Cia2.Reset();
        Sid.Reset();
        Cpu.Reset();
        Z80.Reset();
        _z80Running = Mmu.Z80Active;
        _z80Budget = 0;
        ResetCommon();
        UpdateCia2PortA();
    }

    /// <summary>
    /// One system cycle: VIC (phi1 access, BA, IRQ), CIAs, then the active CPU: the 8502 for one cycle (two in
    /// 2 MHz mode) or the Z80 for two T-states of budget, then SID, VDC and the shared peripherals.
    /// </summary>
    public override void Clock()
    {
        Vic.Clock();
        Cia1.Clock();
        Cia2.Clock();
        bool irq = Vic.Irq || Cia1.IrqLine || Memory.Expansion is { Irq: true };
        bool nmi = Cia2.IrqLine || Keyboard.RestorePressed || Memory.Expansion is { Nmi: true };

        if (_z80Running)
        {
            Z80.Irq = irq;
            Z80.SetNmi(nmi);
            if (!Vic.Ba)
            {
                _z80Budget += 2;
                while (_z80Budget > 0)
                {
                    _z80Budget -= Z80.Step();
                    if (!Mmu.Z80Active)
                    {
                        _z80Running = false;
                        _z80Budget = 0;
                        break;
                    }
                }
            }
        }
        else
        {
            Cpu.Rdy = !Vic.Ba;
            Cpu.Irq = irq;
            Cpu.Nmi = nmi;
            Cpu.Clock();
            if (Vic.FastMode && !(Mmu.Z80Active && Cpu.AtInstructionBoundary))
            {
                Cpu.Rdy = !Vic.Ba;
                Cpu.Clock();
            }
            if (Mmu.Z80Active && Cpu.AtInstructionBoundary)
            {
                _z80Running = true;
                _z80Budget = 0;
            }
        }

        Audio.Clock();
        Vdc.Clock();
        ClockPeripherals();
    }

    /// <inheritdoc />
    public override bool AtInstructionBoundary => _z80Running || Cpu.AtInstructionBoundary;

    #region Peripherals and memory

    protected override byte[]? DriveRom => Roms.Drive1541;

    /// <inheritdoc />
    public override ExpansionDevice? Expansion => Memory.Expansion;

    /// <inheritdoc />
    public override void AttachExpansion(ExpansionDevice? device) => Memory.Expansion = device;

    public override byte PeekMemory(ushort address) => Memory.Peek(address);
    public override void WriteMemory(ushort address, byte value) => Memory.Write(address, value);
    /// <summary>Both RAM banks: bank 0 at 0..$FFFF, bank 1 at $10000..$1FFFF.</summary>
    public override byte[] Ram => Memory.Ram;
    public override byte[] ColorRam => Memory.ColorRam;
    public override IVicMemory VicMemory => Memory;
    public override int VicBank => Memory.VicBank;

    #endregion

    #region Screen

    /// <summary>The 80 column screen as text (screen codes converted like the 40 column screen's).</summary>
    public string GetVdcScreenText() => Vdc.GetScreenText(b => Petscii.ScreenCodeToAscii((byte)(b & 0x7F)));

    /// <summary>The 80 column screen as text with the bytes taken as ASCII (what CP/M puts there).</summary>
    public string GetVdcScreenAscii() => Vdc.GetScreenText(b => b is >= 0x20 and < 0x7F ? (char)b : ' ');

    /// <summary>True when BASIC's READY. prompt is on either screen and the KERNAL waits for input.</summary>
    public override bool IsBasicReady()
    {
        if (ReadRam(KeyboardCountAddress) != 0) return false;
        if (GetScreenText().Contains("READY.", StringComparison.Ordinal)) return true;
        return !C64Mode && GetVdcScreenText().Contains("READY.", StringComparison.Ordinal);
    }

    #endregion

    #region KERNAL facts

    protected override int KeyboardBufferAddress => C64Mode ? 0x0277 : 0x034A;
    protected override int KeyboardCountAddress => C64Mode ? 0x00C6 : 0x00D0;
    protected override int KeyboardMaxAddress => C64Mode ? 0x0289 : 0x0A20;
    protected override byte ReadRam(int address) => Memory.Ram[address & 0xFFFF];
    protected override void WriteRam(int address, byte value) => Memory.Ram[address & 0xFFFF] = value;

    public override int BasicStart => C64Mode ? 0x0801 : 0x1C01;

    protected override void SetBasicPointers(int start, int end)
    {
        var ram = Memory.Ram;
        if (C64Mode)
        {
            ram[0x2D] = ram[0x2F] = ram[0x31] = ram[0xAE] = (byte)end;
            ram[0x2E] = ram[0x30] = ram[0x32] = ram[0xAF] = (byte)(end >> 8);
        }
        else
        {
            // BASIC 7: program text in bank 0 from TXTTAB ($2D/$2E), end of text in TEXT_TOP ($1210/$1211);
            // variables live in bank 1 and are not affected by the program size.
            ram[0x1210] = (byte)end;
            ram[0x1211] = (byte)(end >> 8);
        }
    }

    #endregion
}
