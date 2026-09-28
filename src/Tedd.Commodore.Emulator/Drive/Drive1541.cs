using System;
using Tedd.Commodore.Emulator.Cpu;
using Tedd.Commodore.Emulator.IO;

namespace Tedd.Commodore.Emulator.Drive;

/// <summary>
/// A Commodore 1541 disk drive: 6502 CPU at 1 MHz, 2K RAM, 16K DOS ROM, two 6522 VIAs and the disk mechanics.
/// VIA1 ($1800) talks to the IEC bus, VIA2 ($1C00) controls the drive mechanics and reads/writes the disk data.
/// Wiring per the 1541 schematic (see docs/ARCHITECTURE.md, "IEC serial bus" and "1541 drive").
/// </summary>
public sealed class Drive1541
{
    public Cpu6502 Cpu { get; }
    public DriveMemory Memory { get; }
    public Via6522 Via1 { get; }
    public Via6522 Via2 { get; }
    public DiskUnit Disk { get; } = new();
    public IecPort Port { get; }
    public int DeviceNumber { get; }
    public long Cycles { get; private set; }

    private readonly IecBus _bus;
    private bool _soe;           // VIA2 CA2: "set overflow enable"
    private bool _byteReadyLine = true;

    public Drive1541(byte[] rom, IecBus bus, int deviceNumber = 8)
    {
        if (rom.Length != 16384) throw new ArgumentException("1541 ROM must be 16 KiB", nameof(rom));
        if (deviceNumber is < 8 or > 11) throw new ArgumentOutOfRangeException(nameof(deviceNumber));
        DeviceNumber = deviceNumber;
        _bus = bus;
        Via1 = new Via6522("1541 VIA1");
        Via2 = new Via6522("1541 VIA2");
        Memory = new DriveMemory(rom, Via1, Via2);
        Cpu = new Cpu6502(Memory);
        Port = bus.Attach($"1541 #{deviceNumber}");

        // VIA1 port B: PB0 DATA in, PB1 DATA out, PB2 CLK in, PB3 CLK out, PB4 ATNA, PB5/PB6 device number, PB7 ATN in.
        // Inputs come through inverters: a bit reads 1 when the bus line is low.
        int deviceBits = ((deviceNumber - 8) & 3) << 5;
        Via1.PortBInput = () => (byte)(
            (_bus.DataLow ? 0x01 : 0) |
            (_bus.ClkLow ? 0x04 : 0) |
            (_bus.AtnLow ? 0x80 : 0) |
            deviceBits | 0x1A);
        Via1.PortAInput = () => 0xFF; // parallel port (unused)
        Via1.PortBChanged += UpdateBusOutputs;
        bus.Changed += OnBusChanged;

        // VIA2 port A: data byte from/to the disk. Port B: PB0-1 stepper, PB2 motor, PB3 LED, PB4 write protect
        // sense (1 = write enabled), PB5-6 density, PB7 SYNC (0 = sync found).
        Via2.PortAInput = () => Disk.ReadLatch;
        Via2.PortBInput = () => (byte)(0x6F | (Disk.WriteProtected ? 0 : 0x10) | (Disk.Sync ? 0 : 0x80));
        Via2.PortBChanged += () => Disk.SetControl(Via2.PortBOutput);
        Via2.PortAChanged += () => Disk.WriteData = Via2.PortAOutput;
        // CA2 = SOE (byte ready also sets the CPU's V flag when high), CB2 = read (high) / write (low) mode.
        Via2.Ca2Changed += () => _soe = Via2.Ca2Output;
        Via2.Cb2Changed += () => Disk.WriteMode = !Via2.Cb2Output;
        OnBusChanged();
    }

    /// <summary>Drive activity LED.</summary>
    public bool Led => Disk.Led;
    /// <summary>Spindle motor running.</summary>
    public bool MotorOn => Disk.MotorOn;

    private void UpdateBusOutputs()
    {
        byte pb = Via1.PortBOutput;
        bool atna = (pb & 0x10) != 0;
        // The 1541 hardware pulls DATA low by itself whenever ATN (inverted) differs from ATNA (74LS00 on the board).
        bool autoData = _bus.AtnLow != atna;
        Port.Set(pullAtn: false, pullClk: (pb & 0x08) != 0, pullData: (pb & 0x02) != 0 || autoData);
    }

    private void OnBusChanged()
    {
        // ATN (inverted) feeds VIA1 CA1: the line goes high when ATN is asserted.
        Via1.Ca1 = _bus.AtnLow;
        UpdateBusOutputs();
    }

    public void Reset()
    {
        Via1.Reset();
        Via2.Reset();
        Disk.Reset();
        Cpu.Reset();
        _soe = Via2.Ca2Output;
        Disk.WriteMode = !Via2.Cb2Output;
        Disk.SetControl(Via2.PortBOutput);
        UpdateBusOutputs();
        Via1.Ca1 = _bus.AtnLow;
    }

    /// <summary>Removes the drive from the bus.</summary>
    public void Detach()
    {
        _bus.Changed -= OnBusChanged;
        _bus.Detach(Port);
    }

    /// <summary>Inserts a disk image (null ejects).</summary>
    public void InsertDisk(GcrDisk? disk, bool writeProtected = false) => Disk.Insert(disk, writeProtected);

    /// <summary>Advances the drive by one 1 MHz cycle.</summary>
    public void Clock()
    {
        Disk.Clock();
        if (Disk.ByteReady)
        {
            // Byte ready is an active-low pulse into VIA2 CA1 (negative edge) and, when SOE is high, the CPU's SO pin.
            _byteReadyLine = false;
            Via2.Ca1 = false;
            if (_soe)
                Cpu.SetOverflow();
        }
        else if (!_byteReadyLine)
        {
            _byteReadyLine = true;
            Via2.Ca1 = true;
        }

        Via1.Clock();
        Via2.Clock();
        Cpu.Irq = Via1.IrqLine || Via2.IrqLine;
        Cpu.Clock();
        Cycles++;
    }
}
