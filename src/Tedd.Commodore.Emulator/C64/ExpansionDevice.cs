using System;

namespace Tedd.Commodore.Emulator.C64;

/// <summary>
/// Something plugged into the expansion port of the C64 (the C128 has the same port). What the computer sees of
/// it: the EXROM and GAME lines, which the PLA uses to map the two 8K ROM windows ROML ($8000) and ROMH ($A000, or
/// $E000 in Ultimax mode); the I/O 1 ($DE00-$DEFF) and I/O 2 ($DF00-$DFFF) pages; and the IRQ and NMI lines.
/// <see cref="Cartridge"/> is the plain ROM cartridge. Derive from this class for anything else, such as a device
/// written in .NET that 6502 programs talk to through registers in I/O 1 / I/O 2. See docs/ARCHITECTURE.md,
/// "Expansion port".
/// <para>
/// Threading: the machine calls the device on the emulation thread, inside the bus cycle that makes the access, so
/// a handler takes no emulated time. It must return quickly and never block: start slow work elsewhere and report
/// the result through a status register or <see cref="Irq"/>. Change the lines, the windows and
/// <see cref="Irq"/>/<see cref="Nmi"/> only on the emulation thread, i.e. in a handler, in <see cref="Reset"/>, or
/// through the host's <c>Invoke</c> (EmbeddedEmulator, EmulatorRunner).
/// </para>
/// <para>
/// A device that keeps state belongs in one machine. A device whose state never changes after construction (a
/// <see cref="Cartridge"/>) can be plugged into several machines at once.
/// </para>
/// </summary>
public abstract class ExpansionDevice
{
    /// <summary>Size of the ROML and ROMH windows.</summary>
    public const int WindowSize = 8192;

    private Action? _changed;

    protected ExpansionDevice(string name)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
    }

    /// <summary>A name for the user interface.</summary>
    public string Name { get; }

    /// <summary>EXROM line level: true = high (not asserted), which is also the level with nothing plugged in.</summary>
    public bool Exrom { get; private set; } = true;

    /// <summary>GAME line level: true = high (not asserted), which is also the level with nothing plugged in.</summary>
    public bool Game { get; private set; } = true;

    /// <summary>True when the lines select the Ultimax configuration (EXROM high, GAME low).</summary>
    public bool IsUltimax => Exrom && !Game;

    /// <summary>
    /// The array behind ROML, or null when the device puts nothing on the bus there (the CPU then reads open bus
    /// wherever the PLA maps ROML). The window is the <see cref="WindowSize"/> bytes from <see cref="RomLOffset"/>.
    /// </summary>
    public byte[]? RomL { get; private set; }

    /// <summary>Where the ROML window starts in <see cref="RomL"/>.</summary>
    public int RomLOffset { get; private set; }

    /// <summary>The array behind ROMH, or null (see <see cref="RomL"/>).</summary>
    public byte[]? RomH { get; private set; }

    /// <summary>Where the ROMH window starts in <see cref="RomH"/>.</summary>
    public int RomHOffset { get; private set; }

    /// <summary>IRQ line: true = asserted (pulled low). Combined with the machine's other IRQ sources every cycle.</summary>
    public bool Irq { get; protected set; }

    /// <summary>
    /// NMI line: true = asserted (pulled low). Combined with the machine's other NMI sources every cycle; the CPU
    /// takes an NMI on the edge of the combined line, so release it before asserting it again.
    /// </summary>
    public bool Nmi { get; protected set; }

    /// <summary>Drives the EXROM and GAME lines (true = high, not asserted). The memory map follows at once.</summary>
    protected void SetLines(bool exrom, bool game)
    {
        if (exrom == Exrom && game == Game) return;
        Exrom = exrom;
        Game = game;
        _changed?.Invoke();
    }

    /// <summary>
    /// Points the ROML window at <paramref name="data"/> from <paramref name="offset"/> (null = nothing on the bus).
    /// The CPU reads the array directly, so changes to its contents show on the next read; call this again only to
    /// move the window, e.g. to switch banks.
    /// </summary>
    protected void MapRomL(byte[]? data, int offset = 0)
    {
        CheckWindow(data, offset);
        RomL = data;
        RomLOffset = offset;
        _changed?.Invoke();
    }

    /// <summary>Points the ROMH window at <paramref name="data"/> from <paramref name="offset"/>; see <see cref="MapRomL"/>.</summary>
    protected void MapRomH(byte[]? data, int offset = 0)
    {
        CheckWindow(data, offset);
        RomH = data;
        RomHOffset = offset;
        _changed?.Invoke();
    }

    private static void CheckWindow(byte[]? data, int offset)
    {
        if (data is null) return;
        if (offset < 0 || offset > data.Length - WindowSize)
            throw new ArgumentOutOfRangeException(nameof(offset), $"An 8K window from {offset} does not fit in {data.Length} bytes");
    }

    /// <summary>
    /// A CPU read of I/O 1 or I/O 2 (<paramref name="address"/> is $DE00-$DFFF). Return null to leave the bus alone:
    /// the CPU then reads the last value seen on the bus, as with nothing plugged in.
    /// </summary>
    public virtual byte? ReadIo(ushort address) => null;

    /// <summary>
    /// The same read for debuggers and memory viewers: it must not change anything. The default shows open bus;
    /// override it when reads have side effects that <see cref="ReadIo"/> performs, or to show the registers.
    /// </summary>
    public virtual byte? PeekIo(ushort address) => null;

    /// <summary>A CPU write to I/O 1 or I/O 2 (<paramref name="address"/> is $DE00-$DFFF).</summary>
    public virtual void WriteIo(ushort address, byte value) { }

    /// <summary>
    /// The machine's reset line: called when the machine resets, before its memory map is rebuilt.
    /// <paramref name="hard"/> is true for a power cycle, false for the reset button.
    /// </summary>
    public virtual void Reset(bool hard) { }

    /// <summary>Called by the memory map the device is plugged into, to hear about line and window changes.</summary>
    internal void Connect(Action changed) => _changed += changed;

    internal void Disconnect(Action changed) => _changed -= changed;
}
