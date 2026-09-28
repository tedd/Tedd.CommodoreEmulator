using System;
using System.Collections.Generic;
using NUnit.Framework;
using Tedd.Commodore.Emulator.Tools;
using Tedd.Commodore.Emulator.Video;
using Tedd.Commodore.Tests.Support;

namespace Tedd.Commodore.Tests.Tools;

[TestFixture]
public class CharacterSetTests
{
    /// <summary>A ROM shaped image where character n is filled with the byte n, so glyphs identify themselves.</summary>
    private static byte[] CountingRom()
    {
        var rom = new byte[CharacterSet.RomSize];
        for (int i = 0; i < rom.Length; i++)
            rom[i] = (byte)(i / CharacterSet.BytesPerCharacter);
        return rom;
    }

    // ------------------------------------------------------------------ layout

    [Test]
    public void RomImage_HasTwoSetsOf256Characters()
    {
        var set = CharacterSet.FromRom(new byte[CharacterSet.RomSize], "test");
        Assert.Multiple(() =>
        {
            Assert.That(set.Count, Is.EqualTo(2 * CharacterSet.CharactersPerSet));
            Assert.That(set.Size, Is.EqualTo(4096));
            Assert.That(set.IsRomImage, Is.True);
            Assert.That(CharacterSet.SetSize, Is.EqualTo(2048));
            Assert.That(CharacterSet.LowercaseSetOffset, Is.EqualTo(2048));
        });
    }

    [Test]
    public void Glyph_ReturnsTheEightBytesOfTheCharacter()
    {
        var set = CharacterSet.FromRom(CountingRom(), "test");
        Assert.That(set.Glyph(0).ToArray(), Is.EqualTo(new byte[8]));
        Assert.That(set.Glyph(511).ToArray(), Is.EqualTo(new byte[] { 255, 255, 255, 255, 255, 255, 255, 255 }));
    }

    [Test]
    public void Constructor_RejectsSizesThatAreNotWholeCharacters()
    {
        Assert.Throws<ArgumentException>(() => CharacterSet.FromRom(new byte[12], "odd"));
        Assert.Throws<ArgumentException>(() => CharacterSet.FromRom(Array.Empty<byte>(), "empty"));
    }

    [Test]
    public void Constructor_CopiesTheData()
    {
        var data = new byte[8];
        var set = CharacterSet.FromRom(data, "test");
        data[0] = 0xFF;
        Assert.That(set.Glyph(0)[0], Is.Zero);
    }

    // ------------------------------------------------------------------ pixels

    [Test]
    public void Render_TurnsSetBitsIntoInkStartingWithTheMostSignificantBit()
    {
        // $81 = leftmost and rightmost pixel set.
        var set = CharacterSet.FromRom(new byte[] { 0x81, 0, 0, 0, 0, 0, 0, 0 }, "test");
        var pixels = new byte[CharacterSet.CharacterWidth * CharacterSet.CharacterHeight];
        set.Render(0, pixels, ink: 1, paper: 6);
        Assert.That(pixels[0], Is.EqualTo(1));
        Assert.That(pixels[7], Is.EqualTo(1));
        for (int x = 1; x < 7; x++)
            Assert.That(pixels[x], Is.EqualTo(6), $"pixel {x}");
        for (int i = 8; i < pixels.Length; i++)
            Assert.That(pixels[i], Is.EqualTo(6), $"pixel {i}");
    }

    [Test]
    public void SolidPixelCount_CountsSetBits()
    {
        var set = CharacterSet.FromRom(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0, 0, 0, 0, 0, 0, 0, 0 }, "test");
        Assert.Multiple(() =>
        {
            Assert.That(set.SolidPixelCount(0), Is.EqualTo(64));
            Assert.That(set.IsBlank(0), Is.False);
            Assert.That(set.SolidPixelCount(1), Is.Zero);
            Assert.That(set.IsBlank(1), Is.True);
        });
    }

    [Test]
    public void InvertedMismatchCount_CountsCharactersThatAreNotTheReverseOfTheirCounterpart()
    {
        var rom = new byte[CharacterSet.SetSize];
        var random = new Random(1);
        random.NextBytes(rom.AsSpan(0, CharacterSet.SetSize / 2));
        for (int i = 0; i < CharacterSet.SetSize / 2; i++)
            rom[CharacterSet.SetSize / 2 + i] = (byte)~rom[i];
        Assert.That(CharacterSet.FromRom(rom, "inverted").InvertedMismatchCount(0), Is.Zero);

        rom[CharacterSet.SetSize / 2] ^= 0x01;              // one row of character $80
        rom[CharacterSet.SetSize / 2 + 8 * 5 + 3] ^= 0x80;  // one row of character $85
        Assert.That(CharacterSet.FromRom(rom, "two off").InvertedMismatchCount(0), Is.EqualTo(2));
    }

    [Test]
    public void InvertedMismatchCount_LooksAtTheRequestedSet()
    {
        var rom = new byte[CharacterSet.RomSize];
        for (int set = 0; set < 2; set++)
            for (int i = 0; i < CharacterSet.SetSize / 2; i++)
                rom[set * CharacterSet.SetSize + CharacterSet.SetSize / 2 + i] = 0xFF;   // ~0
        rom[CharacterSet.LowercaseSetOffset + CharacterSet.SetSize / 2] = 0xFE;
        var set2 = CharacterSet.FromRom(rom, "test");
        Assert.Multiple(() =>
        {
            Assert.That(set2.InvertedMismatchCount(0), Is.Zero);
            Assert.That(set2.InvertedMismatchCount(CharacterSet.LowercaseSetOffset), Is.EqualTo(1));
            Assert.Throws<ArgumentOutOfRangeException>(() => set2.InvertedMismatchCount(CharacterSet.SetSize + 8));
        });
    }

    // ------------------------------------------------------------------ PETSCII

    [TestCase(0x41, 0x01)]   // "A"
    [TestCase(0x5A, 0x1A)]   // "Z"
    [TestCase(0x30, 0x30)]   // "0"
    [TestCase(0x20, 0x20)]   // space
    [TestCase(0x40, 0x00)]   // "@"
    [TestCase(0x5C, 0x1C)]   // pound
    [TestCase(0x61, 0x41)]   // shifted A / lowercase a
    [TestCase(0xC1, 0x41)]   // the $C0 alias of the same character
    [TestCase(0xA1, 0x61)]   // C= + A
    [TestCase(0xFF, 0x5E)]   // pi shares the glyph at $5E
    public void ScreenCodeOf_FollowsTheKernalMapping(int petscii, int expected) =>
        Assert.That(CharacterSet.ScreenCodeOf(petscii), Is.EqualTo(expected));

    [TestCase(0x00)]
    [TestCase(0x0D)]   // RETURN
    [TestCase(0x12)]   // reverse on
    [TestCase(0x1F)]
    [TestCase(0x80)]
    [TestCase(0x93)]   // clear screen
    [TestCase(0x9F)]
    public void ScreenCodeOf_ReportsControlCodesAsMinusOne(int petscii) =>
        Assert.That(CharacterSet.ScreenCodeOf(petscii), Is.EqualTo(-1));

    [Test]
    public void PetsciiCodesFor_IsTheExactInverseOfScreenCodeOf()
    {
        var expected = new List<int>[0x80];
        for (int i = 0; i < expected.Length; i++) expected[i] = new List<int>();
        for (int petscii = 0; petscii < 0x100; petscii++)
        {
            int screen = CharacterSet.ScreenCodeOf(petscii);
            if (screen >= 0) expected[screen].Add(petscii);
        }
        for (int screen = 0; screen < 0x80; screen++)
            Assert.That(CharacterSet.PetsciiCodesFor(screen).ToArray(), Is.EqualTo(expected[screen].ToArray()),
                $"screen code ${screen:X2}");
    }

    [Test]
    public void PetsciiCodesFor_CoversEveryPrintableScreenCode()
    {
        for (int screen = 0; screen < 0x80; screen++)
            Assert.That(CharacterSet.PetsciiCodesFor(screen).Length, Is.GreaterThan(0), $"screen code ${screen:X2}");
    }

    [Test]
    public void PetsciiCodesFor_IsEmptyForTheReverseVideoHalf()
    {
        for (int screen = 0x80; screen < 0x100; screen++)
            Assert.That(CharacterSet.PetsciiCodesFor(screen).Length, Is.Zero, $"screen code ${screen:X2}");
    }

    [TestCase(0x01, false, "'A'")]
    [TestCase(0x01, true, "'a'")]
    [TestCase(0x30, false, "'0'")]
    [TestCase(0x20, false, "space")]
    [TestCase(0x41, false, "graphics, SHIFT + A")]
    [TestCase(0x41, true, "'A'")]
    [TestCase(0x61, false, "graphics, C= + A")]
    [TestCase(0x81, false, "'A', reverse video")]
    public void Describe_NamesTheCharacter(int screenCode, bool lowercaseSet, string expected) =>
        Assert.That(CharacterSet.Describe(screenCode, lowercaseSet), Is.EqualTo(expected));

    // ------------------------------------------------------------------ $D018

    [TestCase(0x14, 0x1000)]   // the KERNAL default: screen $0400, character ROM set 1
    [TestCase(0x15, 0x1000)]   // POKE 53272,21
    [TestCase(0x17, 0x1800)]   // POKE 53272,23 - lowercase set
    [TestCase(0x18, 0x2000)]
    [TestCase(0x1E, 0x3800)]
    public void CharacterBaseOf_ReadsBits3To1(int d018, int expected) =>
        Assert.That(CharacterSet.CharacterBaseOf((byte)d018), Is.EqualTo(expected));

    [Test]
    public void D018For_KeepsTheVideoMatrixBits()
    {
        Assert.That(CharacterSet.D018For(0x1800, 0x15), Is.EqualTo(0x17));
        Assert.That(CharacterSet.D018For(0x3800, 0x15), Is.EqualTo(0x1F));
        Assert.That(CharacterSet.CharacterBaseOf(CharacterSet.D018For(0x2800, 0x14)), Is.EqualTo(0x2800));
    }

    // ------------------------------------------------------------------ capture from the VIC

    [Test]
    public void FromVic_ReadsTheBlockD018PointsAtWithoutTouchingTheBus()
    {
        var memory = new FakeVicMemory();
        memory.InstallCharset(0x3800);
        var vic = new VicII(memory);
        vic.Write(0x18, 0x1E);            // screen $0400, character base $3800
        long readsBefore = memory.ReadCount;

        var set = CharacterSet.FromVic(vic, vicBank: 0);

        Assert.Multiple(() =>
        {
            Assert.That(memory.ReadCount, Is.EqualTo(readsBefore), "capturing must peek, never read");
            Assert.That(set.Count, Is.EqualTo(CharacterSet.CharactersPerSet));
            Assert.That(set.VicAddress, Is.EqualTo(0x3800));
            Assert.That(set.BankBase, Is.Zero);
            Assert.That(set.IsRomShadow, Is.False, "$3800 in bank 0 is RAM");
            Assert.That(set.Glyph(FakeVicMemory.CharA).ToArray(),
                Is.EqualTo(new byte[] { 0x18, 0x3C, 0x66, 0x7E, 0x66, 0x66, 0x66, 0x00 }));
        });
    }

    [TestCase(0, 0x1000, true)]    // $1000 in an even bank is the character ROM shadow
    [TestCase(2, 0x1800, true)]
    [TestCase(1, 0x1000, false)]   // odd banks see RAM there
    [TestCase(0, 0x2000, false)]
    public void FromVic_KnowsWhenItIsLookingAtTheRomShadow(int bank, int charBase, bool shadow)
    {
        var vic = new VicII(new FakeVicMemory());
        vic.Write(0x18, CharacterSet.D018For(charBase, 0x14));
        var set = CharacterSet.FromVic(vic, bank);
        Assert.Multiple(() =>
        {
            Assert.That(set.VicAddress, Is.EqualTo(charBase));
            Assert.That(set.IsRomShadow, Is.EqualTo(shadow));
            Assert.That(set.BankBase, Is.EqualTo(bank << 14));
        });
    }

    // ------------------------------------------------------------------ CRC-32

    [Test]
    public void Crc32_MatchesTheStandardIeeePolynomial()
    {
        // Reference values from zlib.crc32 over the same bytes.
        Assert.That(CharacterSet.FromRom(new byte[8], "zeros").Crc32, Is.EqualTo(0x6522DF69u));
        var counting = new byte[16];
        for (int i = 0; i < counting.Length; i++) counting[i] = (byte)i;
        Assert.That(CharacterSet.FromRom(counting, "0..15").Crc32, Is.EqualTo(0xCECEE288u));
    }
}
