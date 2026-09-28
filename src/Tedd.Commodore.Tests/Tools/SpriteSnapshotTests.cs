using NUnit.Framework;
using Tedd.Commodore.Emulator.Tools;
using Tedd.Commodore.Emulator.Video;
using Tedd.Commodore.Tests.Support;

namespace Tedd.Commodore.Tests.Tools;

/// <summary>
/// <see cref="SpriteSnapshot"/>, the sprite decoding behind the GUI's sprite viewer: register decoding,
/// pointers and data addresses, the pixels of single color and multicolor sprites, and the internal DMA state.
/// </summary>
[TestFixture]
public class SpriteSnapshotTests
{
    private const int Screen = FakeVicMemory.DefaultScreen;   // $0400, so the pointers are at $07F8
    private const int CyclesPerFrame = VicII.CyclesPerLine * VicII.LinesPerFrame;

    private FakeVicMemory _mem = null!;
    private VicII _vic = null!;

    [SetUp]
    public void SetUp()
    {
        _mem = new FakeVicMemory();
        _vic = new VicII(_mem);
        _vic.Write(0x18, 0x14);                  // video matrix $0400
        _vic.Write(0x11, 0x1B);                  // DEN, RSEL, YSCROLL 3
        _vic.Write(0x16, 0x08);                  // CSEL
    }

    /// <summary>Puts <paramref name="data"/> in block <paramref name="block"/> and points sprite n at it.</summary>
    private void SetSprite(int n, int x, int y, byte color, byte[] data, byte block = 0x80)
    {
        _mem.SetSpriteData(block, data);
        _mem.SetSpritePointer(n, block, Screen);
        _vic.Write(n << 1, (byte)(x & 0xFF));
        _vic.Write(0x10, (byte)((_vic.Peek(0x10) & ~(1 << n)) | ((x >> 8) & 1) << n));
        _vic.Write((n << 1) | 1, (byte)y);
        _vic.Write(0x27 + n, color);
        _vic.Write(0x15, (byte)(_vic.Peek(0x15) | (1 << n)));
    }

    /// <summary>Clocks until the last executed cycle is (<paramref name="line"/>, <paramref name="cycle"/>).</summary>
    private void RunTo(int line, int cycle)
    {
        for (int guard = 0; guard < 2 * CyclesPerFrame; guard++)
        {
            _vic.Clock();
            if (_vic.RasterLine == line && _vic.RasterCycle == cycle) return;
        }
        Assert.Fail("Did not reach the requested raster position.");
    }

    // 1. Registers ---------------------------------------------------------------------------------------

    [Test]
    public void Capture_ReadsPositionColorAndFlags()
    {
        SetSprite(0, 0x148, 100, 7, FakeVicMemory.SolidSprite());
        _vic.Write(0x1C, 0x01);                  // sprite 0 multicolor
        _vic.Write(0x1D, 0x01);                  // X expanded
        _vic.Write(0x17, 0x01);                  // Y expanded
        _vic.Write(0x1B, 0x01);                  // behind foreground graphics
        _vic.Write(0x25, 0x02);
        _vic.Write(0x26, 0x0A);

        var s = SpriteSnapshot.Capture(_vic)[0];

        Assert.Multiple(() =>
        {
            Assert.That(s.Enabled, Is.True);
            Assert.That(s.X, Is.EqualTo(0x148), "X takes bit 8 from $D010");
            Assert.That(s.Y, Is.EqualTo(100));
            Assert.That(s.DisplayX, Is.EqualTo(0x148 - 24));
            Assert.That(s.DisplayY, Is.EqualTo(50));
            Assert.That(s.Color, Is.EqualTo(7));
            Assert.That(s.Multicolor, Is.True);
            Assert.That(s.ExpandX, Is.True);
            Assert.That(s.ExpandY, Is.True);
            Assert.That(s.BehindForeground, Is.True);
            Assert.That(s.PixelWidth, Is.EqualTo(48));
            Assert.That(s.PixelHeight, Is.EqualTo(42));
            Assert.That(s.MulticolorColor0, Is.EqualTo(2));
            Assert.That(s.MulticolorColor1, Is.EqualTo(10));
        });
    }

    [Test]
    public void Capture_DisabledSpritesAreReported()
    {
        SetSprite(3, 24, 50, 1, FakeVicMemory.SolidSprite());
        var snapshot = SpriteSnapshot.Capture(_vic);
        Assert.That(snapshot.EnableRegister, Is.EqualTo(0x08));
        Assert.That(snapshot[3].Enabled, Is.True);
        Assert.That(snapshot[2].Enabled, Is.False);
        Assert.That(snapshot.Sprites, Has.Count.EqualTo(8));
    }

    [Test]
    public void Capture_PointerAndDataAddressFollowTheVideoMatrixAndBank()
    {
        SetSprite(5, 24, 50, 1, FakeVicMemory.RowSprite(0xAB, 0xCD, 0xEF), block: 0x40);

        var s = SpriteSnapshot.Capture(_vic, vicBank: 2)[5];

        Assert.Multiple(() =>
        {
            Assert.That(s.PointerAddress, Is.EqualTo(Screen + 0x3F8 + 5));
            Assert.That(s.CpuPointerAddress, Is.EqualTo(0x8000 + Screen + 0x3F8 + 5), "bank 2 starts at $8000");
            Assert.That(s.Pointer, Is.EqualTo(0x40));
            Assert.That(s.DataAddress, Is.EqualTo(0x40 * 64));
            Assert.That(s.CpuDataAddress, Is.EqualTo(0x8000 + 0x40 * 64));
            Assert.That(s.Data[0], Is.EqualTo(0xAB));
            Assert.That(s.Data[1], Is.EqualTo(0xCD));
            Assert.That(s.Data[2], Is.EqualTo(0xEF));
            Assert.That(s.Data, Has.Length.EqualTo(63));
        });
    }

    [Test]
    public void Capture_DoesNotTouchTheBusOrClearTheCollisionRegisters()
    {
        SetSprite(0, 24, 50, 1, FakeVicMemory.SolidSprite());
        SetSprite(1, 24, 50, 2, FakeVicMemory.SolidSprite(), block: 0x81);
        RunTo(60, 20);                                    // let the two sprites collide
        Assume.That(_vic.Peek(0x1E), Is.Not.Zero);
        long reads = _mem.ReadCount;

        var snapshot = SpriteSnapshot.Capture(_vic);

        Assert.Multiple(() =>
        {
            Assert.That(_mem.ReadCount, Is.EqualTo(reads), "the snapshot must peek, not read");
            Assert.That(_vic.Peek(0x1E), Is.Not.Zero, "reading $D01E through the snapshot must not clear it");
            Assert.That(snapshot.SpriteSpriteCollisions & 0x03, Is.EqualTo(0x03));
            Assert.That(snapshot[0].SpriteCollision, Is.True);
            Assert.That(snapshot[2].SpriteCollision, Is.False);
        });
    }

    // 2. Pixels ------------------------------------------------------------------------------------------

    [Test]
    public void Render_SingleColor_SetBitsAreTheSpriteColorAndClearBitsTransparent()
    {
        SetSprite(0, 24, 50, 14, FakeVicMemory.RowSprite(0x80, 0x00, 0x01));   // first and last pixel of a row
        var pixels = new byte[SpriteSnapshot.Width * SpriteSnapshot.Height];

        SpriteSnapshot.Capture(_vic)[0].Render(pixels);

        Assert.Multiple(() =>
        {
            Assert.That(pixels[0], Is.EqualTo(14));
            Assert.That(pixels[23], Is.EqualTo(14));
            Assert.That(pixels[1], Is.EqualTo(SpriteSnapshot.Transparent));
            Assert.That(pixels[22], Is.EqualTo(SpriteSnapshot.Transparent));
            Assert.That(pixels[20 * SpriteSnapshot.Width], Is.EqualTo(14), "the 21st row is data bytes 60-62");
        });
    }

    [Test]
    public void Render_Multicolor_PairsSelectTheThreeColorsAndAreTwoPixelsWide()
    {
        // %00011011 = the four pairs 00 01 10 11.
        SetSprite(0, 24, 50, 5, FakeVicMemory.RowSprite(0x1B, 0x1B, 0x1B));
        _vic.Write(0x1C, 0x01);
        _vic.Write(0x25, 0x03);
        _vic.Write(0x26, 0x09);
        var pixels = new byte[SpriteSnapshot.Width * SpriteSnapshot.Height];

        SpriteSnapshot.Capture(_vic)[0].Render(pixels);

        Assert.Multiple(() =>
        {
            Assert.That(pixels[0], Is.EqualTo(SpriteSnapshot.Transparent), "00 = transparent");
            Assert.That(pixels[1], Is.EqualTo(SpriteSnapshot.Transparent));
            Assert.That(pixels[2], Is.EqualTo(3), "01 = $D025");
            Assert.That(pixels[3], Is.EqualTo(3));
            Assert.That(pixels[4], Is.EqualTo(5), "10 = the sprite color");
            Assert.That(pixels[5], Is.EqualTo(5));
            Assert.That(pixels[6], Is.EqualTo(9), "11 = $D026");
            Assert.That(pixels[7], Is.EqualTo(9));
            Assert.That(pixels[8], Is.EqualTo(SpriteSnapshot.Transparent), "the next byte repeats the pattern");
        });
    }

    [Test]
    public void SolidPixelCount_CountsWhatIsActuallyDrawn()
    {
        SetSprite(0, 24, 50, 1, FakeVicMemory.SolidSprite());
        SetSprite(1, 24, 50, 1, FakeVicMemory.RowSprite(0x1B, 0x00, 0x00), block: 0x81);
        _vic.Write(0x1C, 0x02);                            // sprite 1 is multicolor
        var snapshot = SpriteSnapshot.Capture(_vic);

        Assert.Multiple(() =>
        {
            Assert.That(snapshot[0].SolidPixelCount(), Is.EqualTo(24 * 21), "every bit set");
            // Three of the four pairs of $1B are non-transparent and each covers two pixels.
            Assert.That(snapshot[1].SolidPixelCount(), Is.EqualTo(3 * 2 * 21));
        });
    }

    // 3. Sequencer state ---------------------------------------------------------------------------------

    [Test]
    public void Capture_ReportsDmaAndDisplayStateOfTheCurrentLine()
    {
        SetSprite(0, 24, 100, 1, FakeVicMemory.SolidSprite());
        RunTo(99, 63);
        Assert.That(SpriteSnapshot.Capture(_vic)[0].DmaActive, Is.False, "before the Y compare");

        RunTo(100, 58);                                    // DMA turns on in cycle 55/56, display in cycle 58
        var snapshot = SpriteSnapshot.Capture(_vic);

        Assert.Multiple(() =>
        {
            Assert.That(snapshot[0].DmaActive, Is.True);
            Assert.That(snapshot[0].Displayed, Is.True);
            Assert.That(snapshot[0].LatchedPointer, Is.EqualTo(0x80), "the p-access of cycle 58 latched the pointer");
            Assert.That(snapshot.ActiveDmaCount, Is.EqualTo(1));
            Assert.That(snapshot[1].DmaActive, Is.False);
        });
    }

    [Test]
    public void Capture_McFollowsTheDataFetchesOfTheLine()
    {
        SetSprite(0, 24, 100, 1, FakeVicMemory.SolidSprite());
        // Sprite 0 fetches its three bytes in cycles 58-59 of every line it is displayed on, and MCBASE is
        // advanced by 3 in cycles 15/16; by cycle 60 of the second line two lines have been fetched (3.8.1).
        RunTo(101, 60);
        var s = SpriteSnapshot.Capture(_vic)[0];

        Assert.Multiple(() =>
        {
            Assert.That(s.Mc, Is.EqualTo(6), "two lines of three bytes fetched");
            Assert.That(s.CurrentLine, Is.EqualTo(2), "MC points at the line to be fetched next");
            Assert.That(s.McBase, Is.EqualTo(3), "MCBASE follows one line behind");
        });
    }

    [Test]
    public void Update_RefillsTheSameInstance()
    {
        SetSprite(0, 24, 50, 1, FakeVicMemory.SolidSprite());
        var snapshot = SpriteSnapshot.Capture(_vic);
        var first = snapshot[0];
        Assume.That(first.Color, Is.EqualTo(1));

        _vic.Write(0x27, 12);
        _vic.Write(0x00, 200);
        snapshot.Update(_vic);

        Assert.Multiple(() =>
        {
            Assert.That(snapshot[0], Is.SameAs(first), "the sprite objects are reused");
            Assert.That(first.Color, Is.EqualTo(12));
            Assert.That(first.X, Is.EqualTo(200));
        });
    }
}
