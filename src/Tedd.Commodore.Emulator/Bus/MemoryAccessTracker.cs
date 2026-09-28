using System.Runtime.CompilerServices;

namespace Tedd.Commodore.Emulator.Bus;

/// <summary>
/// Records which addresses a CPU has read and which it has written since the bits were last taken: one bit per
/// address in two 8 KB bitmaps. A debugger or memory viewer attaches one to a CPU (<c>Cpu6502.AccessTracker</c>,
/// <c>Z80.AccessTracker</c>); with none attached the CPU pays one null check per bus cycle.
/// <para>
/// The consumer takes words with <c>Interlocked.Exchange(ref word, 0)</c> from its own thread while the CPU
/// keeps OR-ing bits in from the emulation thread, and that is deliberately not synchronised: the CPU's plain
/// read-modify-write can put back a word the consumer has just cleared, which reports those addresses once more
/// on the next poll, but an access is never lost and the emulation thread never waits or takes a locked
/// instruction. A consumer that must not see duplicates can take the words under the machine's lock instead.
/// </para>
/// </summary>
public sealed class MemoryAccessTracker
{
    /// <summary>Number of 64-bit words in each bitmap: 65536 addresses.</summary>
    public const int Words = 1024;

    /// <summary>Bit <c>address &amp; 63</c> of word <c>address &gt;&gt; 6</c> is set once the address has been read.</summary>
    public readonly ulong[] Reads = new ulong[Words];

    /// <summary>Same layout as <see cref="Reads"/>, for writes.</summary>
    public readonly ulong[] Writes = new ulong[Words];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Read(ushort address) => Reads[address >> 6] |= 1UL << (address & 63);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Write(ushort address) => Writes[address >> 6] |= 1UL << (address & 63);

    /// <summary>Forgets every recorded access.</summary>
    public void Clear()
    {
        System.Array.Clear(Reads, 0, Words);
        System.Array.Clear(Writes, 0, Words);
    }
}
