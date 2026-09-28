using System;
using System.Collections.Generic;
using System.IO;

namespace Tedd.Commodore.Emulator.C64;

/// <summary>
/// The ROM images needed to run a C64 (and optionally a 1541 drive), plus logic to find them on disk.
/// ROM images are not part of the repository; they are looked up in a ROM directory (see <see cref="Locate"/>).
/// </summary>
public sealed class RomSet
{
    public const int BasicSize = 8192;
    public const int KernalSize = 8192;
    public const int CharSize = 4096;
    public const int DriveRomSize = 16384;

    /// <summary>BASIC V2 ROM, mapped at $A000-$BFFF.</summary>
    public byte[] Basic { get; }
    /// <summary>KERNAL ROM, mapped at $E000-$FFFF.</summary>
    public byte[] Kernal { get; }
    /// <summary>Character generator ROM, mapped at $D000-$DFFF (CPU) and $1000/$9000 (VIC).</summary>
    public byte[] Char { get; }
    /// <summary>1541 DOS ROM ($C000-$FFFF), or null when no drive ROM was found.</summary>
    public byte[]? Drive1541 { get; }
    /// <summary>Directory the ROMs were loaded from.</summary>
    public string Directory { get; }
    /// <summary>Human readable description of which files were used.</summary>
    public string Description { get; }

    public RomSet(byte[] basic, byte[] kernal, byte[] chargen, byte[]? drive1541, string directory, string description)
    {
        if (basic.Length != BasicSize) throw new ArgumentException($"BASIC ROM must be {BasicSize} bytes", nameof(basic));
        if (kernal.Length != KernalSize) throw new ArgumentException($"KERNAL ROM must be {KernalSize} bytes", nameof(kernal));
        if (chargen.Length != CharSize) throw new ArgumentException($"Character ROM must be {CharSize} bytes", nameof(chargen));
        if (drive1541 is not null && drive1541.Length != DriveRomSize) throw new ArgumentException($"1541 ROM must be {DriveRomSize} bytes", nameof(drive1541));
        Basic = basic;
        Kernal = kernal;
        Char = chargen;
        Drive1541 = drive1541;
        Directory = directory;
        Description = description;
    }

    /// <summary>
    /// Overwrites the character generator image in place. <see cref="Char"/> is the same array the machine's
    /// memory map reads through, so a running C64 draws the new glyphs from the next character fetch on; nothing
    /// is written to disk. Used by the GUI's character set viewer to try a different character set live.
    /// </summary>
    /// <param name="data">The replacement glyphs: 4096 bytes for both sets, 2048 for the one at <paramref name="offset"/>.</param>
    /// <param name="offset">Where in the 4096 byte image to write (0 = set 1, 2048 = set 2).</param>
    public void ReplaceChar(ReadOnlySpan<byte> data, int offset = 0)
    {
        if (offset < 0 || offset > CharSize) throw new ArgumentOutOfRangeException(nameof(offset));
        if (data.Length + offset > CharSize)
            throw new ArgumentException($"{data.Length} bytes at offset {offset} do not fit in a {CharSize} byte character ROM", nameof(data));
        data.CopyTo(Char.AsSpan(offset));
    }

    /// <summary>Name of the environment variable that can point at the ROM directory.</summary>
    public const string EnvironmentVariable = "C64_ROMS";

    private static readonly string[] BasicNames = { "basic.901226-01.bin", "basic.bin", "basic" };
    private static readonly string[] KernalNames = { "kernal.901227-03.bin", "kernal.901227-02.bin", "kernal.901227-01.bin", "kernal.bin", "kernal" };
    private static readonly string[] CombinedNames = { "64c.251913-01.bin", "basic+kernal.bin" };
    private static readonly string[] CharNames = { "characters.901225-01.bin", "chargen.bin", "chargen", "characters.325018-02.bin", "characters.901225-01-DK.bin" };
    private static readonly string[] Drive16KNames = { "1541-II.251968-03.bin", "1541-II.355640-01.bin", "1541C.251968-02.bin", "1541C.251968-01.bin", "dos1541", "d1541II", "dos1541II", "1541.bin" };
    private static readonly string[] DriveLowNames = { "1541-c000.325302-01.bin", "1540-c000.325302-01.bin" };
    private static readonly string[] DriveHighNames = { "1541-e000.901229-05.bin", "1541-e000.901229-06AA.bin", "1541-e000.901229-03.bin", "1541-e000.901229-04.bin", "1541-e000.901229-02.bin", "1541-e000.901229-01.bin", "1540-e000.325303-01.bin" };

    /// <summary>
    /// Finds a directory containing the C64 ROMs. Search order: the <c>C64_ROMS</c> environment variable,
    /// the application base directory, the current directory, then <c>roms</c>, <c>src/Tedd.Commodore.GUI</c> and
    /// <c>Tedd.Commodore.GUI</c> sub-directories of every ancestor of those (so that tests find the
    /// repository layout).
    /// </summary>
    public static string? Locate()
    {
        var candidates = new List<string>();
        var env = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(env))
            candidates.Add(env);
        candidates.Add(AppContext.BaseDirectory);
        candidates.Add(System.IO.Directory.GetCurrentDirectory());

        foreach (var start in candidates)
        {
            if (HasC64Roms(start))
                return Path.GetFullPath(start);
            var dir = new DirectoryInfo(start);
            for (int depth = 0; dir is not null && depth < 8; depth++, dir = dir.Parent)
            {
                foreach (var sub in new[] { "roms", Path.Combine("src", "Tedd.Commodore.GUI"), "Tedd.Commodore.GUI" })
                {
                    var p = Path.Combine(dir.FullName, sub);
                    if (HasC64Roms(p))
                        return Path.GetFullPath(p);
                }
            }
        }
        return null;
    }

    private static bool HasC64Roms(string dir)
    {
        if (!System.IO.Directory.Exists(dir)) return false;
        bool basic = FindFile(dir, BasicNames) is not null || FindFile(dir, CombinedNames) is not null;
        bool kernal = FindFile(dir, KernalNames) is not null || FindFile(dir, CombinedNames) is not null;
        bool chr = FindFile(dir, CharNames) is not null || FindGlob(dir, "characters*.bin", CharSize) is not null;
        return basic && kernal && chr;
    }

    private static string? FindFile(string dir, string[] names)
    {
        foreach (var n in names)
        {
            var p = Path.Combine(dir, n);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    private static string? FindGlob(string dir, string pattern, int size)
    {
        foreach (var f in System.IO.Directory.GetFiles(dir, pattern))
            if (new FileInfo(f).Length == size)
                return f;
        return null;
    }

    /// <summary>Loads the ROM set from the located directory, or returns null if no ROMs can be found.</summary>
    public static RomSet? TryLoadDefault()
    {
        var dir = Locate();
        return dir is null ? null : Load(dir);
    }

    /// <summary>Loads the ROM set from the given directory.</summary>
    public static RomSet Load(string directory)
    {
        var desc = new List<string>();
        byte[]? basic = null, kernal = null;

        var basicFile = FindFile(directory, BasicNames);
        if (basicFile is not null)
        {
            basic = ReadExact(basicFile, BasicSize);
            desc.Add("BASIC=" + Path.GetFileName(basicFile));
        }
        var kernalFile = FindFile(directory, KernalNames);
        if (kernalFile is not null)
        {
            kernal = ReadExact(kernalFile, KernalSize);
            desc.Add("KERNAL=" + Path.GetFileName(kernalFile));
        }
        if (basic is null || kernal is null)
        {
            var combined = FindFile(directory, CombinedNames) ?? throw new FileNotFoundException("No BASIC/KERNAL ROM found in " + directory);
            var data = File.ReadAllBytes(combined);
            if (data.Length != BasicSize + KernalSize)
                throw new InvalidDataException($"{combined} should be {BasicSize + KernalSize} bytes (BASIC + KERNAL)");
            if (basic is null)
            {
                basic = data[..BasicSize];
                desc.Add("BASIC=" + Path.GetFileName(combined) + "[0..8K]");
            }
            if (kernal is null)
            {
                kernal = data[BasicSize..];
                desc.Add("KERNAL=" + Path.GetFileName(combined) + "[8K..16K]");
            }
        }

        var charFile = FindFile(directory, CharNames) ?? FindGlob(directory, "characters*.bin", CharSize)
            ?? throw new FileNotFoundException("No character ROM found in " + directory);
        var chargen = ReadExact(charFile, CharSize);
        desc.Add("CHAR=" + Path.GetFileName(charFile));

        byte[]? drive = null;
        var drive16 = FindFile(directory, Drive16KNames);
        if (drive16 is not null)
        {
            drive = ReadExact(drive16, DriveRomSize);
            desc.Add("1541=" + Path.GetFileName(drive16));
        }
        else
        {
            var lo = FindFile(directory, DriveLowNames);
            var hi = FindFile(directory, DriveHighNames);
            if (lo is not null && hi is not null)
            {
                drive = new byte[DriveRomSize];
                ReadExact(lo, 8192).CopyTo(drive, 0);
                ReadExact(hi, 8192).CopyTo(drive, 8192);
                desc.Add("1541=" + Path.GetFileName(lo) + "+" + Path.GetFileName(hi));
            }
        }

        return new RomSet(basic, kernal, chargen, drive, Path.GetFullPath(directory), string.Join(", ", desc));
    }

    private static byte[] ReadExact(string path, int size)
    {
        var data = File.ReadAllBytes(path);
        if (data.Length != size)
            throw new InvalidDataException($"{path} is {data.Length} bytes, expected {size}");
        return data;
    }
}
