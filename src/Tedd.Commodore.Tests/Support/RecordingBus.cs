using System.Collections.Generic;
using Tedd.Commodore.Emulator.Bus;

namespace Tedd.Commodore.Tests.Support;

/// <summary>
/// 64 KiB flat RAM that records every bus access in order. Used to verify cycle-by-cycle behaviour.
/// </summary>
public sealed class RecordingBus : IBus
{
    public readonly byte[] Ram = new byte[65536];
    public readonly List<BusAccess> Trace = new();
    public bool Recording = true;

    public byte Read(ushort address)
    {
        byte v = Ram[address];
        if (Recording) Trace.Add(new BusAccess(address, v, false));
        return v;
    }

    public void Write(ushort address, byte value)
    {
        Ram[address] = value;
        if (Recording) Trace.Add(new BusAccess(address, value, true));
    }

    public void Load(int address, params byte[] bytes)
    {
        for (int i = 0; i < bytes.Length; i++)
            Ram[(address + i) & 0xFFFF] = bytes[i];
    }

    public void Clear() => Trace.Clear();
}

public readonly record struct BusAccess(ushort Address, byte Value, bool IsWrite)
{
    public override string ToString() => (IsWrite ? "W $" : "R $") + Address.ToString("X4") + " = $" + Value.ToString("X2");
}
