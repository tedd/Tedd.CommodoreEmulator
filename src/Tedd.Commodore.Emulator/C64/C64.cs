using Tedd.Commodore.Emulator.Audio;
using Tedd.Commodore.Emulator.Cpu;
using Tedd.Commodore.Emulator.IO;
using Tedd.Commodore.Emulator.Machines;
using Tedd.Commodore.Emulator.Video;

namespace Tedd.Commodore.Emulator.C64;

/// <summary>
/// A complete PAL Commodore 64: 6510 CPU, PLA/memory, VIC-II 6569, two CIA 6526, SID 6581, keyboard, two
/// joysticks, expansion port (cartridge), the IEC serial bus and optionally a 1541 disk drive.
/// Everything advances one system cycle per <see cref="Clock"/> call; see docs/ARCHITECTURE.md ("Machine
/// timing model") for the ordering.
/// </summary>
public sealed class C64 : CommodoreMachine
{
    public RomSet Roms { get; }
    public C64Memory Memory { get; }

    public override MachineModel Model => MachineModel.C64;

    public C64(RomSet roms, int audioSampleRate = 44100)
    {
        Roms = roms;
        Memory = new C64Memory(roms);
        Cpu = new Cpu6502(Memory);
        Vic = new VicII(Memory);
        Cia1 = new Cia6526("CIA1");
        Cia2 = new Cia6526("CIA2");
        Sid = new Sid6581();
        Audio = new SidResampler(Sid, audioSampleRate, ClockFrequency);
        Memory.Vic = Vic;
        Memory.Sid = Sid;
        Memory.Cia1 = Cia1;
        Memory.Cia2 = Cia2;
        IecPort = Iec.Attach("C64");

        // CIA1: port A drives the keyboard rows (and reads joystick 2), port B reads the columns (and joystick 1).
        // The matrix is passive, so both directions are computed (Programmer's Reference Guide, keyboard matrix).
        Cia1.PortAInput = () => (byte)(Keyboard.ReadRows((byte)(Cia1.PortBOutput & Joystick1.Levels)) & Joystick2.Levels);
        Cia1.PortBInput = () => (byte)(Keyboard.ReadColumns((byte)(Cia1.PortAOutput & Joystick2.Levels)) & Joystick1.Levels);

        // CIA2: PA0-1 VIC bank (inverted), PA3 ATN out, PA4 CLK out, PA5 DATA out, PA6 CLK in, PA7 DATA in.
        Cia2.PortAChanged += UpdateCia2PortA;
        Cia2.PortAInput = () => (byte)(0x3F | (Iec.ClkLow ? 0 : 0x40) | (Iec.DataLow ? 0 : 0x80));
        Cia2.PortBInput = () => 0xFF; // user port, nothing connected

        Vic.FrameCompleted += () => FrameDone = true;

        Reset(hard: true);
    }

    private void UpdateCia2PortA()
    {
        byte pa = Cia2.PortAOutput;
        Memory.VicBank = ~pa & 3;
        IecPort.Set(pullAtn: (pa & 0x08) != 0, pullClk: (pa & 0x10) != 0, pullData: (pa & 0x20) != 0);
    }

    /// <inheritdoc />
    public override void Reset(bool hard = false)
    {
        Memory.Expansion?.Reset(hard);
        Memory.Reset(hard);
        Vic.Reset();
        Cia1.Reset();
        Cia2.Reset();
        Sid.Reset();
        Cpu.Reset();
        ResetCommon();
        UpdateCia2PortA();
    }

    /// <summary>
    /// Advances the whole machine by one system cycle. Order within the cycle: the VIC's phi1 half (memory
    /// access, BA, IRQ), then the CIAs, then the CPU's phi2 access (so a register write made in this cycle is
    /// first seen by the CIA in the next cycle, which is the convention of the Hoxs64/VICE CIA model and what the
    /// Lorenz test-suite measures), then the SID. The CPU samples the interrupt lines at the end of its cycle and
    /// therefore sees the CIA state produced in the same cycle.
    /// </summary>
    public override void Clock()
    {
        Vic.Clock();
        Cia1.Clock();
        Cia2.Clock();
        Cpu.Rdy = !Vic.Ba;
        Cpu.Irq = Vic.Irq || Cia1.IrqLine || Memory.Expansion is { Irq: true };
        Cpu.Nmi = Cia2.IrqLine || Keyboard.RestorePressed || Memory.Expansion is { Nmi: true };
        Cpu.Clock();
        Audio.Clock();
        ClockPeripherals();
    }

    #region Peripherals and memory

    protected override byte[]? DriveRom => Roms.Drive1541;

    /// <inheritdoc />
    public override ExpansionDevice? Expansion => Memory.Expansion;

    /// <inheritdoc />
    public override void AttachExpansion(ExpansionDevice? device) => Memory.Expansion = device;

    public override byte PeekMemory(ushort address) => Memory.Peek(address);
    public override void WriteMemory(ushort address, byte value) => Memory.Write(address, value);
    public override byte[] Ram => Memory.Ram;
    public override byte[] ColorRam => Memory.ColorRam;
    public override IVicMemory VicMemory => Memory;
    public override int VicBank => Memory.VicBank;

    #endregion

    #region KERNAL facts

    protected override int KeyboardBufferAddress => 0x0277;
    protected override int KeyboardCountAddress => 0x00C6;
    protected override int KeyboardMaxAddress => 0x0289;
    protected override byte ReadRam(int address) => Memory.Ram[address & 0xFFFF];
    protected override void WriteRam(int address, byte value) => Memory.Ram[address & 0xFFFF] = value;

    public override int BasicStart => 0x0801;

    protected override void SetBasicPointers(int start, int end)
    {
        // End of program / start of variables ($2D/$2E), arrays ($2F/$30), strings ($31/$32), LOAD end ($AE/$AF)
        var ram = Memory.Ram;
        ram[0x2D] = ram[0x2F] = ram[0x31] = ram[0xAE] = (byte)end;
        ram[0x2E] = ram[0x30] = ram[0x32] = ram[0xAF] = (byte)(end >> 8);
    }

    #endregion
}
