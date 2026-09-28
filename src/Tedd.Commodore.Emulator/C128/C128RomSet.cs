using System;
using System.Collections.Generic;
using System.IO;
using Tedd.Commodore.Emulator.C64;

namespace Tedd.Commodore.Emulator.C128;

/// <summary>
/// The ROM images of a Commodore 128: BASIC 7 low ($4000-$7FFF) and high ($8000-$BFFF), the 16K "KERNAL" chip
/// that holds the screen editor ($C000), the Z80 BIOS ($D000) and the KERNAL proper ($E000), the 8K character
/// generator (C64 set in the lower half, C128 set in the upper half), the C64 mode BASIC and KERNAL, and
/// optionally the 1541 DOS. The files are looked up under the zimmers.net names, the VICE names and a few
/// generic ones, and the combined 32K images of the C128DCR (318022 = BASIC, 318023 = C64 ROMs + C128 KERNAL)
/// are understood too.
/// </summary>
public sealed class C128RomSet
{
    public const int BasicHalfSize = 16384;
    public const int KernalSize = 16384;
    public const int CharSize = 8192;
    /// <summary>Offset of the Z80 BIOS inside <see cref="Kernal"/> (it is the $D000-$DFFF quarter).</summary>
    public const int Z80BiosOffset = 0x1000;
    /// <summary>Offset of the C128 character set inside <see cref="Char"/>.</summary>
    public const int C128CharOffset = 0x1000;

    /// <summary>BASIC 7 low half, mapped at $4000-$7FFF.</summary>
    public byte[] BasicLow { get; }
    /// <summary>BASIC 7 high half, mapped at $8000-$BFFF.</summary>
    public byte[] BasicHigh { get; }
    /// <summary>Editor + Z80 BIOS + KERNAL, mapped at $C000-$FFFF (the BIOS quarter at $D000 is also what the Z80 sees at $0000).</summary>
    public byte[] Kernal { get; }
    /// <summary>8K character generator: C64 set at 0, C128 set at $1000.</summary>
    public byte[] Char { get; }
    /// <summary>C64 mode BASIC ($A000-$BFFF).</summary>
    public byte[] C64Basic { get; }
    /// <summary>C64 mode KERNAL ($E000-$FFFF).</summary>
    public byte[] C64Kernal { get; }
    /// <summary>1541 DOS ROM, or null.</summary>
    public byte[]? Drive1541 { get; }
    public string Directory { get; }
    public string Description { get; }

    public C128RomSet(byte[] basicLow, byte[] basicHigh, byte[] kernal, byte[] chargen, byte[] c64Basic, byte[] c64Kernal, byte[]? drive1541, string directory, string description)
    {
        if (basicLow.Length != BasicHalfSize) throw new ArgumentException($"BASIC low ROM must be {BasicHalfSize} bytes", nameof(basicLow));
        if (basicHigh.Length != BasicHalfSize) throw new ArgumentException($"BASIC high ROM must be {BasicHalfSize} bytes", nameof(basicHigh));
        if (kernal.Length != KernalSize) throw new ArgumentException($"KERNAL ROM must be {KernalSize} bytes", nameof(kernal));
        if (chargen.Length != CharSize) throw new ArgumentException($"Character ROM must be {CharSize} bytes", nameof(chargen));
        if (c64Basic.Length != RomSet.BasicSize) throw new ArgumentException($"C64 BASIC ROM must be {RomSet.BasicSize} bytes", nameof(c64Basic));
        if (c64Kernal.Length != RomSet.KernalSize) throw new ArgumentException($"C64 KERNAL ROM must be {RomSet.KernalSize} bytes", nameof(c64Kernal));
        if (drive1541 is not null && drive1541.Length != RomSet.DriveRomSize) throw new ArgumentException($"1541 ROM must be {RomSet.DriveRomSize} bytes", nameof(drive1541));
        BasicLow = basicLow;
        BasicHigh = basicHigh;
        Kernal = kernal;
        Char = chargen;
        C64Basic = c64Basic;
        C64Kernal = c64Kernal;
        Drive1541 = drive1541;
        Directory = directory;
        Description = description;
    }

    /// <summary>The C64 mode ROMs as a <see cref="RomSet"/> (the C64 character set is the lower half of the 8K chargen).</summary>
    public RomSet ToC64RomSet() => new(C64Basic, C64Kernal, Char[..RomSet.CharSize], Drive1541, Directory, Description);

    /// <summary>Name of the environment variable that can point at the ROM directory (falls back to <see cref="RomSet.EnvironmentVariable"/>).</summary>
    public const string EnvironmentVariable = "C128_ROMS";

    private static readonly string[] BasicLowNames = { "basic-4000.318018-04.bin", "basic-4000.318018-03.bin", "basic-4000.318018-02.bin", "basiclo.bin", "basiclo", "basic128lo.bin" };
    private static readonly string[] BasicHighNames = { "basic-8000.318019-04.bin", "basic-8000.318019-03.bin", "basic-8000.318019-02.bin", "basichi.bin", "basichi", "basic128hi.bin" };
    private static readonly string[] BasicCombinedNames = { "basic.318022-02.bin", "basic.318022-01.bin", "basic.390393-01.bin", "basic.252343-03.bin", "basic128.bin", "basic128" };
    private static readonly string[] KernalNames = { "kernal.318020-05.bin", "kernal.318020-04.bin", "kernal.318020-03.bin", "kernal128.bin", "kernal128", "kernal.128.bin" };
    private static readonly string[] CompleteNames = { "complete.318023-02.bin", "complete.252343-04.bin", "complete128.bin" };
    private static readonly string[] CharNames = { "characters.390059-01.bin", "chargen128.bin", "chargen128", "characters.128.bin" };
    private static readonly string[] C64PartNames = { "c128_c64part.325182-01.bin", "kernal64.bin", "basic64+kernal64.bin" };

    /// <summary>
    /// Finds a directory with a complete C128 ROM set: <c>C128_ROMS</c>, then <c>C64_ROMS</c>, the application
    /// base directory, the current directory, and the <c>roms</c> / <c>src/Tedd.Commodore.GUI</c> /
    /// <c>Tedd.Commodore.GUI</c> sub-directories of their ancestors.
    /// </summary>
    public static string? Locate()
    {
        var candidates = new List<string>();
        foreach (var variable in new[] { EnvironmentVariable, RomSet.EnvironmentVariable })
        {
            var env = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrWhiteSpace(env))
                candidates.Add(env);
        }
        candidates.Add(AppContext.BaseDirectory);
        candidates.Add(System.IO.Directory.GetCurrentDirectory());

        foreach (var start in candidates)
        {
            if (HasRoms(start))
                return Path.GetFullPath(start);
            var dir = new DirectoryInfo(start);
            for (int depth = 0; dir is not null && depth < 8; depth++, dir = dir.Parent)
            {
                foreach (var sub in new[] { "roms", Path.Combine("src", "Tedd.Commodore.GUI"), "Tedd.Commodore.GUI" })
                {
                    var p = Path.Combine(dir.FullName, sub);
                    if (HasRoms(p))
                        return Path.GetFullPath(p);
                }
            }
        }
        return null;
    }

    /// <summary>True when <paramref name="dir"/> holds every image a C128 needs.</summary>
    public static bool HasRoms(string dir)
    {
        if (!System.IO.Directory.Exists(dir)) return false;
        bool basic = (Find(dir, BasicLowNames) is not null && Find(dir, BasicHighNames) is not null) || Find(dir, BasicCombinedNames) is not null;
        bool kernal = Find(dir, KernalNames) is not null || Find(dir, CompleteNames) is not null;
        bool chr = Find(dir, CharNames) is not null || FindGlob(dir, "characters*.bin", CharSize) is not null;
        bool c64 = Find(dir, CompleteNames) is not null || Find(dir, C64PartNames) is not null || HasC64Roms(dir);
        return basic && kernal && chr && c64;
    }

    private static bool HasC64Roms(string dir)
    {
        try
        {
            var set = RomSet.Load(dir);
            return set is not null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static C128RomSet? TryLoadDefault()
    {
        var dir = Locate();
        return dir is null ? null : Load(dir);
    }

    /// <summary>Loads the ROM set from <paramref name="directory"/>.</summary>
    public static C128RomSet Load(string directory)
    {
        var desc = new List<string>();
        byte[]? basicLo = null, basicHi = null, kernal = null, c64Basic = null, c64Kernal = null;

        var lo = Find(directory, BasicLowNames);
        var hi = Find(directory, BasicHighNames);
        if (lo is not null && hi is not null)
        {
            basicLo = ReadExact(lo, BasicHalfSize);
            basicHi = ReadExact(hi, BasicHalfSize);
            desc.Add("BASIC=" + Path.GetFileName(lo) + "+" + Path.GetFileName(hi));
        }
        else
        {
            var combined = Find(directory, BasicCombinedNames) ?? throw new FileNotFoundException("No C128 BASIC ROM found in " + directory);
            var data = ReadExact(combined, 2 * BasicHalfSize);
            basicLo = data[..BasicHalfSize];
            basicHi = data[BasicHalfSize..];
            desc.Add("BASIC=" + Path.GetFileName(combined));
        }

        var complete = Find(directory, CompleteNames);
        var kernalFile = Find(directory, KernalNames);
        if (kernalFile is not null)
        {
            kernal = ReadExact(kernalFile, KernalSize);
            desc.Add("KERNAL=" + Path.GetFileName(kernalFile));
        }
        else if (complete is not null)
        {
            kernal = ReadExact(complete, 2 * KernalSize)[KernalSize..];
            desc.Add("KERNAL=" + Path.GetFileName(complete) + "[16K..32K]");
        }
        else
        {
            throw new FileNotFoundException("No C128 KERNAL ROM found in " + directory);
        }

        var charFile = Find(directory, CharNames) ?? FindGlob(directory, "characters*.bin", CharSize)
            ?? throw new FileNotFoundException("No 8K C128 character ROM found in " + directory);
        var chargen = ReadExact(charFile, CharSize);
        desc.Add("CHAR=" + Path.GetFileName(charFile));

        var c64Part = Find(directory, C64PartNames);
        if (c64Part is not null)
        {
            var data = ReadExact(c64Part, RomSet.BasicSize + RomSet.KernalSize);
            c64Basic = data[..RomSet.BasicSize];
            c64Kernal = data[RomSet.BasicSize..];
            desc.Add("C64=" + Path.GetFileName(c64Part));
        }
        else if (complete is not null)
        {
            var data = ReadExact(complete, 2 * KernalSize);
            c64Basic = data[..RomSet.BasicSize];
            c64Kernal = data[RomSet.BasicSize..KernalSize];
            desc.Add("C64=" + Path.GetFileName(complete) + "[0..16K]");
        }

        byte[]? drive = null;
        try
        {
            var c64 = RomSet.Load(directory);
            c64Basic ??= c64.Basic;
            c64Kernal ??= c64.Kernal;
            if (c64Part is null && complete is null) desc.Add("C64=" + c64.Description);
            drive = c64.Drive1541;
        }
        catch (Exception) when (c64Basic is not null && c64Kernal is not null)
        {
            // The C64 ROMs came from the combined image; the drive ROM is optional.
            drive = null;
        }
        if (c64Basic is null || c64Kernal is null)
            throw new FileNotFoundException("No C64 mode BASIC/KERNAL ROM found in " + directory);
        if (drive is not null && !desc.Exists(d => d.StartsWith("1541=", StringComparison.Ordinal)))
            desc.Add("1541=present");

        return new C128RomSet(basicLo, basicHi, kernal, chargen, c64Basic, c64Kernal, drive, Path.GetFullPath(directory), string.Join(", ", desc));
    }

    private static string? Find(string dir, string[] names)
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

    private static byte[] ReadExact(string path, int size)
    {
        var data = File.ReadAllBytes(path);
        if (data.Length != size)
            throw new InvalidDataException($"{path} is {data.Length} bytes, expected {size}");
        return data;
    }
}
