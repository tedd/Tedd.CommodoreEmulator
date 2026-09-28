using System;
using System.IO;
using NUnit.Framework;
using Tedd.Commodore.Emulator.Tools;
using Tedd.Commodore.Emulator.Video;

namespace Tedd.Commodore.Tests.Video;

[TestFixture]
public class Vdc8563Tests
{
    private static Vdc8563 Vdc()
    {
        var v = new Vdc8563();
        // A 4-line character set at $2000 for codes 0..3, 16 bytes per character.
        for (int c = 0; c < 4; c++)
            for (int line = 0; line < 8; line++)
                v.Ram[0x2000 + c * 16 + line] = (byte)(c == 1 ? 0xFF : c == 2 ? 0x80 : c == 3 ? 0x01 : 0x00);
        return v;
    }

    private static void Select(Vdc8563 v, int reg) => v.Write(0, (byte)reg);

    private static void Set(Vdc8563 v, int reg, byte value)
    {
        Select(v, reg);
        v.Write(1, value);
    }

    private static byte Get(Vdc8563 v, int reg)
    {
        Select(v, reg);
        return v.Read(1);
    }

    [Test]
    public void Status_Has_Ready_And_Revision_Bits()
    {
        var v = Vdc();
        Assert.That(v.Read(0) & 0x80, Is.EqualTo(0x80));
        Assert.That(v.Read(0) & 0x07, Is.EqualTo(v.Revision));
    }

    [Test]
    public void Data_Register_Reads_And_Writes_Ram_With_Auto_Increment()
    {
        var v = Vdc();
        Set(v, 18, 0x12);
        Set(v, 19, 0x34);
        Set(v, 31, 0xAA);
        Set(v, 31, 0xBB);
        Assert.That(v.Ram[0x1234], Is.EqualTo(0xAA));
        Assert.That(v.Ram[0x1235], Is.EqualTo(0xBB));
        Assert.That((Get(v, 18) << 8) | Get(v, 19), Is.EqualTo(0x1236));
        Set(v, 18, 0x12);
        Set(v, 19, 0x34);
        Assert.That(Get(v, 31), Is.EqualTo(0xAA));
        Assert.That(Get(v, 31), Is.EqualTo(0xBB));
        Assert.That(v.Ready, Is.False, "busy for a few cycles after a data access");
        for (int i = 0; i < 50; i++) v.Clock();
        Assert.That(v.Ready, Is.True);
    }

    [Test]
    public void Block_Fill_And_Copy()
    {
        var v = Vdc();
        Set(v, 24, 0x00);                                // fill mode
        Set(v, 18, 0x10);
        Set(v, 19, 0x00);
        Set(v, 31, 0x41);                                // writes $1000 and leaves $41 in R31, address -> $1001
        Set(v, 30, 0x09);                                // fill 9 more bytes
        for (int i = 0; i < 10; i++) Assert.That(v.Ram[0x1000 + i], Is.EqualTo(0x41), $"byte {i}");
        Assert.That(v.Ram[0x100A], Is.EqualTo(0));
        Assert.That((Get(v, 18) << 8) | Get(v, 19), Is.EqualTo(0x100A));

        Set(v, 24, 0x80);                                // copy mode
        Set(v, 32, 0x10);
        Set(v, 33, 0x00);                                // source $1000
        Set(v, 18, 0x20);
        Set(v, 19, 0x00);                                // destination $2000... use $3000 to keep the charset
        Set(v, 18, 0x30);
        Set(v, 30, 0x00);                                // 256 bytes
        for (int i = 0; i < 10; i++) Assert.That(v.Ram[0x3000 + i], Is.EqualTo(0x41));
        Assert.That((Get(v, 32) << 8) | Get(v, 33), Is.EqualTo(0x1100));
        Assert.That((Get(v, 18) << 8) | Get(v, 19), Is.EqualTo(0x3100));
    }

    [Test]
    public void Frame_Timing_Is_About_50_Hz()
    {
        var v = Vdc();
        // R0 = 126 -> 1016 dots per line at 16 MHz = 63.5 us; (R4 + 1) * 8 + R5 lines. Use the PAL KERNAL values.
        Set(v, 4, 38);
        Set(v, 5, 0);
        int frames = 0;
        v.FrameCompleted += () => frames++;
        for (int i = 0; i < 985248; i++) v.Clock();      // one second
        Assert.That(frames, Is.InRange(49, 52));
    }

    [Test]
    public void Vertical_Blank_Bit_Follows_The_Raster()
    {
        var v = Vdc();
        Set(v, 4, 38);
        bool sawBlank = false, sawDisplay = false;
        for (int i = 0; i < 40000; i++)
        {
            v.Clock();
            if ((v.Read(0) & 0x20) != 0) sawBlank = true; else sawDisplay = true;
        }
        Assert.That(sawBlank && sawDisplay, Is.True);
    }

    [Test]
    public void Renders_Text_With_Attributes()
    {
        var v = Vdc();
        // Screen at 0, attributes at $0800 (defaults), background black (R26 low nibble), 80 x 25.
        Set(v, 26, 0x00);
        v.Ram[0] = 1;                                    // solid block
        v.Ram[0x800] = 0x0F;                             // white
        v.Ram[1] = 1;
        v.Ram[0x801] = 0x4F;                             // reverse: shows background
        v.Ram[2] = 2;                                    // left column only
        v.Ram[0x802] = 0x02;                             // dark blue
        v.Render();
        uint At(int x, int y) => v.Frame[(Vdc8563.DisplayY + y) * Vdc8563.FrameWidth + Vdc8563.DisplayX + x];
        Assert.That(At(0, 0), Is.EqualTo(Vdc8563.Palette[15]));
        Assert.That(At(7, 7), Is.EqualTo(Vdc8563.Palette[15]));
        Assert.That(At(8, 0), Is.EqualTo(Vdc8563.Palette[0]), "reverse video of a solid block is background");
        Assert.That(At(16, 0), Is.EqualTo(Vdc8563.Palette[2]));
        Assert.That(At(17, 0), Is.EqualTo(Vdc8563.Palette[0]));
        Assert.That(v.Frame[0], Is.EqualTo(Vdc8563.Palette[0]), "border area shows the background colour");
        var dir = Path.Combine(AppContext.BaseDirectory, "TestResults");
        Directory.CreateDirectory(dir);
        PngWriter.Save(Path.Combine(dir, "vdc_text.png"), Vdc8563.FrameWidth, Vdc8563.FrameHeight, v.Frame);
    }

    [Test]
    public void Renders_Bitmap_Mode()
    {
        var v = Vdc();
        Set(v, 25, 0x80);                                // bitmap, no attributes
        Set(v, 26, 0xF0);                                // white on black
        v.Ram[0] = 0xA5;                                 // first 8 pixels of line 0
        v.Ram[80] = 0xFF;                                // line 1
        v.Render();
        uint At(int x, int y) => v.Frame[(Vdc8563.DisplayY + y) * Vdc8563.FrameWidth + Vdc8563.DisplayX + x];
        Assert.That(At(0, 0), Is.EqualTo(Vdc8563.Palette[15]));
        Assert.That(At(1, 0), Is.EqualTo(Vdc8563.Palette[0]));
        Assert.That(At(2, 0), Is.EqualTo(Vdc8563.Palette[15]));
        Assert.That(At(7, 0), Is.EqualTo(Vdc8563.Palette[15]));
        Assert.That(At(3, 1), Is.EqualTo(Vdc8563.Palette[15]));
    }

    [Test]
    public void Screen_Text_Helper_Reads_Rows()
    {
        var v = Vdc();
        for (int i = 0; i < 5; i++) v.Ram[i] = (byte)(1 + i);
        v.Ram[80] = 8;
        var text = v.GetScreenText(b => b == 0 ? ' ' : (char)('@' + b));
        Assert.That(text, Does.StartWith("ABCDE\nH\n"));
    }
}
