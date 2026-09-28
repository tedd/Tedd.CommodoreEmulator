using System;
using System.IO;
using System.Text;
using NUnit.Framework;
using Tedd.Commodore.Emulator.C64;

namespace Tedd.Commodore.Tests.Machine;

[TestFixture]
public class CartridgeTests
{
    [Test]
    public void Raw_8K()
    {
        var data = new byte[8192];
        data[0] = 0x12;
        var c = Cartridge.FromRaw(data);
        Assert.That(c.RomL![0], Is.EqualTo(0x12));
        Assert.That(c.RomH, Is.Null);
        Assert.That(c.Exrom, Is.False);
        Assert.That(c.Game, Is.True);
        Assert.That(c.IsUltimax, Is.False);
    }

    [Test]
    public void Raw_16K()
    {
        var data = new byte[16384];
        data[8192] = 0x34;
        var c = Cartridge.FromRaw(data);
        Assert.That(c.RomH, Is.Not.Null);
        Assert.That(c.RomH![0], Is.EqualTo(0x34));
        Assert.That(c.Exrom, Is.False);
        Assert.That(c.Game, Is.False);
    }

    [Test]
    public void Raw_4K_Is_Mirrored()
    {
        var data = new byte[4096];
        data[5] = 0x56;
        var c = Cartridge.FromRaw(data);
        Assert.That(c.RomL![5], Is.EqualTo(0x56));
        Assert.That(c.RomL![4096 + 5], Is.EqualTo(0x56));
    }

    [Test]
    public void Raw_Wrong_Size_Throws()
    {
        Assert.Throws<InvalidDataException>(() => Cartridge.FromRaw(new byte[1000]));
    }

    private static byte[] BuildCrt(int type, byte exrom, byte game, params (int loadAddress, byte[] chip)[] chips)
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(Encoding.ASCII.GetBytes("C64 CARTRIDGE   "));
        w.Write(new byte[] { 0, 0, 0, 0x40 });        // header length
        w.Write(new byte[] { 1, 0 });                  // version
        w.Write(new byte[] { (byte)(type >> 8), (byte)type });
        w.Write(exrom);
        w.Write(game);
        w.Write(new byte[6]);
        var name = Encoding.ASCII.GetBytes("TEST CART".PadRight(32, '\0'));
        w.Write(name);
        foreach (var (load, chip) in chips)
        {
            w.Write(Encoding.ASCII.GetBytes("CHIP"));
            int len = 0x10 + chip.Length;
            w.Write(new byte[] { (byte)(len >> 24), (byte)(len >> 16), (byte)(len >> 8), (byte)len });
            w.Write(new byte[] { 0, 0 });             // chip type ROM
            w.Write(new byte[] { 0, 0 });             // bank
            w.Write(new byte[] { (byte)(load >> 8), (byte)load });
            w.Write(new byte[] { (byte)(chip.Length >> 8), (byte)chip.Length });
            w.Write(chip);
        }
        return ms.ToArray();
    }

    [Test]
    public void Crt_8K_Normal()
    {
        var chip = new byte[8192];
        chip[1] = 0x77;
        var crt = BuildCrt(0, 0, 1, (0x8000, chip));
        Assert.That(Cartridge.IsCrt(crt), Is.True);
        var c = Cartridge.FromCrt(crt);
        Assert.That(c.Name, Is.EqualTo("TEST CART"));
        Assert.That(c.RomL![1], Is.EqualTo(0x77));
        Assert.That(c.RomH, Is.Null);
        Assert.That(c.Exrom, Is.False);
        Assert.That(c.Game, Is.True);
    }

    [Test]
    public void Crt_16K_Single_Chip()
    {
        var chip = new byte[16384];
        chip[8192 + 2] = 0x88;
        var c = Cartridge.FromCrt(BuildCrt(0, 0, 0, (0x8000, chip)));
        Assert.That(c.RomH![2], Is.EqualTo(0x88));
        Assert.That(c.Game, Is.False);
    }

    [Test]
    public void Crt_Ultimax_Two_Chips()
    {
        var lo = new byte[8192];
        var hi = new byte[8192];
        hi[3] = 0x99;
        var c = Cartridge.FromCrt(BuildCrt(0, 1, 0, (0x8000, lo), (0xE000, hi)));
        Assert.That(c.IsUltimax, Is.True);
        Assert.That(c.RomH![3], Is.EqualTo(0x99));
    }

    [Test]
    public void Crt_Unsupported_Type_Throws()
    {
        Assert.Throws<NotSupportedException>(() => Cartridge.FromCrt(BuildCrt(5, 0, 1, (0x8000, new byte[8192]))));
    }

    [Test]
    public void Diagnostic_Cartridges_Have_Cbm80_Signature()
    {
        var dir = RomSet.Locate();
        if (dir is null) Assert.Ignore("ROM directory not found");
        int found = 0;
        foreach (var name in new[] { "c64_diag_rev4.1.1.bin", "c64gs_diag.2.0.bin" })
        {
            var path = Path.Combine(dir!, name);
            if (!File.Exists(path)) continue;
            found++;
            var c = Cartridge.Load(path);
            // "CBM80" in PETSCII: C, B, M are the shifted letters $C3 $C2 $CD, followed by ASCII "80".
            Assert.That(c.RomL![4..9], Is.EqualTo(new byte[] { 0xC3, 0xC2, 0xCD, 0x38, 0x30 }), name);
            Assert.That(c.Exrom, Is.False);
            Assert.That(c.Game, Is.True);
        }
        if (found == 0) Assert.Ignore("No diagnostic cartridge images present");
    }
}
