namespace Tedd.Commodore.Emulator.Video;

/// <summary>
/// The memory as seen by the VIC-II: a 14 bit address space (the 16 KiB VIC bank selected through CIA 2 port A)
/// plus the 1 KiB x 4 bit color RAM. The implementation (the C64 memory map / PLA) maps the character ROM into
/// $1000-$1FFF of banks 0 and 2 and, in Ultimax mode, ROMH into $3000-$3FFF of every bank.
/// </summary>
public interface IVicMemory
{
    /// <summary>Reads one byte from the currently selected VIC bank. <paramref name="address14"/> is 0..$3FFF.</summary>
    byte ReadVic(int address14);

    /// <summary>Reads one color RAM nibble. <paramref name="address10"/> is 0..$3FF; the result is 0..15.</summary>
    byte ReadColor(int address10);

    /// <summary>
    /// Same as <see cref="ReadVic"/> but without side effects, for debuggers and viewers: a real read also
    /// leaves the value on the bus, where an open-bus CPU read would pick it up.
    /// </summary>
    byte PeekVic(int address14);
}
