using System;
using NUnit.Framework;
using Tedd.Commodore.Emulator.C64;

namespace Tedd.Commodore.Tests.Machine;

[TestFixture]
public class RomSetTests
{
    [Test]
    public void Locate_Finds_Repository_Rom_Directory()
    {
        var dir = RomSet.Locate();
        if (dir is null) Assert.Ignore("ROM directory not found (ROM images are not committed)");
        Assert.That(dir, Does.Contain("Tedd.Commodore.GUI").Or.Contain("roms").IgnoreCase);
    }

    [Test]
    public void Load_Real_Roms()
    {
        var roms = RomSet.TryLoadDefault();
        if (roms is null) Assert.Ignore("ROM images not available");
        Assert.That(roms!.Basic, Has.Length.EqualTo(8192));
        Assert.That(roms.Kernal, Has.Length.EqualTo(8192));
        Assert.That(roms.Char, Has.Length.EqualTo(4096));
        // KERNAL vectors: NMI $FE43, RESET $FCE2, IRQ $FF48
        Assert.That(roms.Kernal[0x1FFA] | (roms.Kernal[0x1FFB] << 8), Is.EqualTo(0xFE43));
        Assert.That(roms.Kernal[0x1FFC] | (roms.Kernal[0x1FFD] << 8), Is.EqualTo(0xFCE2));
        Assert.That(roms.Kernal[0x1FFE] | (roms.Kernal[0x1FFF] << 8), Is.EqualTo(0xFF48));
        // BASIC starts with the cold/warm start vectors and "CBMBASIC"
        Assert.That(System.Text.Encoding.ASCII.GetString(roms.Basic, 4, 8), Is.EqualTo("CBMBASIC"));
        // Character ROM: '@' is the first glyph
        Assert.That(roms.Char[0], Is.EqualTo(0x3C));
        Assert.That(roms.Char[1], Is.EqualTo(0x66));
        TestContext.Out.WriteLine(roms.Description);
    }

    [Test]
    public void Load_Drive_Rom_If_Present()
    {
        var roms = RomSet.TryLoadDefault();
        if (roms?.Drive1541 is null) Assert.Ignore("1541 ROM not available");
        var rom = roms!.Drive1541!;
        Assert.That(rom, Has.Length.EqualTo(16384));
        int reset = rom[0x3FFC] | (rom[0x3FFD] << 8);
        Assert.That(reset, Is.InRange(0xC000, 0xFFFF), "reset vector points into the DOS ROM");
    }

    [Test]
    public void Constructor_Validates_Sizes()
    {
        Assert.Throws<ArgumentException>(() => new RomSet(new byte[1], new byte[8192], new byte[4096], null, ".", ""));
        Assert.Throws<ArgumentException>(() => new RomSet(new byte[8192], new byte[8192], new byte[4096], new byte[100], ".", ""));
    }
}
