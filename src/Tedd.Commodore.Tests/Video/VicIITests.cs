using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using NUnit.Framework;
using Tedd.Commodore.Emulator.Tools;
using Tedd.Commodore.Emulator.Video;
using Tedd.Commodore.Tests.Support;

namespace Tedd.Commodore.Tests.Video;

/// <summary>
/// VIC-II (6569) tests. Frame buffer coordinates: column = X + 100 (mod 504) since cycle 1 starts at X = $194
/// (<see cref="VicII.FirstXOfLine"/>); the 40 column display window (X $18..$157) is therefore at columns
/// 124..443 and, with RSEL = 1, lines 51..250. Rendering tests save their frame to TestResults/vic_&lt;name&gt;.png.
/// </summary>
[TestFixture]
public class VicIITests
{
    private const int Left = 124;          // frame column of X = $18
    private const int Top = 51;            // first display line, RSEL = 1
    private const int Border = 14;         // border color used by the rig
    private const int Background = 6;      // background color 0 used by the rig
    private const int CyclesPerFrame = VicII.CyclesPerLine * VicII.LinesPerFrame;

    /// <summary>A VIC with the standard C64 register setup on top of a <see cref="FakeVicMemory"/>.</summary>
    private sealed class Rig
    {
        public readonly FakeVicMemory Mem = new();
        public readonly VicII Vic;

        public Rig(bool den = true, int yscroll = 3)
        {
            Vic = new VicII(Mem);
            Mem.InstallCharset();
            Mem.FillScreen(FakeVicMemory.CharBlank);
            Mem.FillColor(1);
            Vic.Write(0x18, 0x14);                                    // VM = $0400, CB = $1000
            Vic.Write(0x11, (byte)(0x08 | (den ? 0x10 : 0) | (yscroll & 7)));  // RSEL, DEN, YSCROLL
            Vic.Write(0x16, 0x08);                                    // CSEL, XSCROLL = 0
            Vic.Write(0x20, Border);
            Vic.Write(0x21, Background);
        }

        public void RunFrame()
        {
            for (int i = 0; i < CyclesPerFrame; i++)
                Vic.Clock();
        }

        /// <summary>Clocks until the last executed cycle is (<paramref name="line"/>, <paramref name="cycle"/>).</summary>
        public void RunTo(int line, int cycle)
        {
            int guard = 0;
            do
            {
                Vic.Clock();
                if (++guard > 2 * CyclesPerFrame)
                    throw new InvalidOperationException("RunTo did not reach the requested position.");
            } while (Vic.RasterLine != line || Vic.RasterCycle != cycle);
        }

        /// <summary>Runs the 63 cycles of the next line and returns BA per cycle (index 1..63).</summary>
        public bool[] BaOfNextLine()
        {
            var ba = new bool[64];
            for (int c = 1; c <= 63; c++)
            {
                Vic.Clock();
                Assert.That(Vic.RasterCycle, Is.EqualTo(c));
                ba[c] = Vic.Ba;
            }
            return ba;
        }

        public int Pixel(int column, int line) => Array.IndexOf(VicII.Palette, Vic.Frame[line * VicII.FrameWidth + column]);

        public void Save(string name) => SaveFrame(Vic, name);
    }

    private static string ResultsDirectory
    {
        get
        {
            var dir = Path.Combine(TestContext.CurrentContext.TestDirectory, "TestResults");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    private static void SaveFrame(VicII vic, string name) =>
        PngWriter.Save(Path.Combine(ResultsDirectory, $"vic_{name}.png"), VicII.FrameWidth, VicII.FrameHeight, vic.Frame);

    // 1. Frame timing --------------------------------------------------------------------------------------

    [Test]
    public void Frame_OneFrameIs63x312Cycles_FrameCompletedOnce()
    {
        var rig = new Rig();
        int completed = 0;
        rig.Vic.FrameCompleted += () => completed++;

        rig.Vic.Clock();
        Assert.That((rig.Vic.RasterLine, rig.Vic.RasterCycle), Is.EqualTo((0, 1)));
        for (int i = 0; i < 62; i++) rig.Vic.Clock();
        Assert.That((rig.Vic.RasterLine, rig.Vic.RasterCycle), Is.EqualTo((0, 63)));
        rig.Vic.Clock();
        Assert.That((rig.Vic.RasterLine, rig.Vic.RasterCycle), Is.EqualTo((1, 1)));

        for (int i = 64; i < CyclesPerFrame; i++) rig.Vic.Clock();
        Assert.That((rig.Vic.RasterLine, rig.Vic.RasterCycle), Is.EqualTo((311, 63)));
        Assert.That(completed, Is.EqualTo(1));
        Assert.That(rig.Vic.FrameCount, Is.EqualTo(1));

        rig.Vic.Clock();
        Assert.That((rig.Vic.RasterLine, rig.Vic.RasterCycle), Is.EqualTo((0, 1)));
        Assert.That(completed, Is.EqualTo(1));
    }

    [Test]
    public void Frame_XMappingAndVisibleArea()
    {
        Assert.That(VicII.XToFrameColumn(VicII.FirstXOfLine), Is.EqualTo(0));
        Assert.That(VicII.XToFrameColumn(0x18), Is.EqualTo(Left));
        Assert.That(VicII.FrameColumnToX(0), Is.EqualTo(0x194));
        Assert.That(VicII.FrameColumnToX(12 * 8 + 4), Is.EqualTo(0x000), "X wraps from $1F7 to $000 in the middle of cycle 13");
        Assert.That(VicII.FrameColumnToX(12 * 8 + 3), Is.EqualTo(0x1F7));
        Assert.That(VicII.VisibleArea, Is.EqualTo((92, 16, 384, 272)));
        Assert.That(Left - VicII.VisibleArea.X, Is.EqualTo(32));
        Assert.That(Top - VicII.VisibleArea.Y, Is.EqualTo(35));
    }

    // 2./3. Border and display window ------------------------------------------------------------------------

    [Test]
    public void Den0_EveryPixelIsBorderColor()
    {
        var rig = new Rig(den: false);
        rig.Mem.FillScreen(FakeVicMemory.CharSolid);
        rig.RunFrame();
        rig.Save("den0");
        for (int y = 0; y < VicII.FrameHeight; y++)
            for (int x = 0; x < VicII.FrameWidth; x++)
                if (rig.Pixel(x, y) != Border)
                    Assert.Fail($"Pixel ({x},{y}) is {rig.Pixel(x, y)}, expected border");
    }

    [TestCase(true, true, 0x18, 0x157, 51, 250)]
    [TestCase(false, true, 0x1F, 0x14E, 51, 250)]
    [TestCase(true, false, 0x18, 0x157, 55, 246)]
    [TestCase(false, false, 0x1F, 0x14E, 55, 246)]
    public void Den1_BlankScreen_DisplayWindowRectangle(bool csel, bool rsel, int xLeft, int xRight, int yTop, int yBottom)
    {
        var rig = new Rig();
        rig.Vic.Write(0x16, (byte)(csel ? 0x08 : 0x00));
        rig.Vic.Write(0x11, (byte)(0x13 | (rsel ? 0x08 : 0x00)));
        rig.RunFrame();
        rig.Save($"window_csel{(csel ? 1 : 0)}_rsel{(rsel ? 1 : 0)}");

        int colLeft = VicII.XToFrameColumn(xLeft), colRight = VicII.XToFrameColumn(xRight);
        for (int y = 0; y < VicII.FrameHeight; y++)
        {
            for (int x = 0; x < VicII.FrameWidth; x++)
            {
                bool inside = x >= colLeft && x <= colRight && y >= yTop && y <= yBottom;
                int expected = inside ? Background : Border;
                if (rig.Pixel(x, y) != expected)
                    Assert.Fail($"Pixel ({x},{y}) is {rig.Pixel(x, y)}, expected {expected}");
            }
        }
    }

    // 4. Bad lines / BA ------------------------------------------------------------------------------------

    [Test]
    public void BadLine_BaAssertedFromCycle12To54()
    {
        var rig = new Rig(yscroll: 3);
        rig.RunTo(0x33 - 1, 63);
        var ba = rig.BaOfNextLine();                     // line $33: (raster & 7) == 3 -> bad line
        for (int c = 1; c <= 63; c++)
            Assert.That(ba[c], Is.EqualTo(c >= 12 && c <= 54), $"BA in cycle {c}");

        var next = rig.BaOfNextLine();                   // line $34: not a bad line
        for (int c = 1; c <= 63; c++)
            Assert.That(next[c], Is.False, $"BA in cycle {c} of a normal line");
    }

    [Test]
    public void BadLine_AecFollowsBaThreeCyclesLater()
    {
        var rig = new Rig(yscroll: 3);
        rig.RunTo(0x33 - 1, 63);
        var aec = new bool[64];
        for (int c = 1; c <= 63; c++) { rig.Vic.Clock(); aec[c] = rig.Vic.Aec; }
        for (int c = 1; c <= 63; c++)
            Assert.That(aec[c], Is.EqualTo(c >= 15 && c <= 54), $"AEC in cycle {c}");
    }

    [Test]
    public void BadLine_YScrollSelectsTheLines()
    {
        var rig = new Rig(yscroll: 0);
        rig.RunTo(0x30 - 1, 63);
        var ba30 = rig.BaOfNextLine();                   // $30 & 7 == 0 -> bad line
        Assert.That(ba30[20], Is.True);
        rig.RunTo(0x33 - 1, 63);
        var ba33 = rig.BaOfNextLine();                   // $33 & 7 == 3 -> not a bad line with YSCROLL = 0
        Assert.That(Array.IndexOf(ba33, true), Is.EqualTo(-1));

        rig.RunTo(0x37 - 1, 63);
        var ba37 = rig.BaOfNextLine();                   // $37 & 7 == 7
        Assert.That(Array.IndexOf(ba37, true), Is.EqualTo(-1));
        rig.RunTo(0x38 - 1, 63);
        Assert.That(rig.BaOfNextLine()[30], Is.True);

        // Lines outside $30-$F7 are never bad lines.
        rig.RunTo(0x28 - 1, 63);
        Assert.That(Array.IndexOf(rig.BaOfNextLine(), true), Is.EqualTo(-1));
        rig.RunTo(0xF8 - 1, 63);
        Assert.That(Array.IndexOf(rig.BaOfNextLine(), true), Is.EqualTo(-1));
    }

    [Test]
    public void BadLine_NoneWhenDenIsOff()
    {
        var rig = new Rig(den: false, yscroll: 3);
        rig.RunTo(0x33 - 1, 63);
        Assert.That(Array.IndexOf(rig.BaOfNextLine(), true), Is.EqualTo(-1));

        // DEN set after line $30 has passed: still no bad lines this frame (3.5: DEN must be set in line $30).
        rig.Vic.Write(0x11, 0x1B);
        rig.RunTo(0x3B - 1, 63);
        Assert.That(Array.IndexOf(rig.BaOfNextLine(), true), Is.EqualTo(-1));

        // Next frame: DEN is set during line $30, bad lines resume.
        rig.RunTo(0x33 - 1, 63);
        Assert.That(rig.BaOfNextLine()[12], Is.True);
    }

    // 5.-9. Graphics modes ---------------------------------------------------------------------------------

    [Test]
    public void StandardText_CheckerboardAndRowsAdvance()
    {
        var rig = new Rig();
        rig.Mem.FillRows(row => row switch { 0 => FakeVicMemory.CharCheckerboard, 1 => FakeVicMemory.CharLeftHalf, 2 => FakeVicMemory.CharTopHalf, _ => FakeVicMemory.CharA });
        rig.RunFrame();
        rig.Save("text_standard");

        // Row 0, cell 0: checkerboard, $AA on even rows, $55 on odd rows, color 1 on background 6.
        for (int r = 0; r < 8; r++)
            for (int c = 0; c < 8; c++)
            {
                bool set = ((r & 1) == 0) ? (c & 1) == 0 : (c & 1) == 1;
                Assert.That(rig.Pixel(Left + c, Top + r), Is.EqualTo(set ? 1 : Background), $"cell(0,0) pixel ({c},{r})");
            }
        // Row 0 spans all 40 columns.
        Assert.That(rig.Pixel(Left + 39 * 8, Top), Is.EqualTo(1));
        Assert.That(rig.Pixel(Left + 39 * 8 + 1, Top), Is.EqualTo(Background));
        // Row 1: left half.
        Assert.That(rig.Pixel(Left + 3, Top + 8), Is.EqualTo(1));
        Assert.That(rig.Pixel(Left + 4, Top + 8), Is.EqualTo(Background));
        // Row 2: top half.
        Assert.That(rig.Pixel(Left + 7, Top + 16 + 3), Is.EqualTo(1));
        Assert.That(rig.Pixel(Left + 7, Top + 16 + 4), Is.EqualTo(Background));
        // Row 3: 'A' (0x18 on its first row -> pixels 3 and 4).
        Assert.That(rig.Pixel(Left + 2, Top + 24), Is.EqualTo(Background));
        Assert.That(rig.Pixel(Left + 3, Top + 24), Is.EqualTo(1));
        Assert.That(rig.Pixel(Left + 4, Top + 24), Is.EqualTo(1));
        Assert.That(rig.Pixel(Left + 5, Top + 24), Is.EqualTo(Background));
        // Last row (24) also shows 'A'; first pixel of the last line of the window is background.
        Assert.That(rig.Pixel(Left + 3, Top + 24 * 8), Is.EqualTo(1));
        Assert.That(rig.Pixel(Left, Top + 199), Is.EqualTo(Background));
        Assert.That(rig.Pixel(Left, Top + 200), Is.EqualTo(Border));
    }

    [Test]
    public void MulticolorText_PairsUseBackgroundColorsAndMcFlag()
    {
        var rig = new Rig();
        rig.Vic.Write(0x16, 0x18);                       // MCM + CSEL
        rig.Vic.Write(0x22, 2);
        rig.Vic.Write(0x23, 5);
        rig.Mem.SetChar(0, 0, FakeVicMemory.CharCheckerboard);   // $AA = "10" pairs, $55 = "01" pairs
        rig.Mem.SetColor(0, 0, 8 | 3);                   // MC flag set, color 3
        rig.Mem.SetChar(1, 0, FakeVicMemory.CharSolid);  // "11" pairs -> color bits 0-2
        rig.Mem.SetColor(1, 0, 8 | 4);
        rig.Mem.SetChar(2, 0, FakeVicMemory.CharSolid);  // MC flag clear: standard, colors 0-7 only
        rig.Mem.SetColor(2, 0, 2);
        rig.RunFrame();
        rig.Save("text_multicolor");

        for (int c = 0; c < 8; c++)
        {
            Assert.That(rig.Pixel(Left + c, Top), Is.EqualTo(5), $"row 0 pixel {c}: '10' -> $D023");
            Assert.That(rig.Pixel(Left + c, Top + 1), Is.EqualTo(2), $"row 1 pixel {c}: '01' -> $D022");
            Assert.That(rig.Pixel(Left + 8 + c, Top), Is.EqualTo(4), $"cell 1 pixel {c}: '11' -> color & 7");
            Assert.That(rig.Pixel(Left + 16 + c, Top), Is.EqualTo(2), $"cell 2 pixel {c}: standard");
        }
    }

    [Test]
    public void EcmText_BackgroundColorFromUpperCharBits()
    {
        var rig = new Rig();
        rig.Vic.Write(0x11, 0x5B);                       // ECM + DEN + RSEL + YSCROLL 3
        rig.Vic.Write(0x22, 2);
        rig.Vic.Write(0x23, 5);
        rig.Vic.Write(0x24, 7);
        rig.Mem.SetChar(0, 0, 0x00);                     // blank char, background 0
        rig.Mem.SetChar(1, 0, 0x40);                     // background 1
        rig.Mem.SetChar(2, 0, 0x80);                     // background 2
        rig.Mem.SetChar(3, 0, 0xC0);                     // background 3
        rig.Mem.SetChar(4, 0, 0xC0 | FakeVicMemory.CharLeftHalf);   // only 64 chars: code & $3F
        rig.Mem.SetColor(4, 0, 1);
        rig.RunFrame();
        rig.Save("text_ecm");

        Assert.That(rig.Pixel(Left + 0, Top + 4), Is.EqualTo(Background));
        Assert.That(rig.Pixel(Left + 8, Top + 4), Is.EqualTo(2));
        Assert.That(rig.Pixel(Left + 16, Top + 4), Is.EqualTo(5));
        Assert.That(rig.Pixel(Left + 24, Top + 4), Is.EqualTo(7));
        Assert.That(rig.Pixel(Left + 32, Top + 4), Is.EqualTo(1), "left half of the char is foreground");
        Assert.That(rig.Pixel(Left + 36, Top + 4), Is.EqualTo(7), "right half shows background 3");
    }

    [Test]
    public void StandardBitmap_ColorsFromVideoMatrixNibbles()
    {
        var rig = new Rig();
        rig.Vic.Write(0x11, 0x3B);                       // BMM + DEN + RSEL + YSCROLL 3
        rig.Vic.Write(0x18, 0x18);                       // VM = $0400, CB13 = 1 -> bitmap at $2000
        rig.Mem.FillBitmap(0x00);
        rig.Mem.SetBitmapCell(0, 0, new byte[] { 0xF0, 0xF0, 0xF0, 0xF0, 0xF0, 0xF0, 0xF0, 0xF0 });
        rig.Mem.SetChar(0, 0, 0x17);                     // "1" -> 1, "0" -> 7
        rig.Mem.SetBitmapCell(1, 0, new byte[] { 0x0F, 0x0F, 0x0F, 0x0F, 0x0F, 0x0F, 0x0F, 0x0F });
        rig.Mem.SetChar(1, 0, 0x53);
        rig.Mem.SetBitmapCell(0, 1, new byte[] { 0xFF, 0x00, 0xFF, 0x00, 0xFF, 0x00, 0xFF, 0x00 });
        rig.Mem.SetChar(0, 1, 0x2C);
        rig.RunFrame();
        rig.Save("bitmap_standard");

        Assert.That(rig.Pixel(Left + 0, Top), Is.EqualTo(1));
        Assert.That(rig.Pixel(Left + 3, Top + 7), Is.EqualTo(1));
        Assert.That(rig.Pixel(Left + 4, Top), Is.EqualTo(7));
        Assert.That(rig.Pixel(Left + 8, Top), Is.EqualTo(3));
        Assert.That(rig.Pixel(Left + 12, Top), Is.EqualTo(5));
        Assert.That(rig.Pixel(Left + 5, Top + 8), Is.EqualTo(2));
        Assert.That(rig.Pixel(Left + 5, Top + 9), Is.EqualTo(0xC));
        Assert.That(rig.Pixel(Left + 20, Top + 20), Is.EqualTo(0), "cleared bitmap with video matrix 0 shows color 0");
    }

    [Test]
    public void MulticolorBitmap_PairsUseBackgroundMatrixAndColorRam()
    {
        var rig = new Rig();
        rig.Vic.Write(0x11, 0x3B);                       // BMM
        rig.Vic.Write(0x16, 0x18);                       // MCM + CSEL
        rig.Vic.Write(0x18, 0x18);
        rig.Mem.FillBitmap(0x00);
        rig.Mem.SetBitmapCell(0, 0, new byte[] { 0x1B, 0x1B, 0x1B, 0x1B, 0x1B, 0x1B, 0x1B, 0x1B });   // 00 01 10 11
        rig.Mem.SetChar(0, 0, 0x12);
        rig.Mem.SetColor(0, 0, 3);
        rig.RunFrame();
        rig.Save("bitmap_multicolor");

        int[] expected = { Background, Background, 1, 1, 2, 2, 3, 3 };
        for (int c = 0; c < 8; c++)
            Assert.That(rig.Pixel(Left + c, Top + 2), Is.EqualTo(expected[c]), $"pixel {c}");
    }

    [TestCase(0x60, 0x08, "ecm_bmm")]
    [TestCase(0x40, 0x18, "ecm_mcm")]
    [TestCase(0x60, 0x18, "ecm_bmm_mcm")]
    public void InvalidModes_RenderBlack(int d011Mode, int d016, string name)
    {
        var rig = new Rig();
        rig.Vic.Write(0x11, (byte)(0x1B | d011Mode));
        rig.Vic.Write(0x16, (byte)d016);
        rig.Vic.Write(0x18, 0x18);
        rig.Mem.FillScreen(FakeVicMemory.CharSolid);
        rig.Mem.FillColor(1);
        rig.Mem.FillBitmap(0xFF);
        rig.RunFrame();
        rig.Save("invalid_" + name);

        for (int y = Top; y < Top + 200; y += 7)
            for (int x = Left; x < Left + 320; x += 5)
                Assert.That(rig.Pixel(x, y), Is.EqualTo(0), $"pixel ({x},{y})");
        Assert.That(rig.Pixel(Left - 1, Top), Is.EqualTo(Border));
    }

    // 10. Scrolling ----------------------------------------------------------------------------------------

    [Test]
    public void XScroll3_ShiftsGraphicsRightBy3()
    {
        var rig = new Rig();
        rig.Vic.Write(0x16, 0x0B);                       // CSEL, XSCROLL = 3
        rig.Mem.SetChar(0, 0, FakeVicMemory.CharLeftHalf);
        rig.RunFrame();
        rig.Save("xscroll3");

        for (int c = 0; c < 3; c++)
            Assert.That(rig.Pixel(Left + c, Top), Is.EqualTo(Background), $"pixel {c}");
        for (int c = 3; c < 7; c++)
            Assert.That(rig.Pixel(Left + c, Top), Is.EqualTo(1), $"pixel {c}");
        for (int c = 7; c < 11; c++)
            Assert.That(rig.Pixel(Left + c, Top), Is.EqualTo(Background), $"pixel {c}");
    }

    [Test]
    public void XScroll7_LoadsFromPreviousCycle()
    {
        var rig = new Rig();
        rig.Vic.Write(0x16, 0x0F);                       // XSCROLL = 7
        rig.Mem.SetChar(0, 0, FakeVicMemory.CharLeftHalf);
        rig.RunFrame();
        rig.Save("xscroll7");
        Assert.That(rig.Pixel(Left + 6, Top), Is.EqualTo(Background));
        Assert.That(rig.Pixel(Left + 7, Top), Is.EqualTo(1));
        Assert.That(rig.Pixel(Left + 10, Top), Is.EqualTo(1));
        Assert.That(rig.Pixel(Left + 11, Top), Is.EqualTo(Background));
    }

    [Test]
    public void YScroll_ShiftsGraphicsDown()
    {
        // YSCROLL = 0: the first bad line is $30 = 48, so line 51 shows row 3 of the character.
        var rig0 = new Rig(yscroll: 0);
        rig0.Mem.FillScreen(FakeVicMemory.CharTopHalf);
        rig0.RunFrame();
        rig0.Save("yscroll0");
        Assert.That(rig0.Pixel(Left, Top), Is.EqualTo(1));
        Assert.That(rig0.Pixel(Left, Top + 1), Is.EqualTo(Background));
        Assert.That(rig0.Pixel(Left, Top + 5), Is.EqualTo(1), "row 1 starts at line 56");

        // YSCROLL = 3: the first bad line is $33 = 51, so line 51 shows row 0 -> everything is 3 lines lower.
        var rig3 = new Rig(yscroll: 3);
        rig3.Mem.FillScreen(FakeVicMemory.CharTopHalf);
        rig3.RunFrame();
        rig3.Save("yscroll3");
        for (int r = 0; r < 4; r++)
            Assert.That(rig3.Pixel(Left, Top + r), Is.EqualTo(1), $"line {Top + r}");
        Assert.That(rig3.Pixel(Left, Top + 4), Is.EqualTo(Background));
        Assert.That(rig3.Pixel(Left, Top + 8), Is.EqualTo(1));
    }

    // 11. Sprites ------------------------------------------------------------------------------------------

    private static void EnableSprite(Rig rig, int n, int x, int y, byte color, byte[] data, byte block = 0x80)
    {
        rig.Mem.SetSpriteData(block, data);
        rig.Mem.SetSpritePointer(n, block);
        rig.Vic.Write(n * 2, (byte)x);
        rig.Vic.Write(n * 2 + 1, (byte)y);
        byte msb = rig.Vic.Peek(0x10);
        msb = (x & 0x100) != 0 ? (byte)(msb | (1 << n)) : (byte)(msb & ~(1 << n));
        rig.Vic.Write(0x10, msb);
        rig.Vic.Write(0x27 + n, color);
        rig.Vic.Write(0x15, (byte)(rig.Vic.Peek(0x15) | (1 << n)));
    }

    [Test]
    public void Sprite_AtX24Y50_AppearsAtDisplayWindowTopLeft()
    {
        var rig = new Rig();
        EnableSprite(rig, 0, 24, 50, 1, FakeVicMemory.SolidSprite());
        rig.RunFrame();
        rig.Save("sprite_topleft");

        Assert.That(rig.Pixel(Left, Top), Is.EqualTo(1), "top-left sprite pixel");
        Assert.That(rig.Pixel(Left - 1, Top), Is.EqualTo(Border));
        Assert.That(rig.Pixel(Left, Top - 1), Is.EqualTo(Border));
        Assert.That(rig.Pixel(Left + 23, Top), Is.EqualTo(1));
        Assert.That(rig.Pixel(Left + 24, Top), Is.EqualTo(Background));
        Assert.That(rig.Pixel(Left, Top + 20), Is.EqualTo(1), "21st sprite line");
        Assert.That(rig.Pixel(Left, Top + 21), Is.EqualTo(Background));
        Assert.That(rig.Pixel(Left + 23, Top + 20), Is.EqualTo(1));
    }

    [Test]
    public void Sprite_XMsbMovesSpriteBy256()
    {
        var rig = new Rig();
        EnableSprite(rig, 1, 256 + 24, 50, 1, FakeVicMemory.SolidSprite());
        rig.RunFrame();
        rig.Save("sprite_xmsb");
        Assert.That(rig.Pixel(Left, Top), Is.EqualTo(Background));
        Assert.That(rig.Pixel(Left + 256, Top), Is.EqualTo(1));
        Assert.That(rig.Pixel(Left + 256 + 23, Top), Is.EqualTo(1));
        Assert.That(rig.Pixel(Left + 256 + 24, Top), Is.EqualTo(Background));
    }

    [Test]
    public void Sprite_XAndYExpansionDoubleTheSize()
    {
        var rig = new Rig();
        EnableSprite(rig, 0, 24, 50, 1, FakeVicMemory.RowSprite(0x80, 0x00, 0x01));   // first and last pixel of each row
        rig.Vic.Write(0x1D, 0x01);
        rig.Vic.Write(0x17, 0x01);
        rig.RunFrame();
        rig.Save("sprite_expanded");
        Assert.That(rig.Pixel(Left, Top), Is.EqualTo(1));
        Assert.That(rig.Pixel(Left + 1, Top), Is.EqualTo(1), "X expansion: 2 pixels per bit");
        Assert.That(rig.Pixel(Left + 2, Top), Is.EqualTo(Background));
        Assert.That(rig.Pixel(Left + 46, Top), Is.EqualTo(1));
        Assert.That(rig.Pixel(Left + 47, Top), Is.EqualTo(1));
        Assert.That(rig.Pixel(Left + 48, Top), Is.EqualTo(Background));
        Assert.That(rig.Pixel(Left, Top + 41), Is.EqualTo(1), "Y expansion: 42 lines");
        Assert.That(rig.Pixel(Left, Top + 42), Is.EqualTo(Background));
    }

    [Test]
    public void Sprite_MulticolorPairs()
    {
        var rig = new Rig();
        rig.Vic.Write(0x25, 2);
        rig.Vic.Write(0x26, 3);
        EnableSprite(rig, 0, 24, 50, 1, FakeVicMemory.RowSprite(0x1B, 0x1B, 0x1B));   // 00 01 10 11
        rig.Vic.Write(0x1C, 0x01);
        rig.RunFrame();
        rig.Save("sprite_multicolor");
        int[] expected = { Background, Background, 2, 2, 1, 1, 3, 3 };
        for (int c = 0; c < 8; c++)
            Assert.That(rig.Pixel(Left + c, Top), Is.EqualTo(expected[c]), $"pixel {c}");
        Assert.That(rig.Pixel(Left + 23, Top), Is.EqualTo(3));
        Assert.That(rig.Pixel(Left + 24, Top), Is.EqualTo(Background));
    }

    [Test]
    public void Sprite_PriorityBitPutsSpriteBehindForeground()
    {
        var rig = new Rig();
        rig.Mem.SetChar(0, 0, FakeVicMemory.CharLeftHalf);
        EnableSprite(rig, 0, 24, 50, 5, FakeVicMemory.SolidSprite());
        rig.Vic.Write(0x1B, 0x01);                       // sprite 0 behind foreground graphics
        rig.RunFrame();
        rig.Save("sprite_behind");
        Assert.That(rig.Pixel(Left, Top), Is.EqualTo(1), "foreground graphics in front");
        Assert.That(rig.Pixel(Left + 4, Top), Is.EqualTo(5), "sprite in front of background");

        rig.Vic.Write(0x1B, 0x00);
        rig.RunFrame();
        rig.Save("sprite_infront");
        Assert.That(rig.Pixel(Left, Top), Is.EqualTo(5));
        Assert.That(rig.Pixel(Left + 4, Top), Is.EqualTo(5));
    }

    [Test]
    public void Sprite_LowerNumberInFront()
    {
        var rig = new Rig();
        EnableSprite(rig, 3, 24, 50, 3, FakeVicMemory.SolidSprite(), 0x80);
        EnableSprite(rig, 5, 24, 50, 5, FakeVicMemory.SolidSprite(), 0x81);
        rig.RunFrame();
        rig.Save("sprite_priority");
        Assert.That(rig.Pixel(Left, Top), Is.EqualTo(3));
        Assert.That(rig.Vic.Peek(0x1E), Is.EqualTo((1 << 3) | (1 << 5)));
    }

    [Test]
    public void Sprite_OffScreenXIsNotShown()
    {
        var rig = new Rig();
        EnableSprite(rig, 0, 0x1E0, 50, 1, FakeVicMemory.SolidSprite());
        EnableSprite(rig, 1, 0x1F8, 50, 1, FakeVicMemory.SolidSprite());
        rig.RunFrame();
        rig.Save("sprite_offscreen");
        for (int y = 0; y < VicII.FrameHeight; y++)
            for (int x = 0; x < VicII.FrameWidth; x++)
                if (rig.Pixel(x, y) == 1)
                    Assert.Fail($"Sprite pixel visible at ({x},{y})");
    }

    // 12. Sprite DMA / BA ----------------------------------------------------------------------------------

    [Test]
    public void SpriteDma_Sprite0_BaInCycles55To59()
    {
        var rig = new Rig(den: false);                   // no bad lines, so only sprite DMA can assert BA
        EnableSprite(rig, 0, 24, 100, 1, FakeVicMemory.SolidSprite());
        rig.RunTo(99 - 1, 63);
        Assert.That(Array.IndexOf(rig.BaOfNextLine(), true), Is.EqualTo(-1), "line 99: no DMA yet");

        var ba = rig.BaOfNextLine();                     // line 100 (not a bad line: 100 & 7 = 4)
        for (int c = 1; c <= 63; c++)
            Assert.That(ba[c], Is.EqualTo(c >= 55 && c <= 59), $"line 100 BA in cycle {c}");

        rig.RunTo(120 - 1, 63);
        var last = rig.BaOfNextLine();                   // line 120: last line with DMA (MCBASE 60 -> 63 in line 121)
        Assert.That(last[55] && last[59], Is.True);

        var off = rig.BaOfNextLine();                    // line 121: DMA off in cycle 16
        Assert.That(Array.IndexOf(off, true), Is.EqualTo(-1), "line 121: no sprite BA");
    }

    [Test]
    public void SpriteDma_Sprite3_BaInCycles61To63And1To2()
    {
        var rig = new Rig(den: false);
        EnableSprite(rig, 3, 24, 100, 1, FakeVicMemory.SolidSprite());
        rig.RunTo(100 - 1, 63);
        var ba = rig.BaOfNextLine();
        for (int c = 1; c <= 63; c++)
            Assert.That(ba[c], Is.EqualTo(c >= 61), $"line 100 BA in cycle {c}");
        var next = rig.BaOfNextLine();                   // line 101 (101 & 7 = 5, not a bad line)
        for (int c = 1; c <= 60; c++)
            Assert.That(next[c], Is.EqualTo(c <= 2), $"line 101 BA in cycle {c}");
    }

    [Test]
    public void SpriteDma_NoBaWhenDisabledOrOutsideYRange()
    {
        var rig = new Rig(den: false);
        EnableSprite(rig, 0, 24, 100, 1, FakeVicMemory.SolidSprite());
        rig.Vic.Write(0x15, 0x00);                       // disabled
        rig.RunTo(100 - 1, 63);
        Assert.That(Array.IndexOf(rig.BaOfNextLine(), true), Is.EqualTo(-1));

        rig.Vic.Write(0x15, 0x01);
        rig.RunTo(150 - 1, 63);                          // outside the 21 lines from Y
        Assert.That(Array.IndexOf(rig.BaOfNextLine(), true), Is.EqualTo(-1));
    }

    // 13. Collisions ---------------------------------------------------------------------------------------

    [Test]
    public void SpriteSpriteCollision_SetsBothBitsAndIrq_ClearedOnRead()
    {
        var rig = new Rig();
        EnableSprite(rig, 0, 24, 50, 1, FakeVicMemory.SolidSprite(), 0x80);
        EnableSprite(rig, 2, 40, 60, 2, FakeVicMemory.SolidSprite(), 0x81);
        rig.Vic.Write(0x1A, VicII.IrqSpriteSprite);
        rig.RunFrame();
        rig.Save("collision_sprite_sprite");

        Assert.That(rig.Vic.Irq, Is.True);
        Assert.That(rig.Vic.Peek(0x19) & (0x80 | VicII.IrqSpriteSprite), Is.EqualTo(0x80 | VicII.IrqSpriteSprite));
        Assert.That(rig.Vic.Peek(0x19) & VicII.IrqSpriteData, Is.EqualTo(0));
        Assert.That(rig.Vic.Peek(0x1E), Is.EqualTo(0x05));
        Assert.That(rig.Vic.Read(0x1E), Is.EqualTo(0x05));
        Assert.That(rig.Vic.Read(0x1E), Is.EqualTo(0x00), "cleared on read");
        Assert.That(rig.Vic.Peek(0x1F), Is.EqualTo(0), "no graphics collision on a blank screen");
        rig.Vic.Write(0x19, VicII.IrqSpriteSprite);
        Assert.That(rig.Vic.Irq, Is.False);
    }

    [Test]
    public void SpriteBackgroundCollision_OnlyWithForegroundPixels()
    {
        var rig = new Rig();
        rig.Mem.SetChar(0, 0, FakeVicMemory.CharLeftHalf);
        rig.Vic.Write(0x1A, VicII.IrqSpriteData);
        EnableSprite(rig, 0, 28, 50, 1, FakeVicMemory.SolidSprite());   // covers the right (background) half and blank cells
        rig.RunFrame();
        rig.Save("collision_none");
        Assert.That(rig.Vic.Peek(0x1F), Is.EqualTo(0));
        Assert.That(rig.Vic.Irq, Is.False);

        rig.Vic.Write(0x00, 24);                         // now over the foreground half
        rig.RunFrame();
        rig.Save("collision_sprite_data");
        Assert.That(rig.Vic.Peek(0x1F), Is.EqualTo(0x01));
        Assert.That(rig.Vic.Irq, Is.True);
        Assert.That(rig.Vic.Read(0x1F), Is.EqualTo(0x01));
        Assert.That(rig.Vic.Read(0x1F), Is.EqualTo(0x00));
    }

    [Test]
    public void SpriteBackgroundCollision_MulticolorOnlyPairs10And11()
    {
        var rig = new Rig();
        rig.Vic.Write(0x16, 0x18);                       // MCM
        rig.Mem.SetChar(0, 0, FakeVicMemory.CharPairs01);          // "01" pairs: background for collisions
        rig.Mem.SetColor(0, 0, 8 | 1);
        EnableSprite(rig, 0, 24, 50, 1, FakeVicMemory.RowSprite(0xFF, 0x00, 0x00));
        rig.RunFrame();
        Assert.That(rig.Vic.Read(0x1F), Is.EqualTo(0), "'01' pixels do not collide");

        rig.Mem.SetChar(0, 0, FakeVicMemory.CharCheckerboard);      // "10" on even rows: foreground
        rig.RunFrame();
        rig.Save("collision_multicolor");
        Assert.That(rig.Vic.Read(0x1F), Is.EqualTo(1));
    }

    [Test]
    public void Collision_SecondCollisionRaisesNoIrqUntilRegisterIsRead()
    {
        var rig = new Rig();
        EnableSprite(rig, 0, 24, 50, 1, FakeVicMemory.SolidSprite(), 0x80);
        EnableSprite(rig, 1, 24, 50, 2, FakeVicMemory.SolidSprite(), 0x81);
        rig.Vic.Write(0x1A, VicII.IrqSpriteSprite);
        rig.RunFrame();
        Assert.That(rig.Vic.Irq, Is.True);
        rig.Vic.Write(0x19, VicII.IrqSpriteSprite);      // acknowledge without reading $D01E
        Assert.That(rig.Vic.Irq, Is.False);

        rig.RunFrame();                                  // collides again every line
        Assert.That(rig.Vic.Irq, Is.False, "no new IRQ while the collision register is still set");
        Assert.That(rig.Vic.Peek(0x1E), Is.EqualTo(0x03));

        rig.Vic.Read(0x1E);                              // clear
        rig.RunFrame();
        Assert.That(rig.Vic.Irq, Is.True, "first collision after reading raises the IRQ again");
    }

    // 14. Raster IRQ ---------------------------------------------------------------------------------------

    [Test]
    public void RasterIrq_RaisedInCycle1OfTheCompareLine()
    {
        var rig = new Rig();
        rig.Vic.Write(0x12, 100);
        rig.Vic.Write(0x1A, VicII.IrqRaster);
        rig.RunTo(99, 63);
        Assert.That(rig.Vic.Irq, Is.False);
        rig.Vic.Clock();
        Assert.That((rig.Vic.RasterLine, rig.Vic.RasterCycle), Is.EqualTo((100, 1)));
        Assert.That(rig.Vic.Irq, Is.True);
        Assert.That(rig.Vic.Peek(0x19), Is.EqualTo(0xF1));
        Assert.That(rig.Vic.Peek(0x12), Is.EqualTo(100));

        rig.Vic.Write(0x19, 0x01);                       // acknowledge
        Assert.That(rig.Vic.Irq, Is.False);
        Assert.That(rig.Vic.Peek(0x19), Is.EqualTo(0x70));
        rig.RunTo(101, 5);
        Assert.That(rig.Vic.Irq, Is.False, "only once per frame");
    }

    [Test]
    public void RasterIrq_Line0_RaisedInCycle2()
    {
        var rig = new Rig();
        rig.Vic.Write(0x12, 0);
        rig.Vic.Write(0x1A, VicII.IrqRaster);
        rig.RunFrame();
        rig.Vic.Write(0x19, 0x0F);
        Assert.That(rig.Vic.Irq, Is.False);
        rig.Vic.Clock();                                 // line 0 cycle 1: RASTER still reads 311
        Assert.That(rig.Vic.Irq, Is.False);
        Assert.That(rig.Vic.Peek(0x12), Is.EqualTo(311 & 0xFF));
        Assert.That(rig.Vic.Peek(0x11) & 0x80, Is.EqualTo(0x80));
        rig.Vic.Clock();                                 // line 0 cycle 2
        Assert.That(rig.Vic.Irq, Is.True);
        Assert.That(rig.Vic.Peek(0x12), Is.EqualTo(0));
        Assert.That(rig.Vic.Peek(0x11) & 0x80, Is.EqualTo(0));
    }

    [Test]
    public void RasterIrq_HighLineViaD011Bit7_AndDisabledIrq()
    {
        var rig = new Rig();
        rig.Vic.Write(0x11, 0x9B);                       // compare bit 8
        rig.Vic.Write(0x12, 300 & 0xFF);
        rig.RunTo(300, 1);
        Assert.That(rig.Vic.Irq, Is.False, "latch set but not enabled");
        Assert.That(rig.Vic.Peek(0x19), Is.EqualTo(0x71));
        rig.Vic.Write(0x1A, VicII.IrqRaster);
        Assert.That(rig.Vic.Irq, Is.True, "enabling a pending latch asserts IRQ");
        Assert.That(rig.Vic.Peek(0x19), Is.EqualTo(0xF1));
        rig.Vic.Write(0x19, 0xFF);
        Assert.That(rig.Vic.Peek(0x19), Is.EqualTo(0x70));

        // Only line 300 matched, not line 44 (compare value is 9 bits).
        rig.Vic.Write(0x11, 0x1B);                       // clear bit 8 -> compare 44 for the next frame
        rig.RunTo(44, 1);
        Assert.That(rig.Vic.Irq, Is.True);
    }

    // 15. Registers ----------------------------------------------------------------------------------------

    [Test]
    public void Registers_ReadBackRules()
    {
        var mem = new FakeVicMemory();
        var vic = new VicII(mem);
        vic.Write(0x16, 0x00);
        Assert.That(vic.Read(0x16), Is.EqualTo(0xC0));
        vic.Write(0x16, 0x3F);
        Assert.That(vic.Read(0x16), Is.EqualTo(0xFF));
        vic.Write(0x18, 0x14);
        Assert.That(vic.Read(0x18), Is.EqualTo(0x15));
        Assert.That(vic.Read(0x19), Is.EqualTo(0x70));
        vic.Write(0x1A, 0x00);
        Assert.That(vic.Read(0x1A), Is.EqualTo(0xF0));
        vic.Write(0x1A, 0x05);
        Assert.That(vic.Read(0x1A), Is.EqualTo(0xF5));
        for (int r = 0x20; r <= 0x2E; r++)
        {
            vic.Write(r, (byte)(r & 0x0F));
            Assert.That(vic.Read(r), Is.EqualTo(0xF0 | (r & 0x0F)), $"register {r:X2}");
        }
        for (int r = 0x2F; r <= 0x3F; r++)
        {
            vic.Write(r, 0x00);
            Assert.That(vic.Read(r), Is.EqualTo(0xFF), $"register {r:X2}");
        }
        vic.Write(0x00, 0xAB);
        Assert.That(vic.Read(0x00), Is.EqualTo(0xAB));
        vic.Write(0x11, 0x1B);
        Assert.That(vic.Read(0x11), Is.EqualTo(0x1B));
        vic.Write(0x11, 0x9B);
        Assert.That(vic.Read(0x11), Is.EqualTo(0x1B), "bit 7 reads the raster, not the written value");
        vic.Write(0x12, 0x55);
        Assert.That(vic.Read(0x12), Is.EqualTo(0x00), "$D012 reads the current raster line");
    }

    [Test]
    public void Registers_RasterReadBack()
    {
        var rig = new Rig();
        rig.RunTo(200, 10);
        Assert.That(rig.Vic.Read(0x12), Is.EqualTo(200));
        Assert.That(rig.Vic.Read(0x11) & 0x80, Is.EqualTo(0));
        rig.RunTo(300, 10);
        Assert.That(rig.Vic.Read(0x12), Is.EqualTo(300 & 0xFF));
        Assert.That(rig.Vic.Read(0x11) & 0x80, Is.EqualTo(0x80));
        rig.RunTo(311, 63);
        Assert.That(rig.Vic.Read(0x12), Is.EqualTo(311 & 0xFF));
        rig.RunTo(0, 1);
        Assert.That(rig.Vic.Read(0x12), Is.EqualTo(311 & 0xFF), "raster wraps in cycle 2 of line 0");
        rig.RunTo(0, 2);
        Assert.That(rig.Vic.Read(0x12), Is.EqualTo(0));
        rig.RunTo(1, 1);
        Assert.That(rig.Vic.Read(0x12), Is.EqualTo(1), "other lines increment in cycle 1");
    }

    [Test]
    public void LightPen_LatchesOncePerFrame()
    {
        var rig = new Rig();
        rig.Vic.Write(0x1A, VicII.IrqLightPen);
        rig.RunTo(100, 20);
        rig.Vic.SetLightPen(true);
        int expectedX = VicII.FrameColumnToX(19 * 8);
        Assert.That(rig.Vic.Read(0x13), Is.EqualTo(expectedX >> 1));
        Assert.That(rig.Vic.Read(0x14), Is.EqualTo(100));
        Assert.That(rig.Vic.Irq, Is.True);
        Assert.That(rig.Vic.Peek(0x19) & (0x80 | VicII.IrqLightPen), Is.EqualTo(0x80 | VicII.IrqLightPen));

        rig.Vic.SetLightPen(false);
        rig.RunTo(150, 20);
        rig.Vic.SetLightPen(true);
        Assert.That(rig.Vic.Read(0x14), Is.EqualTo(100), "only the first trigger per frame is latched");
        rig.Vic.SetLightPen(false);

        rig.RunTo(120, 30);                              // next frame
        rig.Vic.SetLightPen(true);
        Assert.That(rig.Vic.Read(0x14), Is.EqualTo(120));
        rig.Vic.Write(0x13, 0);
        Assert.That(rig.Vic.Read(0x13), Is.EqualTo(VicII.FrameColumnToX(29 * 8) >> 1), "latches are read-only");
    }

    // 16. Idle state ---------------------------------------------------------------------------------------

    [Test]
    public void IdleState_ShowsDataFrom3FFF()
    {
        var rig = new Rig(den: false);
        rig.Mem.FillScreen(FakeVicMemory.CharSolid);     // would be visible in display state
        rig.Mem.Ram[0x3FFF] = 0xAA;
        rig.Mem.Ram[0x39FF] = 0xF0;
        rig.RunTo(49, 63);                               // after line $30: DEN is not latched, no bad lines
        rig.Vic.Write(0x11, 0x1B);                       // DEN on before the top border check at line 51
        rig.RunFrame();
        rig.Save("idle_3fff");

        Assert.That(rig.Vic.DisplayState, Is.False);
        for (int c = 0; c < 8; c++)
            Assert.That(rig.Pixel(Left + c, Top + 10), Is.EqualTo((c & 1) == 0 ? 0 : Background), $"pixel {c}: $AA pattern in black on background");
        Assert.That(rig.Pixel(Left + 319, Top + 199), Is.EqualTo(Background));
        Assert.That(rig.Pixel(Left + 318, Top + 199), Is.EqualTo(0));

        // ECM: idle accesses read $39FF. DEN must again be off while line $30 passes, then on before line 51.
        rig.Vic.Write(0x11, 0x4B);
        rig.RunTo(49, 63);
        rig.Vic.Write(0x11, 0x5B);
        rig.RunFrame();
        rig.Save("idle_39ff");
        Assert.That(rig.Vic.DisplayState, Is.False);
        Assert.That(rig.Pixel(Left + 3, Top + 10), Is.EqualTo(0));
        Assert.That(rig.Pixel(Left + 4, Top + 10), Is.EqualTo(Background));
    }

    [Test]
    public void DisplayState_EntersOnBadLineAndLeavesAfterRc7()
    {
        var rig = new Rig();
        rig.RunTo(0x33 - 1, 63);
        Assert.That(rig.Vic.DisplayState, Is.False);
        rig.Vic.Clock();
        Assert.That(rig.Vic.DisplayState, Is.True, "display state as soon as the bad line condition holds");
        rig.RunTo(0xF3 + 7, 57);                         // last bad line with YSCROLL 3 is $F3, RC = 7 in line 250
        Assert.That(rig.Vic.DisplayState, Is.True);
        rig.Vic.Clock();                                 // cycle 58: RC = 7 -> idle
        Assert.That(rig.Vic.DisplayState, Is.False);
        Assert.That((rig.Vic.RasterLine, rig.Vic.RasterCycle), Is.EqualTo((250, 58)));
    }

    // 17. PngWriter ----------------------------------------------------------------------------------------

    [Test]
    public void PngWriter_EncodesSignatureAndHeader()
    {
        var pixels = new uint[4 * 3];
        for (int i = 0; i < pixels.Length; i++)
            pixels[i] = VicII.Palette[i];
        var png = PngWriter.Encode(4, 3, pixels);

        Assert.That(png.Length, Is.GreaterThan(8 + 25 + 12 + 12));
        Assert.That(png.Length, Is.LessThan(200));
        Assert.That(png[..8], Is.EqualTo(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }));
        Assert.That(png[8..16], Is.EqualTo(new byte[] { 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' }));
        Assert.That(png[16..24], Is.EqualTo(new byte[] { 0, 0, 0, 4, 0, 0, 0, 3 }), "width 4, height 3");
        Assert.That(png[24..29], Is.EqualTo(new byte[] { 8, 2, 0, 0, 0 }), "8 bit RGB");
        uint crc = (uint)(png[29] << 24 | png[30] << 16 | png[31] << 8 | png[32]);
        Assert.That(crc, Is.EqualTo(PngWriter.Crc32(png.AsSpan(12, 17))));
        Assert.That(png[^12..^8], Is.EqualTo(new byte[] { 0, 0, 0, 0 }));
        Assert.That(png[^8..^4], Is.EqualTo(new byte[] { (byte)'I', (byte)'E', (byte)'N', (byte)'D' }));
        Assert.That(png[^4..], Is.EqualTo(new byte[] { 0xAE, 0x42, 0x60, 0x82 }), "CRC of IEND");
        Assert.That(PngWriter.Crc32("123456789"u8), Is.EqualTo(0xCBF43926u));

        var path = Path.Combine(ResultsDirectory, "vic_pngwriter_small.png");
        PngWriter.Save(path, 4, 3, pixels);
        Assert.That(File.ReadAllBytes(path), Is.EqualTo(png));
    }

    // Performance ------------------------------------------------------------------------------------------

    [Test]
    public void Performance_FramesRenderQuickly()
    {
        var rig = new Rig();
        rig.Mem.FillScreen(FakeVicMemory.CharA);
        for (int n = 0; n < 8; n++)
            EnableSprite(rig, n, 24 + n * 30, 50 + n * 10, (byte)n, FakeVicMemory.SolidSprite(), (byte)(0x80 + n));
        for (int i = 0; i < 25; i++)                     // warm up (tiered JIT)
            rig.RunFrame();
        const int frames = 25;
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < frames; i++)
            rig.RunFrame();
        sw.Stop();
        rig.Save("performance");
        TestContext.Out.WriteLine($"{frames} frames with 8 sprites: {sw.Elapsed.TotalMilliseconds:F1} ms ({sw.Elapsed.TotalMilliseconds / frames:F2} ms/frame)");
        Assert.That(sw.ElapsedMilliseconds, Is.LessThan(2000));
    }
}
