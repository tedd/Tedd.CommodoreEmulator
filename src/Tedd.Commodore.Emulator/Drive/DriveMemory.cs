using Tedd.Commodore.Emulator.Bus;
using Tedd.Commodore.Emulator.IO;

namespace Tedd.Commodore.Emulator.Drive;

/// <summary>
/// Address space of the 1541: 2K RAM at $0000-$07FF, VIA1 at $1800-$180F, VIA2 at $1C00-$1C0F (both mirrored
/// through their $400 window), 16K DOS ROM at $C000-$FFFF. Address lines A13/A14 are not decoded for the RAM/VIA
/// area (mirrors at $2000, $4000, $6000) and A15 selects the ROM (mirror at $8000-$BFFF). Unused areas return the
/// last value seen on the bus.
/// </summary>
public sealed class DriveMemory : IBus
{
    public readonly byte[] Ram = new byte[2048];
    private readonly byte[] _rom;
    private readonly Via6522 _via1;
    private readonly Via6522 _via2;

    /// <summary>Last value on the data bus (returned for open addresses).</summary>
    public byte LastBusValue;

    public DriveMemory(byte[] rom, Via6522 via1, Via6522 via2)
    {
        _rom = rom;
        _via1 = via1;
        _via2 = via2;
    }

    public byte[] Rom => _rom;

    public byte Read(ushort address)
    {
        byte v = ReadInternal(address, peek: false);
        LastBusValue = v;
        return v;
    }

    /// <summary>Read without side effects (VIA registers are peeked).</summary>
    public byte Peek(ushort address) => ReadInternal(address, peek: true);

    private byte ReadInternal(ushort address, bool peek)
    {
        if ((address & 0x8000) != 0)
            return _rom[address & 0x3FFF];
        int a = address & 0x1FFF;
        if (a < 0x0800)
            return Ram[a];
        if ((a & 0x1C00) == 0x1800)
            return peek ? _via1.Peek(a & 0x0F) : _via1.Read(a & 0x0F);
        if ((a & 0x1C00) == 0x1C00)
            return peek ? _via2.Peek(a & 0x0F) : _via2.Read(a & 0x0F);
        return LastBusValue;
    }

    public void Write(ushort address, byte value)
    {
        LastBusValue = value;
        if ((address & 0x8000) != 0)
            return;
        int a = address & 0x1FFF;
        if (a < 0x0800)
            Ram[a] = value;
        else if ((a & 0x1C00) == 0x1800)
            _via1.Write(a & 0x0F, value);
        else if ((a & 0x1C00) == 0x1C00)
            _via2.Write(a & 0x0F, value);
    }
}
