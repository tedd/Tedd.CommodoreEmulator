using System;
using System.Collections.Generic;
using Tedd.Commodore.Emulator.Bus;

namespace Tedd.Commodore.Emulator.Tools;

/// <summary>
/// One contiguous block of assembled output, starting at <see cref="Address"/>.
/// </summary>
public readonly record struct AssemblySegment(ushort Address, byte[] Bytes)
{
    /// <summary>Exclusive end address of the segment (may be 0x10000).</summary>
    public int End => Address + Bytes.Length;
}

/// <summary>
/// Output of <see cref="Assembler.Assemble"/>.
/// </summary>
public sealed class AssemblyResult
{
    /// <summary>
    /// Address of <c>Bytes[0]</c>: the lowest address any byte was emitted at (or the default/first origin
    /// when the source emitted nothing).
    /// </summary>
    public ushort Origin { get; }

    /// <summary>
    /// Contiguous memory image from <see cref="Origin"/> to the highest emitted address. When the source uses
    /// several origins the gaps between segments are zero-filled here; <see cref="CopyTo(byte[])"/> only copies
    /// the bytes that were actually emitted.
    /// </summary>
    public byte[] Bytes { get; }

    /// <summary>All labels and symbols (case-sensitive names) with their values truncated to 16 bits.</summary>
    public IReadOnlyDictionary<string, ushort> Labels { get; }

    /// <summary>Full-precision symbol values (assignments may exceed 16 bits or be negative).</summary>
    public IReadOnlyDictionary<string, long> Symbols { get; }

    /// <summary>The emitted blocks in source order (one per origin that produced output).</summary>
    public IReadOnlyList<AssemblySegment> Segments { get; }

    /// <summary>Address of the first byte emitted in source order (the first origin that produced output).</summary>
    public ushort StartAddress { get; }

    /// <summary>Exclusive end address of <see cref="Bytes"/> (Origin + Bytes.Length).</summary>
    public int EndAddress => Origin + Bytes.Length;

    internal AssemblyResult(ushort origin, byte[] bytes, ushort startAddress,
        IReadOnlyDictionary<string, ushort> labels, IReadOnlyDictionary<string, long> symbols,
        IReadOnlyList<AssemblySegment> segments)
    {
        Origin = origin;
        Bytes = bytes;
        StartAddress = startAddress;
        Labels = labels;
        Symbols = symbols;
        Segments = segments;
    }

    /// <summary>
    /// Copies every emitted segment into <paramref name="memory"/> (a flat 64 KiB image). Bytes in the gaps
    /// between segments are left untouched.
    /// </summary>
    public void CopyTo(byte[] memory)
    {
        if (memory is null) throw new ArgumentNullException(nameof(memory));
        foreach (var segment in Segments)
        {
            if (segment.End > memory.Length)
                throw new ArgumentException($"Segment at ${segment.Address:X4} ({segment.Bytes.Length} bytes) does not fit in a {memory.Length} byte memory.", nameof(memory));
            Array.Copy(segment.Bytes, 0, memory, segment.Address, segment.Bytes.Length);
        }
    }

    /// <summary>Writes every emitted byte through <paramref name="write"/> in ascending address order per segment.</summary>
    public void CopyTo(Action<ushort, byte> write)
    {
        if (write is null) throw new ArgumentNullException(nameof(write));
        foreach (var segment in Segments)
            for (int i = 0; i < segment.Bytes.Length; i++)
                write((ushort)(segment.Address + i), segment.Bytes[i]);
    }

    /// <summary>Writes every emitted byte to a bus (one <see cref="IBus.Write"/> per byte).</summary>
    public void CopyTo(IBus bus)
    {
        if (bus is null) throw new ArgumentNullException(nameof(bus));
        CopyTo(bus.Write);
    }
}
