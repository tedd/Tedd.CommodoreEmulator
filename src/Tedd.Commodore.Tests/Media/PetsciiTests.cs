using System;
using NUnit.Framework;
using Tedd.Commodore.Emulator.Media;

namespace Tedd.Commodore.Tests.Media;

[TestFixture]
public class PetsciiTests
{
    [Test]
    public void PetsciiToAscii_LettersDependOnCharset()
    {
        for (int i = 0; i < 26; i++)
        {
            var code = (byte)(0x41 + i);
            Assert.That(Petscii.PetsciiToAscii(code), Is.EqualTo((char)('A' + i)), "power-on set shows upper case");
            Assert.That(Petscii.PetsciiToAscii(code, PetsciiCharset.UppercaseGraphics), Is.EqualTo((char)('A' + i)));
            Assert.That(Petscii.PetsciiToAscii(code, PetsciiCharset.LowercaseUppercase), Is.EqualTo((char)('a' + i)));

            var shifted = (byte)(0xC1 + i);
            Assert.That(Petscii.PetsciiToAscii(shifted, PetsciiCharset.LowercaseUppercase), Is.EqualTo((char)('A' + i)));
            Assert.That(Petscii.PetsciiToAscii(shifted, PetsciiCharset.UppercaseGraphics), Is.EqualTo((char)('A' + i)), "graphics in set 1, mapped to the letter anyway");
            Assert.That(Petscii.PetsciiToAscii((byte)(0x61 + i)), Is.EqualTo((char)('A' + i)), "$60-$7F is an alias of $C0-$DF");
        }
    }

    [Test]
    public void PetsciiToAscii_SharedRangeAndSpecials()
    {
        for (int c = 0x20; c <= 0x40; c++)
            Assert.That(Petscii.PetsciiToAscii((byte)c), Is.EqualTo((char)c));
        Assert.That(Petscii.PetsciiToAscii(0x5B), Is.EqualTo('['));
        Assert.That(Petscii.PetsciiToAscii(0x5D), Is.EqualTo(']'));
        Assert.That(Petscii.PetsciiToAscii(0x5C), Is.EqualTo('£'));
        Assert.That(Petscii.PetsciiToAscii(0x5E), Is.EqualTo('↑'));
        Assert.That(Petscii.PetsciiToAscii(0x5F), Is.EqualTo('←'));
        Assert.That(Petscii.PetsciiToAscii(0x0D), Is.EqualTo('\n'));
        Assert.That(Petscii.PetsciiToAscii(0x8D), Is.EqualTo('\n'), "shifted RETURN");
        Assert.That(Petscii.PetsciiToAscii(0xA0), Is.EqualTo(' '), "shifted space");
        Assert.That(Petscii.PetsciiToAscii(0xFF), Is.EqualTo('π'));
        Assert.That(Petscii.PetsciiToAscii(0x93), Is.EqualTo(Petscii.Unmappable), "CLR control code");
        Assert.That(Petscii.PetsciiToAscii(0xA1), Is.EqualTo(Petscii.Unmappable), "graphics symbol");
    }

    [Test]
    public void AsciiToPetscii_Letters()
    {
        Assert.That(Petscii.AsciiToPetscii('a'), Is.EqualTo(0x41));
        Assert.That(Petscii.AsciiToPetscii('z'), Is.EqualTo(0x5A));
        Assert.That(Petscii.AsciiToPetscii('A'), Is.EqualTo(0xC1));
        Assert.That(Petscii.AsciiToPetscii('Z'), Is.EqualTo(0xDA));
        Assert.That(Petscii.AsciiToPetscii('0'), Is.EqualTo(0x30));
        Assert.That(Petscii.AsciiToPetscii(' '), Is.EqualTo(0x20));
        Assert.That(Petscii.AsciiToPetscii('@'), Is.EqualTo(0x40));
        Assert.That(Petscii.AsciiToPetscii('['), Is.EqualTo(0x5B));
        Assert.That(Petscii.AsciiToPetscii('£'), Is.EqualTo(0x5C));
        Assert.That(Petscii.AsciiToPetscii('\n'), Is.EqualTo(0x0D));
        Assert.That(Petscii.AsciiToPetscii('\r'), Is.EqualTo(0x0D));
        Assert.That(Petscii.AsciiToPetscii('~'), Is.EqualTo((byte)'?'), "unmappable");
        Assert.That(Petscii.AsciiToPetscii('é'), Is.EqualTo((byte)'?'), "unmappable");
    }

    [Test]
    public void AsciiToPetscii_RoundTripsThroughLowercaseSet()
    {
        const string text = "Hello World 123 [ok] @£\n";
        var petscii = Petscii.AsciiToPetscii(text);
        Assert.That(petscii, Has.Length.EqualTo(text.Length));
        Assert.That(Petscii.PetsciiToAscii(petscii, PetsciiCharset.LowercaseUppercase), Is.EqualTo(text));
        Assert.That(Petscii.PetsciiToAscii(petscii), Is.EqualTo("HELLO WORLD 123 [OK] @£\n"), "power-on set shows everything upper case");
    }

    [Test]
    public void AsciiToKeyboardBuffer_RunWithReturn()
    {
        var buffer = Petscii.AsciiToKeyboardBuffer("RUN\n");
        Assert.That(buffer, Is.EqualTo(new byte[] { 0x52, 0x55, 0x4E, 0x0D }), "unshifted key codes, RETURN last");
        Assert.That(Petscii.AsciiToKeyboardBuffer("run\r"), Is.EqualTo(buffer), "case-insensitive by default");
        Assert.That(Petscii.AsciiToKeyboardBuffer("LOAD\"*\",8,1\n"), Is.EqualTo(new byte[] { 0x4C, 0x4F, 0x41, 0x44, 0x22, 0x2A, 0x22, 0x2C, 0x38, 0x2C, 0x31, 0x0D }));
    }

    [Test]
    public void AsciiToKeyboardBuffer_PreserveCase()
    {
        var buffer = Petscii.AsciiToKeyboardBuffer("Ab\n", preserveCase: true);
        Assert.That(buffer, Is.EqualTo(new byte[] { 0xC1, 0x42, 0x0D }));
    }

    [Test]
    public void ScreenCodes_HelloWorld()
    {
        // "hello world" as it is stored in the video matrix (screen codes 1-26 = letters, 32 = space).
        var codes = new byte[] { 8, 5, 12, 12, 15, 32, 23, 15, 18, 12, 4 };
        Assert.That(Petscii.ScreenCodesToAscii(codes, PetsciiCharset.LowercaseUppercase), Is.EqualTo("hello world"));
        Assert.That(Petscii.ScreenCodesToAscii(codes), Is.EqualTo("HELLO WORLD"), "the same bytes show upper case in the power-on set");
    }

    [Test]
    public void ScreenCodes_ReverseBitIsStripped()
    {
        var codes = new byte[] { 8 | 0x80, 9 | 0x80, 0x21 | 0x80 };
        Assert.That(Petscii.ScreenCodesToAscii(codes), Is.EqualTo("HI!"));
        Assert.That(Petscii.ScreenCodeToAscii(0xA0), Is.EqualTo(' '), "reverse space");
    }

    [Test]
    public void ScreenCodes_SpecialsAndDigits()
    {
        Assert.That(Petscii.ScreenCodeToAscii(0), Is.EqualTo('@'));
        Assert.That(Petscii.ScreenCodeToAscii(27), Is.EqualTo('['));
        Assert.That(Petscii.ScreenCodeToAscii(28), Is.EqualTo('£'));
        Assert.That(Petscii.ScreenCodeToAscii(29), Is.EqualTo(']'));
        Assert.That(Petscii.ScreenCodeToAscii(30), Is.EqualTo('↑'));
        Assert.That(Petscii.ScreenCodeToAscii(31), Is.EqualTo('←'));
        Assert.That(Petscii.ScreenCodeToAscii(32), Is.EqualTo(' '));
        for (int d = 0; d < 10; d++)
            Assert.That(Petscii.ScreenCodeToAscii((byte)(48 + d)), Is.EqualTo((char)('0' + d)));
        Assert.That(Petscii.ScreenCodeToAscii(63), Is.EqualTo('?'));
        Assert.That(Petscii.ScreenCodeToAscii(65, PetsciiCharset.LowercaseUppercase), Is.EqualTo('A'), "shifted letters in set 2");
        Assert.That(Petscii.ScreenCodeToAscii(90, PetsciiCharset.LowercaseUppercase), Is.EqualTo('Z'));
        Assert.That(Petscii.ScreenCodeToAscii(96), Is.EqualTo(' '), "shifted space");
        Assert.That(Petscii.ScreenCodeToAscii(97), Is.EqualTo(Petscii.Unmappable), "graphics block");
        Assert.That(Petscii.ScreenCodeToAscii(94), Is.EqualTo(Petscii.Unmappable), "π glyph position ($DE) is a graphics symbol");
    }

    [Test]
    public void PetsciiToScreenCode_KernalTable()
    {
        // Table from the KERNAL screen editor / codebase64 "PETSCII to screencode conversion".
        Assert.That(Petscii.PetsciiToScreenCode(0x40), Is.EqualTo(0x00), "'@'");
        Assert.That(Petscii.PetsciiToScreenCode(0x41), Is.EqualTo(0x01), "'a'");
        Assert.That(Petscii.PetsciiToScreenCode(0x5A), Is.EqualTo(0x1A), "'z'");
        Assert.That(Petscii.PetsciiToScreenCode(0x20), Is.EqualTo(0x20), "space");
        Assert.That(Petscii.PetsciiToScreenCode(0x31), Is.EqualTo(0x31), "'1'");
        Assert.That(Petscii.PetsciiToScreenCode(0xC1), Is.EqualTo(0x41), "'A'");
        Assert.That(Petscii.PetsciiToScreenCode(0xDA), Is.EqualTo(0x5A), "'Z'");
        Assert.That(Petscii.PetsciiToScreenCode(0x61), Is.EqualTo(0x41), "$60-$7F alias of $C0-$DF");
        Assert.That(Petscii.PetsciiToScreenCode(0xA0), Is.EqualTo(0x60), "shifted space");
        Assert.That(Petscii.PetsciiToScreenCode(0xE0), Is.EqualTo(0x60), "$E0-$FF alias of $A0-$BF");
        Assert.That(Petscii.PetsciiToScreenCode(0xFF), Is.EqualTo(0x5E), "π");
        Assert.That(Petscii.PetsciiToScreenCode(0x00), Is.EqualTo(0x80), "control codes land in the reverse block");
        Assert.That(Petscii.PetsciiToScreenCode(0x1F), Is.EqualTo(0x9F));
        Assert.That(Petscii.PetsciiToScreenCode(0x80), Is.EqualTo(0x80));
    }

    [Test]
    public void ScreenCodeToPetscii_InvertsPetsciiToScreenCode()
    {
        // Every printable PETSCII code in the canonical ranges survives a round trip through the screen code.
        for (int c = 0x20; c <= 0x5F; c++)
            Assert.That(Petscii.ScreenCodeToPetscii(Petscii.PetsciiToScreenCode((byte)c)), Is.EqualTo((byte)c), $"${c:X2}");
        for (int c = 0xA0; c <= 0xDF; c++)
            Assert.That(Petscii.ScreenCodeToPetscii(Petscii.PetsciiToScreenCode((byte)c)), Is.EqualTo((byte)c), $"${c:X2}");
        // And every screen code 0-127 round-trips the other way.
        for (int s = 0; s < 128; s++)
            Assert.That(Petscii.PetsciiToScreenCode(Petscii.ScreenCodeToPetscii((byte)s)), Is.EqualTo((byte)s), $"screen code {s}");
    }

    [Test]
    public void AsciiToScreenCode()
    {
        Assert.That(Petscii.AsciiToScreenCode('a'), Is.EqualTo(1));
        Assert.That(Petscii.AsciiToScreenCode('A'), Is.EqualTo(65));
        Assert.That(Petscii.AsciiToScreenCode(' '), Is.EqualTo(32));
        Assert.That(Petscii.AsciiToScreenCode('@'), Is.EqualTo(0));
        Assert.That(Petscii.AsciiToScreenCode('9'), Is.EqualTo(57));
        var row = new byte[11];
        const string text = "hello world";
        for (int i = 0; i < text.Length; i++) row[i] = Petscii.AsciiToScreenCode(text[i]);
        Assert.That(Petscii.ScreenCodesToAscii(row, PetsciiCharset.LowercaseUppercase), Is.EqualTo(text));
    }

    [Test]
    public void NullArguments_Throw()
    {
        Assert.That(() => Petscii.AsciiToPetscii((string)null!), Throws.ArgumentNullException);
        Assert.That(() => Petscii.AsciiToKeyboardBuffer(null!), Throws.ArgumentNullException);
    }
}
