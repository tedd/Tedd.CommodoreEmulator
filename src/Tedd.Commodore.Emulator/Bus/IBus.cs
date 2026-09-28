namespace Tedd.Commodore.Emulator.Bus;

/// <summary>
/// A 16-bit address / 8-bit data bus as seen from a CPU. Every call represents exactly one bus cycle.
/// </summary>
public interface IBus
{
    byte Read(ushort address);
    void Write(ushort address, byte value);
}

/// <summary>
/// Something that advances one clock cycle at a time.
/// </summary>
public interface IClockable
{
    void Clock();
}
