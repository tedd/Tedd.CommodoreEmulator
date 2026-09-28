using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Tedd.Commodore.Emulator.Media;

/// <summary>Type of a T64 directory entry (byte 0 of the 32 byte entry).</summary>
public enum T64EntryType : byte
{
    /// <summary>Unused slot (skipped).</summary>
    Free = 0,
    /// <summary>Normal tape file (a plain memory image, by far the most common).</summary>
    NormalTapeFile = 1,
    /// <summary>Tape file with header (C64S specific).</summary>
    TapeFileWithHeader = 2,
    /// <summary>Memory snapshot (C64S v0.9 specific, obsolete).</summary>
    MemorySnapshot = 3,
    /// <summary>Tape block (C64S specific).</summary>
    TapeBlock = 4,
    /// <summary>Digitized stream (C64S specific).</summary>
    DigitizedStream = 5,
}

/// <summary>One file stored inside a <see cref="T64Image"/>.</summary>
public sealed class T64Entry
{
    /// <summary>File name with the trailing $20 padding removed (decoded as PETSCII in the power-on character set).</summary>
    public string Name { get; }

    /// <summary>Raw 16 byte PETSCII name as stored in the entry.</summary>
    public byte[] RawName { get; }

    /// <summary>Entry type (byte 0 of the entry).</summary>
    public T64EntryType EntryType { get; }

    /// <summary>1541 file type byte (byte 1 of the entry): $82 = PRG, $81 = SEQ, $80 = DEL, $83 = USR, $84 = REL; the closed bit ($80) is usually set.</summary>
    public byte FileType { get; }

    /// <summary>C64 start (load) address (bytes 2-3, little-endian).</summary>
    public ushort StartAddress { get; }

    /// <summary>End address as written in the entry (bytes 4-5, exclusive). Often wrong, see <see cref="EndAddress"/>.</summary>
    public ushort DeclaredEndAddress { get; }

    /// <summary>
    /// Effective end address (exclusive) = <see cref="StartAddress"/> + <see cref="Data"/>.Length. Equals
    /// <see cref="DeclaredEndAddress"/> unless the declared value was wrong and had to be clamped to the data
    /// actually present in the container. Kept as an <see cref="int"/> because a file may legitimately end at $10000.
    /// </summary>
    public int EndAddress => StartAddress + Data.Length;

    /// <summary>Offset of the file data inside the container (bytes 8-11, little-endian).</summary>
    public int DataOffset { get; }

    /// <summary>The file contents (without load address).</summary>
    public byte[] Data { get; }

    /// <summary>True when <see cref="DeclaredEndAddress"/> did not match the data present and the size was derived from the container instead.</summary>
    public bool EndAddressWasCorrected { get; }

    /// <summary>True for entries whose file type is PRG (low nibble of <see cref="FileType"/> = 2) - the only kind that can be loaded as a program.</summary>
    public bool IsProgram => (FileType & 0x07) == 0x02;

    internal T64Entry(string name, byte[] rawName, T64EntryType entryType, byte fileType, ushort startAddress,
        ushort declaredEndAddress, int dataOffset, byte[] data, bool endAddressWasCorrected)
    {
        Name = name;
        RawName = rawName;
        EntryType = entryType;
        FileType = fileType;
        StartAddress = startAddress;
        DeclaredEndAddress = declaredEndAddress;
        DataOffset = dataOffset;
        Data = data;
        EndAddressWasCorrected = endAddressWasCorrected;
    }

    /// <summary>Returns the entry as a <see cref="PrgFile"/> (load address = <see cref="StartAddress"/>). The data array is shared, not copied.</summary>
    public PrgFile ToProgram() => new(StartAddress, Data);

    public override string ToString() => $"\"{Name}\" {(IsProgram ? "PRG" : $"type ${FileType:X2}")} ${StartAddress:X4}-${EndAddress:X4} ({Data.Length} bytes)";
}

/// <summary>
/// Parser for the T64 tape container used by the C64S emulator (Miha Peternel) and since adopted by every
/// C64 emulator. Despite its name it is not a tape recording: it is a small archive of memory images.
///
/// Layout (from the C64S documentation and the VICE "t64" format description in vice/doc/html/formats):
/// <code>
///   Header, 64 bytes
///     $00  32  description: "C64 tape image file" or "C64S tape file" (NUL/space padded)
///     $20   2  version, little-endian: $0100 (old) or $0101 (new)
///     $22   2  maximum number of directory entries (size of the directory)
///     $24   2  number of used entries. Many files store 0 here; then non-empty entries must be counted.
///     $26   2  unused
///     $28  24  user description, PETSCII/ASCII padded with $20
///   Directory, "maximum entries" * 32 bytes
///     $00   1  entry type: 0 free, 1 normal tape file, 2 tape file with header, 3 memory snapshot,
///              4 tape block, 5 digitized stream
///     $01   1  1541 file type: $82 PRG, $81 SEQ ...
///     $02   2  start address (little-endian)
///     $04   2  end address (little-endian, exclusive)
///     $06   2  unused
///     $08   4  offset of the file data from the beginning of the container (little-endian)
///     $0C   4  unused
///     $10  16  file name, PETSCII, padded with $20
///   File data
/// </code>
/// Known bug worked around here: the tool that created most T64 files in circulation wrote a bogus end
/// address (very often $C3C6) so <c>end - start</c> is larger than the data that is actually in the file. When
/// that happens (or the end address is below the start address) the size is clamped to the bytes available
/// between this entry's offset and the next entry's offset (or the end of the file), which is what VICE does.
/// </summary>
public sealed class T64Image
{
    /// <summary>Size of the fixed header.</summary>
    public const int HeaderSize = 64;

    /// <summary>Size of one directory entry.</summary>
    public const int EntrySize = 32;

    private static readonly string[] Signatures = { "C64 tape image file", "C64S tape file", "C64S tape image file" };

    /// <summary>Description string from the header (e.g. "C64 tape image file"), padding removed.</summary>
    public string Description { get; }

    /// <summary>User description (24 bytes at $28), trimmed; usually the tape/game name.</summary>
    public string UserDescription { get; }

    /// <summary>Format version from the header ($0100 or $0101).</summary>
    public ushort Version { get; }

    /// <summary>Directory size as written in the header.</summary>
    public int MaxEntries { get; }

    /// <summary>Used entry count as written in the header (0 in many files; <see cref="Entries"/> is authoritative).</summary>
    public int DeclaredUsedEntries { get; }

    /// <summary>The non-empty directory entries in directory order.</summary>
    public IReadOnlyList<T64Entry> Entries { get; }

    private T64Image(string description, string userDescription, ushort version, int maxEntries, int declaredUsedEntries, IReadOnlyList<T64Entry> entries)
    {
        Description = description;
        UserDescription = userDescription;
        Version = version;
        MaxEntries = maxEntries;
        DeclaredUsedEntries = declaredUsedEntries;
        Entries = entries;
    }

    /// <summary>Returns true when the buffer starts with one of the known T64 signatures and is large enough to hold a header.</summary>
    public static bool IsT64(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < HeaderSize)
            return false;
        foreach (var signature in Signatures)
        {
            if (StartsWithAscii(bytes, signature))
                return true;
        }
        return false;
    }

    /// <summary>Loads and parses a T64 file from disk.</summary>
    public static T64Image Load(string path)
    {
        if (path is null) throw new ArgumentNullException(nameof(path));
        return Load(File.ReadAllBytes(path));
    }

    /// <summary>Parses a T64 container held in memory. Throws <see cref="FormatException"/> if the signature is missing.</summary>
    public static T64Image Load(ReadOnlySpan<byte> bytes)
    {
        if (!IsT64(bytes))
            throw new FormatException("Not a T64 tape image: signature \"C64 tape image file\" / \"C64S tape file\" not found.");

        var description = DecodeHeaderText(bytes.Slice(0, 32));
        var version = ReadU16(bytes, 0x20);
        int maxEntries = ReadU16(bytes, 0x22);
        int usedEntries = ReadU16(bytes, 0x24);
        var userDescription = Petscii.PetsciiToAscii(TrimPadding(bytes.Slice(0x28, 24)));

        // Directory. Never read past the end of a truncated file: only the entries that fit are considered.
        int directoryCapacity = Math.Max(0, (bytes.Length - HeaderSize) / EntrySize);
        int entryCount = Math.Min(maxEntries, directoryCapacity);

        // First pass: collect offsets of all used entries so each entry's data can be bounded by the next one.
        var offsets = new List<int>(entryCount);
        for (int i = 0; i < entryCount; i++)
        {
            int e = HeaderSize + i * EntrySize;
            if (bytes[e] == (byte)T64EntryType.Free)
                continue;
            offsets.Add((int)Math.Min(ReadU32(bytes, e + 8), (uint)bytes.Length));
        }
        offsets.Sort();

        var entries = new List<T64Entry>(offsets.Count);
        for (int i = 0; i < entryCount; i++)
        {
            int e = HeaderSize + i * EntrySize;
            var entryType = (T64EntryType)bytes[e];
            if (entryType == T64EntryType.Free)
                continue;

            var fileType = bytes[e + 1];
            var start = ReadU16(bytes, e + 2);
            var declaredEnd = ReadU16(bytes, e + 4);
            uint rawOffset = ReadU32(bytes, e + 8);
            int offset = (int)Math.Min(rawOffset, (uint)bytes.Length);
            var rawName = bytes.Slice(e + 16, 16).ToArray();
            var name = Petscii.PetsciiToAscii(TrimPadding(rawName), PetsciiCharset.UppercaseGraphics);

            // Data runs at most until the next entry's data (or the end of the file).
            int limit = bytes.Length;
            foreach (var other in offsets)
            {
                if (other > offset)
                {
                    limit = other;
                    break;
                }
            }
            int available = Math.Max(0, limit - offset);

            int declaredSize = declaredEnd - start;
            bool corrected;
            int size;
            if (declaredSize <= 0 || declaredSize > available)
            {
                // The known T64 end-address bug: clamp to what is really in the container.
                size = available;
                corrected = declaredSize != available;
            }
            else
            {
                size = declaredSize;
                corrected = false;
            }

            var data = bytes.Slice(offset, size).ToArray();
            entries.Add(new T64Entry(name, rawName, entryType, fileType, start, declaredEnd, offset, data, corrected));
        }

        return new T64Image(description, userDescription, version, maxEntries, usedEntries, entries.AsReadOnly());
    }

    /// <summary>Returns entry <paramref name="index"/> (index into <see cref="Entries"/>) as a <see cref="PrgFile"/>.</summary>
    public PrgFile GetProgram(int index)
    {
        if ((uint)index >= (uint)Entries.Count)
            throw new ArgumentOutOfRangeException(nameof(index), index, $"The image has {Entries.Count} entries.");
        return Entries[index].ToProgram();
    }

    /// <summary>Returns the first PRG entry as a <see cref="PrgFile"/>, or null if the image holds no program (what "LOAD" from tape would fetch).</summary>
    public PrgFile? GetFirstProgram()
    {
        foreach (var entry in Entries)
        {
            if (entry.IsProgram)
                return entry.ToProgram();
        }
        return null;
    }

    private static bool StartsWithAscii(ReadOnlySpan<byte> bytes, string text)
    {
        if (bytes.Length < text.Length)
            return false;
        for (int i = 0; i < text.Length; i++)
        {
            if (bytes[i] != (byte)text[i])
                return false;
        }
        return true;
    }

    private static string DecodeHeaderText(ReadOnlySpan<byte> bytes)
    {
        int length = bytes.IndexOf((byte)0);
        if (length < 0) length = bytes.Length;
        return Encoding.ASCII.GetString(bytes.Slice(0, length).ToArray()).TrimEnd(' ');
    }

    private static ReadOnlySpan<byte> TrimPadding(ReadOnlySpan<byte> bytes)
    {
        int end = bytes.Length;
        while (end > 0 && (bytes[end - 1] == 0x20 || bytes[end - 1] == 0x00 || bytes[end - 1] == 0xA0))
            end--;
        return bytes.Slice(0, end);
    }

    private static ushort ReadU16(ReadOnlySpan<byte> bytes, int offset) => (ushort)(bytes[offset] | (bytes[offset + 1] << 8));

    private static uint ReadU32(ReadOnlySpan<byte> bytes, int offset)
        => (uint)(bytes[offset] | (bytes[offset + 1] << 8) | (bytes[offset + 2] << 16) | (bytes[offset + 3] << 24));

    public override string ToString() => $"{Description} \"{UserDescription}\" ({Entries.Count} entries)";
}
