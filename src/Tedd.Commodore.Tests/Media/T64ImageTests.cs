using System;
using System.IO;
using NUnit.Framework;
using Tedd.Commodore.Emulator.Media;

namespace Tedd.Commodore.Tests.Media;

[TestFixture]
public class T64ImageTests
{
    private static string? FroggerPath()
    {
        // The test assembly is in src/Tedd.Commodore.Tests/bin/<cfg>/<tfm>/, images live in src/Tedd.Commodore.GUI/ (git-ignored).
        var dir = TestContext.CurrentContext.TestDirectory;
        for (int i = 0; i < 6 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, "Tedd.Commodore.GUI", "Frogger 64 (Europe).T64");
            if (File.Exists(candidate))
                return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    /// <summary>
    /// Builds a T64 container in memory. Each entry is (name, start, declaredEnd, data); the data is appended
    /// in order directly after the directory.
    /// </summary>
    private static byte[] BuildT64(string signature, ushort version, int maxEntries, int usedEntries, string userDescription,
        params (string Name, ushort Start, ushort DeclaredEnd, byte[] Data)[] files)
    {
        int dataStart = T64Image.HeaderSize + maxEntries * T64Image.EntrySize;
        int total = dataStart;
        foreach (var f in files) total += f.Data.Length;
        var bytes = new byte[total];

        for (int i = 0; i < signature.Length; i++) bytes[i] = (byte)signature[i];
        bytes[0x20] = (byte)(version & 0xFF); bytes[0x21] = (byte)(version >> 8);
        bytes[0x22] = (byte)(maxEntries & 0xFF); bytes[0x23] = (byte)(maxEntries >> 8);
        bytes[0x24] = (byte)(usedEntries & 0xFF); bytes[0x25] = (byte)(usedEntries >> 8);
        for (int i = 0; i < 24; i++) bytes[0x28 + i] = i < userDescription.Length ? (byte)userDescription[i] : (byte)0x20;

        int offset = dataStart;
        for (int i = 0; i < files.Length; i++)
        {
            var f = files[i];
            int e = T64Image.HeaderSize + i * T64Image.EntrySize;
            bytes[e] = 1;          // normal tape file
            bytes[e + 1] = 0x82;   // PRG
            bytes[e + 2] = (byte)(f.Start & 0xFF); bytes[e + 3] = (byte)(f.Start >> 8);
            bytes[e + 4] = (byte)(f.DeclaredEnd & 0xFF); bytes[e + 5] = (byte)(f.DeclaredEnd >> 8);
            bytes[e + 8] = (byte)(offset & 0xFF); bytes[e + 9] = (byte)((offset >> 8) & 0xFF);
            bytes[e + 10] = (byte)((offset >> 16) & 0xFF); bytes[e + 11] = (byte)((offset >> 24) & 0xFF);
            for (int n = 0; n < 16; n++) bytes[e + 16 + n] = n < f.Name.Length ? (byte)f.Name[n] : (byte)0x20;
            f.Data.CopyTo(bytes, offset);
            offset += f.Data.Length;
        }
        return bytes;
    }

    private static byte[] Pattern(int length, byte seed)
    {
        var data = new byte[length];
        for (int i = 0; i < length; i++) data[i] = (byte)(seed + i);
        return data;
    }

    [Test]
    public void Frogger_Header()
    {
        var path = FroggerPath();
        if (path is null) Assert.Ignore("Frogger 64 (Europe).T64 not present in src/Tedd.Commodore.GUI/");

        var image = T64Image.Load(path);
        Assert.That(image.Description, Does.StartWith("C64 tape image file"));
        Assert.That(image.Version, Is.EqualTo(0x0100));
        Assert.That(image.Entries, Has.Count.EqualTo(1));
    }

    [Test]
    public void Frogger_FirstEntry()
    {
        var path = FroggerPath();
        if (path is null) Assert.Ignore("Frogger 64 (Europe).T64 not present in src/Tedd.Commodore.GUI/");

        var image = T64Image.Load(path);
        var entry = image.Entries[0];
        Assert.That(entry.Name, Is.EqualTo("FROGGER 64+"));
        Assert.That(entry.EntryType, Is.EqualTo(T64EntryType.NormalTapeFile));
        Assert.That(entry.FileType, Is.EqualTo(0x82));
        Assert.That(entry.IsProgram, Is.True);
        Assert.That(entry.StartAddress, Is.EqualTo(0x0801));
        Assert.That(entry.EndAddress, Is.EqualTo(0x39BC));
        Assert.That(entry.DeclaredEndAddress, Is.EqualTo(0x39BC));
        Assert.That(entry.EndAddressWasCorrected, Is.False);
        Assert.That(entry.Data, Has.Length.EqualTo(0x39BC - 0x0801));

        var prg = image.GetProgram(0);
        Assert.That(prg.LoadAddress, Is.EqualTo(0x0801));
        Assert.That(prg.Data, Is.SameAs(entry.Data));
        // A BASIC program at $0801 starts with the link pointer to the next line; Frogger's stub is "239 SYS2061" so the link is $080B.
        Assert.That(prg.Data[0], Is.EqualTo(0x0B));
        Assert.That(prg.Data[1], Is.EqualTo(0x08));
    }

    [Test]
    public void Frogger_IsT64()
    {
        var path = FroggerPath();
        if (path is null) Assert.Ignore("Frogger 64 (Europe).T64 not present in src/Tedd.Commodore.GUI/");
        Assert.That(T64Image.IsT64(File.ReadAllBytes(path)), Is.True);
    }

    [Test]
    public void Synthetic_TwoEntries()
    {
        var a = Pattern(100, 0x10);
        var b = Pattern(300, 0x40);
        var bytes = BuildT64("C64 tape image file", 0x0101, 4, 2, "MY TAPE",
            ("FIRST", 0x0801, 0x0801 + 100, a),
            ("SECOND", 0xC000, 0xC000 + 300, b));

        var image = T64Image.Load(bytes);
        Assert.That(image.Description, Is.EqualTo("C64 tape image file"));
        Assert.That(image.UserDescription, Is.EqualTo("MY TAPE"));
        Assert.That(image.Version, Is.EqualTo(0x0101));
        Assert.That(image.MaxEntries, Is.EqualTo(4));
        Assert.That(image.DeclaredUsedEntries, Is.EqualTo(2));
        Assert.That(image.Entries, Has.Count.EqualTo(2));

        Assert.That(image.Entries[0].Name, Is.EqualTo("FIRST"));
        Assert.That(image.Entries[0].StartAddress, Is.EqualTo(0x0801));
        Assert.That(image.Entries[0].EndAddress, Is.EqualTo(0x0801 + 100));
        Assert.That(image.Entries[0].Data, Is.EqualTo(a));
        Assert.That(image.Entries[0].EndAddressWasCorrected, Is.False);

        Assert.That(image.Entries[1].Name, Is.EqualTo("SECOND"));
        Assert.That(image.Entries[1].StartAddress, Is.EqualTo(0xC000));
        Assert.That(image.Entries[1].Data, Is.EqualTo(b));
        Assert.That(image.GetProgram(1).LoadAddress, Is.EqualTo(0xC000));
    }

    [Test]
    public void Synthetic_WrongEndAddress_IsClampedToNextEntry()
    {
        var a = Pattern(100, 0x10);
        var b = Pattern(50, 0x40);
        // First entry claims to end at $C3C6 (the classic bug), which would be 48 069 bytes; only 100 are present.
        var bytes = BuildT64("C64 tape image file", 0x0100, 2, 2, "BUGGY",
            ("BROKEN", 0x0801, 0xC3C6, a),
            ("NEXT", 0x2000, 0x2000 + 50, b));

        var image = T64Image.Load(bytes);
        Assert.That(image.Entries[0].Data, Is.EqualTo(a));
        Assert.That(image.Entries[0].Data, Has.Length.EqualTo(100));
        Assert.That(image.Entries[0].EndAddress, Is.EqualTo(0x0801 + 100));
        Assert.That(image.Entries[0].DeclaredEndAddress, Is.EqualTo(0xC3C6));
        Assert.That(image.Entries[0].EndAddressWasCorrected, Is.True);
        Assert.That(image.Entries[1].Data, Is.EqualTo(b));
    }

    [Test]
    public void Synthetic_WrongEndAddress_LastEntryClampedToFileLength()
    {
        var a = Pattern(1234, 0x33);
        var bytes = BuildT64("C64S tape file", 0x0100, 1, 1, "X", ("ONLY", 0x0801, 0xC3C6, a));

        var image = T64Image.Load(bytes);
        Assert.That(image.Description, Is.EqualTo("C64S tape file"));
        Assert.That(image.Entries[0].Data, Is.EqualTo(a));
        Assert.That(image.Entries[0].EndAddress, Is.EqualTo(0x0801 + 1234));
        Assert.That(image.Entries[0].EndAddressWasCorrected, Is.True);
    }

    [Test]
    public void Synthetic_EndBelowStart_IsClamped()
    {
        var a = Pattern(64, 0x01);
        var bytes = BuildT64("C64 tape image file", 0x0100, 1, 1, "X", ("NEG", 0xD000, 0x0100, a));

        var image = T64Image.Load(bytes);
        Assert.That(image.Entries[0].Data, Is.EqualTo(a));
        Assert.That(image.Entries[0].EndAddressWasCorrected, Is.True);
    }

    [Test]
    public void Synthetic_UsedEntriesZero_CountsNonEmptyEntries()
    {
        var a = Pattern(10, 0x10);
        var b = Pattern(20, 0x20);
        var bytes = BuildT64("C64 tape image file", 0x0100, 3, 0, "ZERO", ("A", 0x1000, 0x100A, a), ("B", 0x2000, 0x2014, b));

        var image = T64Image.Load(bytes);
        Assert.That(image.DeclaredUsedEntries, Is.EqualTo(0));
        Assert.That(image.Entries, Has.Count.EqualTo(2));
        Assert.That(image.Entries[0].Name, Is.EqualTo("A"));
        Assert.That(image.Entries[1].Name, Is.EqualTo("B"));
    }

    [Test]
    public void Synthetic_FreeEntriesAreSkipped()
    {
        var a = Pattern(10, 0x10);
        var b = Pattern(20, 0x20);
        var c = Pattern(30, 0x30);
        var bytes = BuildT64("C64 tape image file", 0x0100, 3, 3, "GAP", ("A", 0x1000, 0x100A, a), ("MIDDLE", 0x2000, 0x2014, b), ("C", 0x3000, 0x301E, c));
        // Mark the middle directory slot as free; its data stays in the file, which must not confuse the others.
        bytes[T64Image.HeaderSize + T64Image.EntrySize] = 0;

        var image = T64Image.Load(bytes);
        Assert.That(image.Entries, Has.Count.EqualTo(2));
        Assert.That(image.Entries[0].Name, Is.EqualTo("A"));
        Assert.That(image.Entries[0].Data, Is.EqualTo(a));
        Assert.That(image.Entries[1].Name, Is.EqualTo("C"));
        Assert.That(image.Entries[1].Data, Is.EqualTo(c));
    }

    [Test]
    public void Synthetic_NameIsTrimmedAndDecodedFromPetscii()
    {
        var bytes = BuildT64("C64 tape image file", 0x0100, 1, 1, "X", ("HI", 0x0801, 0x0802, new byte[] { 0x60 }));
        // Replace the name with PETSCII "GAME 2" followed by shifted spaces ($A0) and normal padding.
        int e = T64Image.HeaderSize + 16;
        var name = new byte[] { 0x47, 0x41, 0x4D, 0x45, 0x20, 0x32, 0xA0, 0xA0, 0x20, 0x20, 0x20, 0x20, 0x20, 0x20, 0x20, 0x20 };
        name.CopyTo(bytes, e);

        var image = T64Image.Load(bytes);
        Assert.That(image.Entries[0].Name, Is.EqualTo("GAME 2"));
        Assert.That(image.Entries[0].RawName, Is.EqualTo(name));
    }

    [Test]
    public void IsT64_FalseForRandomData()
    {
        var rng = new Random(1234);
        var bytes = new byte[4096];
        rng.NextBytes(bytes);
        Assert.That(T64Image.IsT64(bytes), Is.False);
        Assert.That(T64Image.IsT64(ReadOnlySpan<byte>.Empty), Is.False);
        Assert.That(T64Image.IsT64(new byte[] { (byte)'C', (byte)'6', (byte)'4' }), Is.False, "too short for a header");
        Assert.That(() => T64Image.Load(bytes), Throws.TypeOf<FormatException>());
    }

    [Test]
    public void IsT64_TrueForSignatures()
    {
        Assert.That(T64Image.IsT64(BuildT64("C64 tape image file", 0x0100, 0, 0, "")), Is.True);
        Assert.That(T64Image.IsT64(BuildT64("C64S tape file", 0x0100, 0, 0, "")), Is.True);
        Assert.That(T64Image.IsT64(BuildT64("C64S tape image file", 0x0101, 0, 0, "")), Is.True);
    }

    [Test]
    public void TruncatedDirectory_DoesNotThrow()
    {
        // Header says 8 entries but the file ends after the header: nothing to read.
        var bytes = BuildT64("C64 tape image file", 0x0100, 8, 1, "TRUNC");
        Array.Resize(ref bytes, T64Image.HeaderSize);
        var image = T64Image.Load(bytes);
        Assert.That(image.Entries, Is.Empty);
        Assert.That(() => image.GetProgram(0), Throws.TypeOf<ArgumentOutOfRangeException>());
        Assert.That(image.GetFirstProgram(), Is.Null);
    }

    [Test]
    public void DataOffsetBeyondFile_YieldsEmptyEntry()
    {
        var bytes = BuildT64("C64 tape image file", 0x0100, 1, 1, "X", ("FAR", 0x0801, 0x0900, Pattern(8, 1)));
        // Point the data offset far outside the file.
        bytes[T64Image.HeaderSize + 8] = 0xFF; bytes[T64Image.HeaderSize + 9] = 0xFF;
        bytes[T64Image.HeaderSize + 10] = 0x00; bytes[T64Image.HeaderSize + 11] = 0x00;

        var image = T64Image.Load(bytes);
        Assert.That(image.Entries, Has.Count.EqualTo(1));
        Assert.That(image.Entries[0].Data, Is.Empty);
    }

    [Test]
    public void GetFirstProgram_SkipsNonProgramEntries()
    {
        var seq = Pattern(5, 1);
        var prg = Pattern(7, 9);
        var bytes = BuildT64("C64 tape image file", 0x0100, 2, 2, "X", ("DATA", 0x0000, 0x0005, seq), ("CODE", 0x0801, 0x0808, prg));
        bytes[T64Image.HeaderSize + 1] = 0x81; // first entry is SEQ

        var image = T64Image.Load(bytes);
        Assert.That(image.Entries[0].IsProgram, Is.False);
        var first = image.GetFirstProgram();
        Assert.That(first, Is.Not.Null);
        Assert.That(first!.LoadAddress, Is.EqualTo(0x0801));
        Assert.That(first.Data, Is.EqualTo(prg));
    }

    [Test]
    public void PrgFile_RoundTrip()
    {
        var data = Pattern(500, 0x77);
        var prg = new PrgFile(0x0801, data);
        var bytes = prg.ToBytes();
        Assert.That(bytes, Has.Length.EqualTo(502));
        Assert.That(bytes[0], Is.EqualTo(0x01));
        Assert.That(bytes[1], Is.EqualTo(0x08));

        var back = PrgFile.FromBytes(bytes);
        Assert.That(back.LoadAddress, Is.EqualTo(0x0801));
        Assert.That(back.Data, Is.EqualTo(data));
        Assert.That(back.EndAddress, Is.EqualTo(0x0801 + 500));
    }

    [Test]
    public void PrgFile_SaveAndLoad()
    {
        var path = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"prgtest_{Guid.NewGuid():N}.prg");
        try
        {
            var prg = new PrgFile(0xC000, Pattern(33, 0xA5));
            prg.Save(path);
            var back = PrgFile.Load(path);
            Assert.That(back.LoadAddress, Is.EqualTo(0xC000));
            Assert.That(back.Data, Is.EqualTo(prg.Data));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Test]
    public void PrgFile_TooShort_Throws()
    {
        Assert.That(() => PrgFile.FromBytes(new byte[] { 0x01 }), Throws.TypeOf<FormatException>());
        Assert.That(PrgFile.FromBytes(new byte[] { 0x01, 0x08 }).Data, Is.Empty);
    }
}
