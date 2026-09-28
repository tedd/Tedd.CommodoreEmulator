using System;
using Tedd.Commodore.Emulator.C64;

namespace Tedd.Commodore.Tests.Support;

/// <summary>
/// An expansion port device for the tests. I/O 1 registers: $DE00 (write) selects which 8K bank of
/// <see cref="Banks"/> ROML shows; $DE01 (write) drives the lines (bit 0 = EXROM level, bit 1 = GAME level, so $03
/// maps nothing); $DE02 (write) bit 0 asserts IRQ, bit 1 NMI; $DE03 (read) counts its reads, a read side effect;
/// $DE04 (read) returns <see cref="Input"/>. Every other I/O 1 address is open bus. I/O 2 ($DF00-$DFFF) is 256 bytes
/// of RAM. Bank n of <see cref="Banks"/> is filled with $80 + n.
/// </summary>
public sealed class TestExpansionDevice : ExpansionDevice
{
    public readonly byte[] Banks = new byte[4 * WindowSize];
    public readonly byte[] IoRam = new byte[256];
    public (ushort Address, byte Value) LastWrite;
    public int WriteCount, ReadCount, Resets;
    public bool? LastResetHard;
    public byte Input;

    public TestExpansionDevice() : base("test device")
    {
        for (int bank = 0; bank < 4; bank++)
            Array.Fill(Banks, (byte)(0x80 + bank), bank * WindowSize, WindowSize);
        MapRomL(Banks);
    }

    /// <summary>Points ROML anywhere (null = no ROML), for the window tests.</summary>
    public void Map(byte[]? romL, int offset = 0) => MapRomL(romL, offset);

    public override byte? ReadIo(ushort address) => address == 0xDE03 ? (byte)++ReadCount : PeekIo(address);

    public override byte? PeekIo(ushort address) => address switch
    {
        0xDE03 => (byte)ReadCount,
        0xDE04 => Input,
        >= 0xDF00 => IoRam[address & 0xFF],
        _ => null,
    };

    public override void WriteIo(ushort address, byte value)
    {
        LastWrite = (address, value);
        WriteCount++;
        switch (address)
        {
            case 0xDE00: MapRomL(Banks, (value & 3) * WindowSize); break;
            case 0xDE01: SetLines(exrom: (value & 1) != 0, game: (value & 2) != 0); break;
            case 0xDE02: Irq = (value & 1) != 0; Nmi = (value & 2) != 0; break;
            case >= 0xDF00: IoRam[address & 0xFF] = value; break;
        }
    }

    public override void Reset(bool hard)
    {
        Resets++;
        LastResetHard = hard;
        Irq = Nmi = false;
        MapRomL(Banks);
        SetLines(exrom: true, game: true);
    }
}
