using System;
using System.IO;

namespace Tedd.Commodore.Emulator.Media;

/// <summary>
/// A Commodore program file (".PRG"): a two byte little-endian load address followed by the raw memory
/// image. This is exactly what the KERNAL LOAD routine expects from a device (the first two bytes of a
/// PRG file on disk/tape are the start address, see "Commodore 64 Programmer's Reference Guide",
/// chapter "The KERNAL", LOAD) and what tools such as VICE and the 1541 file system exchange.
/// </summary>
public sealed class PrgFile
{
    /// <summary>Address the data is loaded to (the first two bytes of the file, little-endian).</summary>
    public ushort LoadAddress { get; set; }

    /// <summary>Program bytes (everything after the two byte load address). Never null.</summary>
    public byte[] Data { get; set; }

    /// <summary>Address of the first byte after the program (<see cref="LoadAddress"/> + length), as a 32-bit value so it does not wrap.</summary>
    public int EndAddress => LoadAddress + Data.Length;

    /// <summary>Creates an empty program with load address $0801 (start of BASIC on the C64).</summary>
    public PrgFile() : this(0x0801, Array.Empty<byte>())
    {
    }

    /// <summary>Creates a program from a load address and its bytes. The array is used as-is (not copied).</summary>
    public PrgFile(ushort loadAddress, byte[] data)
    {
        LoadAddress = loadAddress;
        Data = data ?? throw new ArgumentNullException(nameof(data));
    }

    /// <summary>Parses a PRG image (load address + data). Throws <see cref="FormatException"/> if shorter than two bytes.</summary>
    public static PrgFile FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 2)
            throw new FormatException("A PRG file must be at least two bytes long (the load address).");
        var loadAddress = (ushort)(bytes[0] | (bytes[1] << 8));
        return new PrgFile(loadAddress, bytes.Slice(2).ToArray());
    }

    /// <summary>Loads a PRG file from disk.</summary>
    public static PrgFile Load(string path)
    {
        if (path is null) throw new ArgumentNullException(nameof(path));
        return FromBytes(File.ReadAllBytes(path));
    }

    /// <summary>Serialises the program to the on-disk format (load address little-endian, then data).</summary>
    public byte[] ToBytes()
    {
        var result = new byte[Data.Length + 2];
        result[0] = (byte)(LoadAddress & 0xFF);
        result[1] = (byte)(LoadAddress >> 8);
        Data.CopyTo(result, 2);
        return result;
    }

    /// <summary>Writes the program to disk in PRG format (overwrites an existing file).</summary>
    public void Save(string path)
    {
        if (path is null) throw new ArgumentNullException(nameof(path));
        File.WriteAllBytes(path, ToBytes());
    }

    public override string ToString() => $"PRG ${LoadAddress:X4}-${EndAddress:X4} ({Data.Length} bytes)";
}
