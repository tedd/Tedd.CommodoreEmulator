using System;
using System.IO;
using System.Text;

namespace Tedd.Commodore.Emulator.C64;

/// <summary>
/// A cartridge plugged into the expansion port: ROML (8K at $8000), optional ROMH (8K at $A000, or $E000 in
/// Ultimax mode) and the EXROM/GAME lines. Supports raw 4K/8K/16K images and the .CRT container (type 0,
/// "normal cartridge" only).
/// </summary>
public sealed class Cartridge
{
    public const int RomSize = 8192;

    /// <summary>8K ROM at $8000-$9FFF.</summary>
    public byte[] RomL { get; }
    /// <summary>8K ROM at $A000-$BFFF (16K cartridges) or $E000-$FFFF (Ultimax), or null.</summary>
    public byte[]? RomH { get; }
    /// <summary>EXROM line level: true = high (inactive).</summary>
    public bool Exrom { get; }
    /// <summary>GAME line level: true = high (inactive).</summary>
    public bool Game { get; }
    public string Name { get; }

    public bool IsUltimax => Exrom && !Game;

    public Cartridge(byte[] romL, byte[]? romH, bool exrom, bool game, string name)
    {
        if (romL.Length != RomSize) throw new ArgumentException("ROML must be 8192 bytes", nameof(romL));
        if (romH is not null && romH.Length != RomSize) throw new ArgumentException("ROMH must be 8192 bytes", nameof(romH));
        RomL = romL;
        RomH = romH;
        Exrom = exrom;
        Game = game;
        Name = name;
    }

    /// <summary>Loads a raw binary (4K/8K/16K) or a .CRT file depending on its content.</summary>
    public static Cartridge Load(string path)
    {
        var data = File.ReadAllBytes(path);
        return IsCrt(data) ? FromCrt(data, Path.GetFileNameWithoutExtension(path)) : FromRaw(data, Path.GetFileNameWithoutExtension(path));
    }

    public static bool IsCrt(ReadOnlySpan<byte> data) =>
        data.Length >= 0x40 && Encoding.ASCII.GetString(data[..16]) == "C64 CARTRIDGE   ";

    /// <summary>
    /// Raw ROM image: 4K (padded/mirrored to 8K), 8K (ROML, EXROM low, GAME high) or 16K (ROML + ROMH, both low).
    /// </summary>
    public static Cartridge FromRaw(byte[] data, string name = "cartridge")
    {
        switch (data.Length)
        {
            case 4096:
            {
                var romL = new byte[RomSize];
                data.CopyTo(romL, 0);
                data.CopyTo(romL, 4096);
                return new Cartridge(romL, null, exrom: false, game: true, name);
            }
            case 8192:
                return new Cartridge(data, null, exrom: false, game: true, name);
            case 16384:
                return new Cartridge(data[..RomSize], data[RomSize..], exrom: false, game: false, name);
            default:
                throw new InvalidDataException($"Unsupported raw cartridge size {data.Length}; expected 4096, 8192 or 16384 bytes");
        }
    }

    /// <summary>Parses a .CRT container (VICE format, type 0 only).</summary>
    public static Cartridge FromCrt(byte[] data, string fallbackName = "cartridge")
    {
        if (!IsCrt(data)) throw new InvalidDataException("Not a CRT file");
        int headerLength = ReadBE32(data, 0x10);
        int type = (data[0x16] << 8) | data[0x17];
        if (type != 0)
            throw new NotSupportedException($"CRT hardware type {type} is not supported (only type 0, normal cartridges)");
        bool exrom = data[0x18] != 0;
        bool game = data[0x19] != 0;
        var name = Encoding.ASCII.GetString(data, 0x20, 32).TrimEnd('\0', ' ');
        if (name.Length == 0) name = fallbackName;

        byte[]? romL = null, romH = null;
        int pos = headerLength;
        while (pos + 0x10 <= data.Length)
        {
            if (Encoding.ASCII.GetString(data, pos, 4) != "CHIP")
                break;
            int packetLength = ReadBE32(data, pos + 4);
            int loadAddress = (data[pos + 0xC] << 8) | data[pos + 0xD];
            int size = (data[pos + 0xE] << 8) | data[pos + 0xF];
            var chip = new byte[size];
            Array.Copy(data, pos + 0x10, chip, 0, Math.Min(size, data.Length - pos - 0x10));
            if (size == 16384 && loadAddress == 0x8000)
            {
                romL = chip[..RomSize];
                romH = chip[RomSize..];
            }
            else if (loadAddress == 0x8000)
                romL = Pad(chip);
            else if (loadAddress == 0xA000 || loadAddress == 0xE000)
                romH = Pad(chip);
            pos += packetLength;
        }
        if (romL is null && romH is null)
            throw new InvalidDataException("CRT file contains no CHIP packets");
        romL ??= new byte[RomSize];
        return new Cartridge(romL, romH, exrom, game, name);
    }

    private static byte[] Pad(byte[] chip)
    {
        if (chip.Length == RomSize) return chip;
        var r = new byte[RomSize];
        for (int i = 0; i < RomSize; i++)
            r[i] = chip[i % chip.Length];
        return r;
    }

    private static int ReadBE32(byte[] d, int o) => (d[o] << 24) | (d[o + 1] << 16) | (d[o + 2] << 8) | d[o + 3];
}
