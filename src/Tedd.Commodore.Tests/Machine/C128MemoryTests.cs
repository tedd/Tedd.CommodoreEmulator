using System;
using NUnit.Framework;
using Tedd.Commodore.Emulator.C128;
using Tedd.Commodore.Emulator.C64;

namespace Tedd.Commodore.Tests.Machine;

/// <summary>MMU / memory map rules of the C128, checked with synthetic ROM images (no real ROMs needed).</summary>
[TestFixture]
public class C128MemoryTests
{
    /// <summary>ROM images filled with a marker byte per ROM so the mapped source can be identified.</summary>
    public static C128RomSet FakeRoms()
    {
        static byte[] Fill(int size, byte marker)
        {
            var a = new byte[size];
            Array.Fill(a, marker);
            return a;
        }
        var kernal = Fill(C128RomSet.KernalSize, 0xEE);
        Array.Fill(kernal, (byte)0xCC, 0, 0x1000);          // editor
        Array.Fill(kernal, (byte)0xBB, 0x1000, 0x1000);     // Z80 BIOS
        var chargen = Fill(C128RomSet.CharSize, 0x64);
        Array.Fill(chargen, (byte)0x28, 0x1000, 0x1000);    // C128 set
        return new C128RomSet(Fill(C128RomSet.BasicHalfSize, 0x40), Fill(C128RomSet.BasicHalfSize, 0x80), kernal, chargen,
            Fill(RomSet.BasicSize, 0xA6), Fill(RomSet.KernalSize, 0xE6), null, "", "fake");
    }

    private static C128Memory Memory()
    {
        var m = new C128Memory(FakeRoms());
        for (int i = 0; i < 0x20000; i++) m.Ram[i] = (byte)(i >> 16 == 0 ? 0x11 : 0x22);
        return m;
    }

    [Test]
    public void Reset_Configuration_Is_Bank0_All_Roms_And_Io()
    {
        var m = Memory();
        Assert.That(m.Mmu.Cr, Is.EqualTo(0));
        Assert.That(m.Peek(0x0400), Is.EqualTo(0x11));   // RAM bank 0
        Assert.That(m.Peek(0x4000), Is.EqualTo(0x40));   // BASIC low
        Assert.That(m.Peek(0x8000), Is.EqualTo(0x80));   // BASIC high
        Assert.That(m.Peek(0xC000), Is.EqualTo(0xCC));   // editor
        Assert.That(m.Peek(0xE000), Is.EqualTo(0xEE));   // KERNAL
        Assert.That(m.Peek(0xFFFF), Is.EqualTo(0xEE));
        Assert.That(m.Mmu.Z80Active, Is.True);
        Assert.That(m.Peek(0xD50B), Is.EqualTo(0x20), "MMU version register: 2 banks, version 0");
    }

    [Test]
    public void Configuration_Register_Selects_Ram_Per_Region()
    {
        var m = Memory();
        m.Write(0xFF00, 0x3F);                           // I/O off, RAM everywhere, bank 0
        Assert.That(m.Peek(0x4000), Is.EqualTo(0x11));
        Assert.That(m.Peek(0x8000), Is.EqualTo(0x11));
        Assert.That(m.Peek(0xC000), Is.EqualTo(0x11));
        Assert.That(m.Peek(0xD000), Is.EqualTo(0x11));
        Assert.That(m.Peek(0xE000), Is.EqualTo(0x11));
        m.Write(0xFF00, 0x01);                           // I/O off, ROMs: the character ROM (C128 half) appears at $D000
        Assert.That(m.Peek(0xD000), Is.EqualTo(0x28));
        m.Write(0xFF00, 0x00);
        Assert.That(m.Peek(0xFF00), Is.EqualTo(0x00), "$FF00 reads the CR");
    }

    [Test]
    public void Ff01_To_Ff04_Load_The_Preconfiguration_Registers()
    {
        var m = Memory();
        m.Write(0xD501, 0x3F);                           // PCR A = all RAM
        m.Write(0xD502, 0x7F);                           // PCR B = all RAM, bank 1
        Assert.That(m.Peek(0xFF01), Is.EqualTo(0x3F));
        m.Write(0xFF01, 0x00);                           // any value: CR := PCR A
        Assert.That(m.Mmu.Cr, Is.EqualTo(0x3F));
        Assert.That(m.Peek(0x4000), Is.EqualTo(0x11));
        m.Write(0xFF02, 0x00);
        Assert.That(m.Mmu.Cr, Is.EqualTo(0x7F));
        Assert.That(m.Peek(0x4000), Is.EqualTo(0x22), "bank 1");
    }

    [Test]
    public void Common_Ram_Always_Comes_From_Bank_0()
    {
        var m = Memory();
        m.Write(0xD506, 0x04);                           // 1K common at the bottom
        m.Write(0xFF00, 0x7E);                           // bank 1, all RAM, I/O still on
        Assert.That(m.Peek(0x0200), Is.EqualTo(0x11), "$0000-$03FF shared");
        Assert.That(m.Peek(0x0400), Is.EqualTo(0x22));
        m.Write(0xD506, 0x0B);                           // 16K common at the top
        Assert.That(m.Peek(0x0400), Is.EqualTo(0x22));
        Assert.That(m.Peek(0xBFFF), Is.EqualTo(0x22));
        Assert.That(m.Peek(0xC000), Is.EqualTo(0x11), "$C000-$FFFF shared");
        // Writes follow the same rule.
        m.Write(0xC100, 0x99);
        Assert.That(m.Ram[0xC100], Is.EqualTo(0x99));
        Assert.That(m.Ram[0x1C100], Is.EqualTo(0x22));
        m.Write(0x8000, 0x77);
        Assert.That(m.Ram[0x18000], Is.EqualTo(0x77));
        Assert.That(m.Ram[0x8000], Is.EqualTo(0x11));
    }

    [Test]
    public void Page_Zero_And_Stack_Can_Be_Relocated_And_Swap_With_The_Target()
    {
        var m = Memory();
        m.Ram[0x0010] = 0xA0;
        m.Ram[0x1210] = 0xB0;
        m.Ram[0x0110] = 0xA1;
        m.Ram[0x2310] = 0xB1;
        m.Write(0xD508, 0x00);                           // P0 bank 0
        m.Write(0xD507, 0x12);                           // page 0 -> $1200
        m.Write(0xD50A, 0x00);
        m.Write(0xD509, 0x23);                           // page 1 -> $2300
        Assert.That(m.Peek(0x0010), Is.EqualTo(0xB0));
        Assert.That(m.Peek(0x1210), Is.EqualTo(0xA0), "the target page appears where page 0 was");
        Assert.That(m.Peek(0x0110), Is.EqualTo(0xB1));
        Assert.That(m.Peek(0x2310), Is.EqualTo(0xA1));
        m.Write(0x0020, 0x55);
        Assert.That(m.Ram[0x1220], Is.EqualTo(0x55));
        Assert.That(m.Peek(0xD508), Is.EqualTo(0xF0), "P0H reads with the upper nibble set");
        // The 8502 port stays at $0000/$0001 regardless.
        Assert.That(m.Peek(0x0000), Is.EqualTo(m.PortDdr));
    }

    [Test]
    public void Relocated_Page_Zero_In_Bank_1_Falls_Back_To_Bank_0_When_Common()
    {
        var m = Memory();
        m.Write(0xD506, 0x04);                           // 1K common bottom
        m.Write(0xD508, 0x01);                           // bank 1
        m.Write(0xD507, 0x02);                           // page 0 -> page 2, which is common -> bank 0
        Assert.That(m.Mmu.Page0Bank, Is.EqualTo(0));
        Assert.That(m.Peek(0x0010), Is.EqualTo(0x11));
        m.Write(0xD507, 0x40);                           // page $40 is not common -> bank 1
        Assert.That(m.Mmu.Page0Bank, Is.EqualTo(1));
        Assert.That(m.Peek(0x0010), Is.EqualTo(0x22));
    }

    [Test]
    public void Io_Area_Has_The_C128_Layout()
    {
        var m = Memory();
        var vdc = new Emulator.Video.Vdc8563();
        m.Vdc = vdc;
        m.Write(0xD600, 0x12);
        Assert.That(vdc.SelectedRegister, Is.EqualTo(0x12));
        Assert.That((m.Peek(0xD600) & 0x80) != 0, Is.True, "VDC status ready bit");
        Assert.That(m.Peek(0xD700), Is.EqualTo(m.LastBusValue), "$D700 is not decoded");
        m.Write(0xD800, 0x0A);
        Assert.That(m.Peek(0xD800) & 0x0F, Is.EqualTo(0x0A));
        Assert.That(m.Peek(0xD505) & 0x01, Is.EqualTo(0), "MCR bit 0: Z80 active after reset");
        Assert.That(m.Peek(0xD505) & 0x30, Is.EqualTo(0x30), "no cartridge: GAME and EXROM read high");
        Assert.That(m.Peek(0xD505) & 0x80, Is.EqualTo(0x80), "40/80 key up = 40 columns");
    }

    [Test]
    public void Colour_Ram_Banks_Follow_Port_Bits_0_And_1()
    {
        var m = Memory();
        m.Write(0x0001, 0x3F);                           // bits 0,1 = 1: bank 0 for CPU and VIC
        m.Write(0xD800, 0x05);
        Assert.That(m.ReadColor(0), Is.EqualTo(0x05));
        m.Write(0x0001, 0x3E);                           // bit 0 = 0: CPU sees bank 1, VIC still bank 0
        m.Write(0xD800, 0x09);
        Assert.That(m.ReadColor(0), Is.EqualTo(0x05));
        Assert.That(m.Peek(0xD800) & 0x0F, Is.EqualTo(0x09));
        m.Write(0x0001, 0x3C);                           // bit 1 = 0: VIC sees bank 1 too
        Assert.That(m.ReadColor(0), Is.EqualTo(0x09));
    }

    [Test]
    public void Z80_Sees_Its_Bios_At_0000_And_The_Io_Ports()
    {
        var m = Memory();
        Assert.That(m.ReadZ80(0x0000), Is.EqualTo(0xBB), "BIOS = the $D000 quarter of the KERNAL ROM");
        Assert.That(m.ReadZ80(0x0FFF), Is.EqualTo(0xBB));
        Assert.That(m.ReadZ80(0x1000), Is.EqualTo(0x11));
        Assert.That(m.ReadZ80(0xE000), Is.EqualTo(0xEE));
        m.WriteZ80(0x0000, 0x77);                        // no processor port for the Z80: plain RAM
        Assert.That(m.Ram[0], Is.EqualTo(0x77));
        Assert.That(m.PortDdr, Is.EqualTo(0x2F));
        m.Write(0xFF00, 0x3F);                           // RAM everywhere, still bank 0: the BIOS stays
        Assert.That(m.Memory_Z80BiosVisible(), Is.True);
        Assert.That(m.ReadZ80(0x0000), Is.EqualTo(0xBB));
        m.Write(0xFF00, 0x7E);                           // bank 1: RAM (bank 1) at $0000
        Assert.That(m.Memory_Z80BiosVisible(), Is.False);
        Assert.That(m.ReadZ80(0x0000), Is.EqualTo(0x22));
        m.Write(0xFF00, 0x00);
        // Ports $0000-$0FFF reach the RAM under the I/O area; $D5xx reaches the MMU.
        m.Ram[0xD123] = 0x66;
        Assert.That(m.InZ80(0x0123), Is.EqualTo(0x66));
        m.OutZ80(0x0124, 0x67);
        Assert.That(m.Ram[0xD124], Is.EqualTo(0x67));
        Assert.That(m.InZ80(0xD50B), Is.EqualTo(0x20));
        m.OutZ80(0xD505, 0x01);
        Assert.That(m.Mmu.Z80Active, Is.False);
    }

    [Test]
    public void C64_Mode_Uses_The_C64_Roms_And_Hides_The_Mmu()
    {
        var m = Memory();
        m.Write(0xD505, 0x41);                           // C64 mode, 8502
        Assert.That(m.Mmu.C64Mode, Is.True);
        Assert.That(m.Peek(0xA000), Is.EqualTo(0xA6), "C64 BASIC");
        Assert.That(m.Peek(0xE000), Is.EqualTo(0xE6), "C64 KERNAL");
        Assert.That(m.Peek(0x4000), Is.EqualTo(0x11));
        Assert.That(m.Peek(0xFF00), Is.EqualTo(0xE6), "no MMU window in C64 mode");
        Assert.That(m.Peek(0xD500), Is.EqualTo(m.LastBusValue), "no MMU registers in C64 mode");
        m.Write(0x0001, 0x34);                           // LORAM=HIRAM=CHAREN=0: all RAM like a C64
        Assert.That(m.Peek(0xA000), Is.EqualTo(0x11));
        Assert.That(m.Peek(0xE000), Is.EqualTo(0x11));
        m.Write(0x0001, 0x32);                           // HIRAM only, CHAREN low: KERNAL + character ROM (C64 half)
        Assert.That(m.Peek(0xD000), Is.EqualTo(0x64));
        Assert.That(m.PeekVic(0x1000), Is.EqualTo(0x64), "VIC sees the C64 character set");
        Assert.That(m.Peek(0xE000), Is.EqualTo(0xE6));
    }

    [Test]
    public void Vic_Reads_The_C128_Character_Set_And_The_Rcr_Ram_Bank()
    {
        var m = Memory();
        Assert.That(m.PeekVic(0x1000), Is.EqualTo(0x28));
        Assert.That(m.PeekVic(0x0400), Is.EqualTo(0x11));
        m.Write(0xD506, 0x40);                           // VIC in bank 1
        Assert.That(m.PeekVic(0x0400), Is.EqualTo(0x22));
        Assert.That(m.PeekVic(0x1000), Is.EqualTo(0x28), "character ROM shadow stays");
        m.VicBank = 1;
        Assert.That(m.PeekVic(0x1000), Is.EqualTo(0x22), "no shadow in banks 1 and 3");
    }
}

internal static class C128MemoryTestExtensions
{
    public static bool Memory_Z80BiosVisible(this C128Memory m) => m.Z80BiosVisible;
}
