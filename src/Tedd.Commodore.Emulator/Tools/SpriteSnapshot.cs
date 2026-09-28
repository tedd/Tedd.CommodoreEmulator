using System;
using System.Collections.Generic;
using Tedd.Commodore.Emulator.Video;

namespace Tedd.Commodore.Emulator.Tools;

/// <summary>
/// Everything a sprite viewer/debugger needs about the eight VIC-II sprites: the sprite registers
/// ($D000-$D010, $D015, $D017, $D01B-$D01F, $D025-$D02E), the sprite pointers and the 63 data bytes behind
/// them, and the internal DMA/display state of the sprite sequencers (3.8).
///
/// Capturing does not touch the machine: memory is read through <see cref="IVicMemory.PeekVic"/> and the
/// registers through <see cref="VicII.Peek"/>, so nothing is left on the bus and no read side effect (the
/// collision registers!) is triggered. <see cref="Update"/> refills the same instance, so a viewer that
/// refreshes several times per second does not allocate.
/// </summary>
public sealed class SpriteSnapshot
{
    /// <summary>Sprite width in pixels, before X expansion.</summary>
    public const int Width = 24;

    /// <summary>Sprite height in pixels/rows, before Y expansion.</summary>
    public const int Height = 21;

    /// <summary>Bytes of sprite data (3 per row); the 64th byte of the block is not used by the VIC.</summary>
    public const int DataSize = Width / 8 * Height;

    /// <summary>Value <see cref="SpriteInfo.Render"/> writes for a transparent pixel.</summary>
    public const byte Transparent = 0xFF;

    /// <summary>X coordinate of the left edge of the 40 column display window (3.9).</summary>
    public const int DisplayLeft = 24;

    /// <summary>Y coordinate of the first line of the 25 row display window (3.9).</summary>
    public const int DisplayTop = 50;

    private readonly SpriteInfo[] _sprites = new SpriteInfo[8];

    public SpriteSnapshot()
    {
        for (int n = 0; n < _sprites.Length; n++)
            _sprites[n] = new SpriteInfo(n);
    }

    /// <summary>The eight sprites, index 0..7 (0 has the highest display priority).</summary>
    public IReadOnlyList<SpriteInfo> Sprites => _sprites;

    /// <summary>Sprite <paramref name="n"/> (0..7).</summary>
    public SpriteInfo this[int n] => _sprites[n & 7];

    /// <summary>Sprite multicolor 0, $D025 (the "01" bit pair of a multicolor sprite).</summary>
    public int MulticolorColor0 { get; private set; }

    /// <summary>Sprite multicolor 1, $D026 (the "11" bit pair of a multicolor sprite).</summary>
    public int MulticolorColor1 { get; private set; }

    /// <summary>Background color 0, $D021 (what shows through a transparent sprite pixel over the background).</summary>
    public int BackgroundColor { get; private set; }

    /// <summary>$D015: one bit per enabled sprite.</summary>
    public byte EnableRegister { get; private set; }

    /// <summary>$D017: one bit per Y expanded sprite.</summary>
    public byte ExpandYRegister { get; private set; }

    /// <summary>$D01B: one bit per sprite that foreground graphics are drawn in front of.</summary>
    public byte PriorityRegister { get; private set; }

    /// <summary>$D01C: one bit per multicolor sprite.</summary>
    public byte MulticolorRegister { get; private set; }

    /// <summary>$D01D: one bit per X expanded sprite.</summary>
    public byte ExpandXRegister { get; private set; }

    /// <summary>$D01E as it stands, without clearing it: sprite-sprite collisions since it was last read (3.8.2).</summary>
    public byte SpriteSpriteCollisions { get; private set; }

    /// <summary>$D01F as it stands, without clearing it: sprite-graphics collisions since it was last read (3.8.2).</summary>
    public byte SpriteDataCollisions { get; private set; }

    /// <summary>Video matrix base within the VIC bank ($D018 bits 4-7 &lt;&lt; 6); the pointers are its last 8 bytes.</summary>
    public int VideoMatrix { get; private set; }

    /// <summary>Start of the 16 KiB VIC bank in the 64 KiB address space (CIA 2 port A), 0 when not supplied.</summary>
    public int BankBase { get; private set; }

    /// <summary>Raster line the machine was on when the snapshot was taken.</summary>
    public int RasterLine { get; private set; }

    /// <summary>Number of sprites whose DMA is currently on (0..8); each one costs the CPU 2 cycles per line.</summary>
    public int ActiveDmaCount { get; private set; }

    /// <summary>Takes a fresh snapshot of <paramref name="vic"/>.</summary>
    /// <param name="vic">The chip to read (through its side effect free views).</param>
    /// <param name="vicBank">The 16 KiB bank the VIC sees (0..3), used to report addresses as the CPU sees them.</param>
    public static SpriteSnapshot Capture(VicII vic, int vicBank = 0)
    {
        var snapshot = new SpriteSnapshot();
        snapshot.Update(vic, vicBank);
        return snapshot;
    }

    /// <summary>Refills this snapshot from <paramref name="vic"/>; see <see cref="Capture"/>.</summary>
    public void Update(VicII vic, int vicBank = 0)
    {
        if (vic is null) throw new ArgumentNullException(nameof(vic));
        var memory = vic.Memory;
        MulticolorColor0 = vic.Peek(0x25) & 0x0F;
        MulticolorColor1 = vic.Peek(0x26) & 0x0F;
        BackgroundColor = vic.Peek(0x21) & 0x0F;
        EnableRegister = vic.Peek(0x15);
        SpriteSpriteCollisions = vic.Peek(0x1E);
        SpriteDataCollisions = vic.Peek(0x1F);
        VideoMatrix = (vic.Peek(0x18) & 0xF0) << 6;
        BankBase = (vicBank & 3) << 14;
        RasterLine = vic.RasterLine;

        MulticolorRegister = vic.Peek(0x1C);
        ExpandXRegister = vic.Peek(0x1D);
        ExpandYRegister = vic.Peek(0x17);
        PriorityRegister = vic.Peek(0x1B);
        int dma = 0;
        for (int n = 0; n < _sprites.Length; n++)
        {
            int bit = 1 << n;
            var s = _sprites[n];
            s.Enabled = (EnableRegister & bit) != 0;
            s.X = vic.SpriteX(n);
            s.Y = vic.Peek((n << 1) | 1);
            s.Color = vic.Peek(0x27 + n) & 0x0F;
            s.MulticolorColor0 = MulticolorColor0;
            s.MulticolorColor1 = MulticolorColor1;
            s.Multicolor = (MulticolorRegister & bit) != 0;
            s.ExpandX = (ExpandXRegister & bit) != 0;
            s.ExpandY = (ExpandYRegister & bit) != 0;
            s.BehindForeground = (PriorityRegister & bit) != 0;
            s.SpriteCollision = (SpriteSpriteCollisions & bit) != 0;
            s.DataCollision = (SpriteDataCollisions & bit) != 0;
            s.DmaActive = vic.SpriteDma(n);
            s.Displayed = vic.SpriteDisplayed(n);
            s.ExpansionFlipFlop = vic.SpriteExpansionFlipFlop(n);
            s.Mc = vic.SpriteMc(n);
            s.McBase = vic.SpriteMcBase(n);
            s.LatchedPointer = vic.SpritePointer(n);
            s.ShiftRegister = vic.SpriteShiftRegister(n);
            s.BankBase = BankBase;

            s.PointerAddress = VideoMatrix | 0x3F8 | n;
            s.Pointer = memory.PeekVic(s.PointerAddress);
            s.DataAddress = s.Pointer << 6;
            for (int i = 0; i < DataSize; i++)
                s.Data[i] = memory.PeekVic(s.DataAddress + i);

            if (s.DmaActive) dma++;
        }
        ActiveDmaCount = dma;
    }
}

/// <summary>One sprite of a <see cref="SpriteSnapshot"/>. Rewritten in place by <see cref="SpriteSnapshot.Update"/>.</summary>
public sealed class SpriteInfo
{
    internal SpriteInfo(int index) => Index = index;

    /// <summary>Sprite number, 0..7. Lower numbers are displayed in front of higher ones (3.8.2).</summary>
    public int Index { get; }

    /// <summary>MxE, $D015: the sprite takes part in the DMA and can be displayed.</summary>
    public bool Enabled { get; internal set; }

    /// <summary>MxX, $D000 + 2n plus the MSB from $D010: 0..511. The display window starts at X = 24.</summary>
    public int X { get; internal set; }

    /// <summary>MxY, $D001 + 2n: 0..255. The display window starts at Y = 50.</summary>
    public int Y { get; internal set; }

    /// <summary>X of the top left sprite pixel relative to the display window (negative = left of it).</summary>
    public int DisplayX => X - SpriteSnapshot.DisplayLeft;

    /// <summary>Y of the top sprite line relative to the display window (negative = above it).</summary>
    public int DisplayY => Y - SpriteSnapshot.DisplayTop;

    /// <summary>MxC, $D027 + n: the sprite color (the "10" bit pair in multicolor mode).</summary>
    public int Color { get; internal set; }

    /// <summary>$D025, repeated here so a sprite can be rendered on its own.</summary>
    public int MulticolorColor0 { get; internal set; }

    /// <summary>$D026, repeated here so a sprite can be rendered on its own.</summary>
    public int MulticolorColor1 { get; internal set; }

    /// <summary>MxMC, $D01C: bit pairs instead of single bits, 12 double wide pixels per row.</summary>
    public bool Multicolor { get; internal set; }

    /// <summary>MxXE, $D01D: every pixel is twice as wide.</summary>
    public bool ExpandX { get; internal set; }

    /// <summary>MxYE, $D017: every line is displayed twice.</summary>
    public bool ExpandY { get; internal set; }

    /// <summary>MxDP, $D01B: foreground graphics are drawn in front of this sprite.</summary>
    public bool BehindForeground { get; internal set; }

    /// <summary>This sprite's bit in $D01E: it has collided with another sprite since the register was read.</summary>
    public bool SpriteCollision { get; internal set; }

    /// <summary>This sprite's bit in $D01F: it has collided with foreground graphics since the register was read.</summary>
    public bool DataCollision { get; internal set; }

    /// <summary>The sprite pointer as it is in memory right now (video matrix + $3F8 + n).</summary>
    public byte Pointer { get; internal set; }

    /// <summary>The pointer the VIC latched in the last p-access of this sprite; 0 before the first one.</summary>
    public byte LatchedPointer { get; internal set; }

    /// <summary>Address of the sprite pointer within the VIC bank.</summary>
    public int PointerAddress { get; internal set; }

    /// <summary>Address of the 64 byte data block within the VIC bank (<see cref="Pointer"/> * 64).</summary>
    public int DataAddress { get; internal set; }

    /// <summary>Start of the VIC bank, so <see cref="DataAddress"/> can be shown as the CPU sees it.</summary>
    public int BankBase { get; internal set; }

    /// <summary>Address of the sprite data as the CPU sees it (bank + <see cref="DataAddress"/>).</summary>
    public int CpuDataAddress => BankBase + DataAddress;

    /// <summary>Address of the sprite pointer as the CPU sees it (bank + <see cref="PointerAddress"/>).</summary>
    public int CpuPointerAddress => BankBase + PointerAddress;

    /// <summary>The 63 data bytes at <see cref="DataAddress"/>, three per line, MSB leftmost.</summary>
    public byte[] Data { get; } = new byte[SpriteSnapshot.DataSize];

    /// <summary>Sprite DMA is on: the VIC fetches this sprite's data and steals 2 cycles per line (3.8.1).</summary>
    public bool DmaActive { get; internal set; }

    /// <summary>The sprite is in display state, i.e. its data is being shifted out on this line (3.8.1 rule 4).</summary>
    public bool Displayed { get; internal set; }

    /// <summary>Y expansion flip-flop: when cleared MCBASE is not advanced, which doubles every line (3.8.1).</summary>
    public bool ExpansionFlipFlop { get; internal set; }

    /// <summary>MC: offset of the next s-access inside the 64 byte block (0..63).</summary>
    public int Mc { get; internal set; }

    /// <summary>MCBASE: the MC value reloaded at the start of each raster line (0..63).</summary>
    public int McBase { get; internal set; }

    /// <summary>The 24 bit shift register holding the three bytes of the line being displayed.</summary>
    public uint ShiftRegister { get; internal set; }

    /// <summary>Displayed width in pixels: 24, or 48 with X expansion.</summary>
    public int PixelWidth => ExpandX ? SpriteSnapshot.Width * 2 : SpriteSnapshot.Width;

    /// <summary>Displayed height in raster lines: 21, or 42 with Y expansion.</summary>
    public int PixelHeight => ExpandY ? SpriteSnapshot.Height * 2 : SpriteSnapshot.Height;

    /// <summary>The line of the data block MC currently points at (0..20), or -1 when MC is past the last line.</summary>
    public int CurrentLine => Mc / 3 >= SpriteSnapshot.Height ? -1 : Mc / 3;

    /// <summary>Number of non-transparent pixels, i.e. how much of the sprite is actually drawn.</summary>
    public int SolidPixelCount()
    {
        int count = 0;
        if (Multicolor)
        {
            for (int i = 0; i < Data.Length; i++)
            {
                byte b = Data[i];
                for (int p = 0; p < 4; p++)
                    if (((b >> (p * 2)) & 3) != 0) count += 2;
            }
        }
        else
        {
            for (int i = 0; i < Data.Length; i++)
                for (int b = Data[i]; b != 0; b >>= 1)
                    count += b & 1;
        }
        return count;
    }

    /// <summary>
    /// Decodes the sprite into <see cref="SpriteSnapshot.Width"/> x <see cref="SpriteSnapshot.Height"/> color
    /// indices (0..15), row by row; transparent pixels are <see cref="SpriteSnapshot.Transparent"/>. In
    /// multicolor mode each bit pair fills two pixels: "00" transparent, "01" $D025, "10" the sprite color,
    /// "11" $D026 (3.8.1 rule 6). X/Y expansion is not applied, it only scales what is decoded here.
    /// </summary>
    public void Render(Span<byte> pixels)
    {
        if (pixels.Length < SpriteSnapshot.Width * SpriteSnapshot.Height)
            throw new ArgumentException($"Need {SpriteSnapshot.Width * SpriteSnapshot.Height} pixels.", nameof(pixels));
        for (int row = 0; row < SpriteSnapshot.Height; row++)
        {
            int bits = (Data[row * 3] << 16) | (Data[row * 3 + 1] << 8) | Data[row * 3 + 2];
            int at = row * SpriteSnapshot.Width;
            if (Multicolor)
            {
                for (int pair = 0; pair < SpriteSnapshot.Width / 2; pair++)
                {
                    byte c = ((bits >> (22 - pair * 2)) & 3) switch
                    {
                        1 => (byte)MulticolorColor0,
                        2 => (byte)Color,
                        3 => (byte)MulticolorColor1,
                        _ => SpriteSnapshot.Transparent,
                    };
                    pixels[at + pair * 2] = c;
                    pixels[at + pair * 2 + 1] = c;
                }
            }
            else
            {
                for (int x = 0; x < SpriteSnapshot.Width; x++)
                    pixels[at + x] = ((bits >> (23 - x)) & 1) != 0 ? (byte)Color : SpriteSnapshot.Transparent;
            }
        }
    }
}
