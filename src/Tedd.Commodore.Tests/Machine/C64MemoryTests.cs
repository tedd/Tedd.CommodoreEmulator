using System;
using NUnit.Framework;
using Tedd.Commodore.Emulator.C64;

namespace Tedd.Commodore.Tests.Machine;

[TestFixture]
public class C64MemoryTests
{
    /// <summary>ROM images filled with a marker byte per ROM: BASIC $BA, KERNAL $EA, character ROM $CC.</summary>
    public static RomSet FakeRoms()
    {
        var basic = new byte[RomSet.BasicSize];
        var kernal = new byte[RomSet.KernalSize];
        var chr = new byte[RomSet.CharSize];
        Array.Fill(basic, (byte)0xBA);
        Array.Fill(kernal, (byte)0xEA);
        Array.Fill(chr, (byte)0xCC);
        kernal[0x1FFC] = 0x00; // reset vector $8000 style marker
        kernal[0x1FFD] = 0x80;
        return new RomSet(basic, kernal, chr, null, ".", "fake");
    }

    private static C64Memory Fresh()
    {
        var m = new C64Memory(FakeRoms());
        for (int i = 0; i < 0x10000; i++) m.Ram[i] = 0x11;
        return m;
    }

    private static void SetPort(C64Memory m, int lines)
    {
        m.Write(0, 0x2F);
        m.Write(1, (byte)(0x30 | (lines & 7)));
    }

    [Test]
    public void PowerOn_Port_Maps_Basic_Io_Kernal()
    {
        var m = Fresh();
        Assert.That(m.Read(0), Is.EqualTo(0x2F));
        Assert.That(m.Read(1) & 7, Is.EqualTo(7));
        Assert.That(m.Read(0xA000), Is.EqualTo(0xBA), "BASIC");
        Assert.That(m.Read(0xE000), Is.EqualTo(0xEA), "KERNAL");
        Assert.That(m.Read(0x8000), Is.EqualTo(0x11), "RAM at $8000");
        Assert.That(m.Read(0xC000), Is.EqualTo(0x11), "RAM at $C000");
        // I/O mapped, no chips attached: open bus returns the last bus value
        m.Read(0xC000);
        Assert.That(m.Read(0xDE00), Is.EqualTo(0x11));
    }

    // lines = bit 0 LORAM, bit 1 HIRAM, bit 2 CHAREN (c64-wiki "Bank Switching", modes 24-31)
    [TestCase(0, 0x11, 0x11, 0x11, 0x11)] // mode 24: all RAM
    [TestCase(1, 0x11, 0x11, 0xCC, 0x11)] // mode 25: LORAM: char ROM only
    [TestCase(2, 0x11, 0x11, 0xCC, 0xEA)] // mode 26: HIRAM: char, kernal
    [TestCase(3, 0x11, 0xBA, 0xCC, 0xEA)] // mode 27: LORAM+HIRAM: basic, char, kernal
    [TestCase(4, 0x11, 0x11, 0x11, 0x11)] // mode 28: CHAREN alone: all RAM
    [TestCase(5, 0x11, 0x11, -1, 0x11)]   // mode 29: LORAM+CHAREN: I/O only
    [TestCase(6, 0x11, 0x11, -1, 0xEA)]   // mode 30: HIRAM+CHAREN: I/O, kernal
    [TestCase(7, 0x11, 0xBA, -1, 0xEA)]   // mode 31: default
    public void Pla_Without_Cartridge(int lines, int at8000, int atA000, int atD000, int atE000)
    {
        var m = Fresh();
        SetPort(m, lines);
        Assert.That(m.Read(0x8000), Is.EqualTo(at8000), "$8000");
        Assert.That(m.Read(0xA000), Is.EqualTo(atA000), "$A000");
        if (atD000 >= 0)
            Assert.That(m.Read(0xD000), Is.EqualTo(atD000), "$D000");
        else
        {
            // I/O: color RAM is always there when I/O is mapped
            m.Write(0xD800, 0x05);
            Assert.That(m.Read(0xD800) & 0x0F, Is.EqualTo(5), "$D800 is color RAM when I/O is mapped");
            Assert.That(m.Ram[0xD800], Is.EqualTo(0x11), "RAM under I/O untouched");
        }
        Assert.That(m.Read(0xE000), Is.EqualTo(atE000), "$E000");
    }

    [Test]
    public void Port_InputBits_Have_PullUps()
    {
        var m = Fresh();
        m.Write(1, 0x00);      // LORAM/HIRAM/CHAREN written low
        m.Write(0, 0x00);      // ...but all bits are inputs: pull-ups win
        Assert.That(m.Read(1) & 7, Is.EqualTo(7));
        Assert.That(m.Read(0xA000), Is.EqualTo(0xBA), "BASIC still mapped through pull-ups");
        m.Write(0, 0x07);      // now outputs: the low value takes effect
        Assert.That(m.Read(1) & 7, Is.EqualTo(0));
        Assert.That(m.Read(0xA000), Is.EqualTo(0x11));
    }

    [Test]
    public void Port_CassetteSense_And_Motor()
    {
        var m = Fresh();
        Assert.That(m.Read(1) & 0x10, Is.EqualTo(0x10), "no key pressed reads 1");
        m.CassetteSense = false;
        Assert.That(m.Read(1) & 0x10, Is.EqualTo(0), "key pressed reads 0");
        m.Write(1, 0x37);
        Assert.That(m.CassetteMotor, Is.False);
        m.Write(1, 0x17);
        Assert.That(m.CassetteMotor, Is.True);
    }

    [Test]
    public void Port_Floating_Bits_Keep_Last_Driven_Level()
    {
        // Lorenz "cpuport": bits 3, 6 and 7 read the level last driven on the pin, not the data register.
        var m = Fresh();
        m.Write(0, 0xFF);
        m.Write(1, 0xFF);
        m.Write(0, 0x00);          // all inputs: floating pins keep 1
        m.Write(1, 0xFF);
        Assert.That(m.Read(1), Is.EqualTo(0xDF));
        m.Write(0, 0xFF);
        m.Write(1, 0xFF);
        m.Write(1, 0x00);          // driven low
        m.Write(0, 0x00);          // inputs again: floating pins keep 0 even when FF is written now
        m.Write(1, 0xFF);
        Assert.That(m.Read(1), Is.EqualTo(0x17));
    }

    [Test]
    public void Writes_Under_Rom_Go_To_Ram()
    {
        var m = Fresh();
        m.Write(0xA123, 0x42);
        m.Write(0xE123, 0x43);
        m.Write(0xD123, 0x44); // I/O mapped: goes to the VIC (absent) and not RAM
        Assert.That(m.Ram[0xA123], Is.EqualTo(0x42));
        Assert.That(m.Ram[0xE123], Is.EqualTo(0x43));
        Assert.That(m.Ram[0xD123], Is.EqualTo(0x11));
        Assert.That(m.Read(0xA123), Is.EqualTo(0xBA), "ROM still read");
        SetPort(m, 0);
        Assert.That(m.Read(0xA123), Is.EqualTo(0x42));
        Assert.That(m.Read(0xE123), Is.EqualTo(0x43));
    }

    [Test]
    public void Cartridge_8K_Maps_RomL_Only_When_Loram_And_Hiram()
    {
        var m = Fresh();
        var romL = new byte[8192];
        Array.Fill(romL, (byte)0x8A);
        m.Expansion = new Cartridge(romL, null, exrom: false, game: true, "8k");
        Assert.That(m.Read(0x8000), Is.EqualTo(0x8A));
        Assert.That(m.Read(0xA000), Is.EqualTo(0xBA), "BASIC still visible with an 8K cartridge");
        SetPort(m, 5);
        Assert.That(m.Read(0x8000), Is.EqualTo(0x11), "ROML hidden when HIRAM=0");
        SetPort(m, 6);
        Assert.That(m.Read(0x8000), Is.EqualTo(0x11), "ROML hidden when LORAM=0");
        SetPort(m, 3);
        Assert.That(m.Read(0x8000), Is.EqualTo(0x8A), "ROML visible with LORAM=HIRAM=1, CHAREN=0");
        SetPort(m, 7);
        Assert.That(m.Read(0x8000), Is.EqualTo(0x8A));
        m.Write(0x8000, 0x99);
        Assert.That(m.Ram[0x8000], Is.EqualTo(0x99), "write goes to RAM under ROML");
        Assert.That(m.Read(0x8000), Is.EqualTo(0x8A));
    }

    [Test]
    public void Cartridge_16K_Maps_RomH_At_A000()
    {
        var m = Fresh();
        var romL = new byte[8192];
        var romH = new byte[8192];
        Array.Fill(romL, (byte)0x8A);
        Array.Fill(romH, (byte)0x8B);
        m.Expansion = new Cartridge(romL, romH, exrom: false, game: false, "16k");
        Assert.That(m.Read(0x8000), Is.EqualTo(0x8A));
        Assert.That(m.Read(0xA000), Is.EqualTo(0x8B));
        Assert.That(m.Read(0xE000), Is.EqualTo(0xEA));
        SetPort(m, 2); // LORAM=0, HIRAM=1: ROMH visible, ROML not
        Assert.That(m.Read(0x8000), Is.EqualTo(0x11));
        Assert.That(m.Read(0xA000), Is.EqualTo(0x8B));
        SetPort(m, 5); // HIRAM=0: neither
        Assert.That(m.Read(0xA000), Is.EqualTo(0x11));
    }

    [Test]
    public void Cartridge_Ultimax_Mode()
    {
        var m = Fresh();
        var romL = new byte[8192];
        var romH = new byte[8192];
        Array.Fill(romL, (byte)0x8A);
        Array.Fill(romH, (byte)0x8B);
        m.Expansion = new Cartridge(romL, romH, exrom: true, game: false, "ultimax");
        SetPort(m, 0); // port does not matter in Ultimax mode
        Assert.That(m.Read(0x0800), Is.EqualTo(0x11), "RAM $0000-$0FFF");
        m.Read(0x0800);
        Assert.That(m.Read(0x2000), Is.EqualTo(0x11), "open: last bus value");
        Assert.That(m.Read(0x8000), Is.EqualTo(0x8A));
        Assert.That(m.Read(0xE000), Is.EqualTo(0x8B), "ROMH at $E000");
        m.Write(0xD800, 0x03);
        Assert.That(m.Read(0xD800) & 0x0F, Is.EqualTo(3), "I/O always mapped");
        m.Write(0x2000, 0x55);
        Assert.That(m.Ram[0x2000], Is.EqualTo(0x11), "writes to open areas ignored");
        Assert.That(m.ReadVic(0x3000), Is.EqualTo(0x8B), "VIC sees ROMH at $3000");
    }

    [Test]
    public void ColorRam_UpperNibble_Is_BusNoise()
    {
        var m = Fresh();
        m.Write(0xD800, 0xF7);
        m.Ram[0x4000] = 0xA0;
        m.Read(0x4000);
        Assert.That(m.Read(0xD800), Is.EqualTo(0xA7));
        Assert.That(m.ColorRam[0], Is.EqualTo(7));
    }

    [Test]
    public void Vic_View_CharRom_In_Banks_0_And_2()
    {
        var m = Fresh();
        m.Ram[0x1000] = 0x01;
        m.Ram[0x5000] = 0x02;
        m.Ram[0x9000] = 0x03;
        m.Ram[0xD000] = 0x04;
        m.VicBank = 0;
        Assert.That(m.ReadVic(0x1000), Is.EqualTo(0xCC));
        m.VicBank = 1;
        Assert.That(m.ReadVic(0x1000), Is.EqualTo(0x02));
        m.VicBank = 2;
        Assert.That(m.ReadVic(0x1000), Is.EqualTo(0xCC));
        m.VicBank = 3;
        Assert.That(m.ReadVic(0x1000), Is.EqualTo(0x04));
        m.Ram[0xC400] = 0x77;
        Assert.That(m.ReadVic(0x0400), Is.EqualTo(0x77), "bank 3 screen");
    }

    [Test]
    public void Vic_Color_Read_Returns_Nibble()
    {
        var m = Fresh();
        m.ColorRam[0x3FF] = 0x0E;
        Assert.That(m.ReadColor(0x3FF), Is.EqualTo(14));
        Assert.That(m.ReadColor(0x7FF), Is.EqualTo(14), "mirrors above 1K");
    }

    [Test]
    public void HardReset_Fills_Ram_With_PowerOn_Pattern()
    {
        var m = new C64Memory(FakeRoms());
        Assert.That(m.Ram[0x0000], Is.EqualTo(0x00));
        Assert.That(m.Ram[0x003F], Is.EqualTo(0x00));
        Assert.That(m.Ram[0x0040], Is.EqualTo(0xFF));
        Assert.That(m.Ram[0x007F], Is.EqualTo(0xFF));
        Assert.That(m.Ram[0x0080], Is.EqualTo(0x00));
        m.Ram[0x1234] = 0x5A;
        m.Reset(hard: false);
        Assert.That(m.Ram[0x1234], Is.EqualTo(0x5A), "soft reset keeps RAM");
        m.Reset(hard: true);
        Assert.That(m.Ram[0x1234], Is.EqualTo(0x00));
    }

    [Test]
    public void Peek_Is_Same_As_Read_For_Ram_And_Rom()
    {
        var m = Fresh();
        Assert.That(m.Peek(0x1234), Is.EqualTo(0x11));
        Assert.That(m.Peek(0xA000), Is.EqualTo(0xBA));
        Assert.That(m.Peek(0xFFFD), Is.EqualTo(0x80));
    }
}
