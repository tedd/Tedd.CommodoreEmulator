using System;
using Tedd.Commodore.Emulator.Bus;

namespace Tedd.Commodore.Emulator.Video;

/// <summary>
/// The MOS 8563 video display controller of the Commodore 128: the 80 column RGBI display with its own 16K of
/// video RAM, 37 registers reached through the address/status port ($D600) and the data port ($D601), text mode
/// with per character attributes (colour, reverse, blink, underline, alternate character set), 640 x 200 bitmap
/// mode, a hardware cursor and the block copy/fill engine. (C128 Programmer's Reference Guide, chapter 10.)
/// <para>
/// Timing: the VDC runs from its own 16 MHz dot clock, independent of the VIC. <see cref="Clock"/> is called
/// once per system cycle and advances a raster counter by the equivalent number of dots, so the vertical
/// blanking bit of the status register and the blink phases follow the real ~50 Hz frame; the picture is
/// rendered into <see cref="Frame"/> once per VDC frame (there is no beam-position dependent rendering: register
/// changes take effect at the next frame). The "ready" bit of the status register goes low for the same number
/// of cycles VICE uses after data accesses and block operations, which is what the KERNAL polls.
/// </para>
/// <para>
/// Frame buffer: <see cref="FrameWidth"/> x <see cref="FrameHeight"/> (768 x 272) ARGB pixels: twice the width
/// of the VIC's visible area and the same height, so a front-end can show both pictures in one window. The
/// 640 x 200 display area starts at (<see cref="DisplayX"/>, <see cref="DisplayY"/>); the rest shows the
/// background colour (the VDC has no separate border colour). Smooth scrolling (R24 bits 0-4, R25 bits 0-3)
/// and interlace (R8) are not modelled.
/// </para>
/// </summary>
public sealed class Vdc8563 : IClockable
{
    public const int FrameWidth = 768;
    public const int FrameHeight = 272;
    public const int DisplayX = 64;
    public const int DisplayY = 36;
    public const int DisplayWidth = 640;
    public const int DisplayHeight = 200;
    public const int RegisterCount = 37;

    /// <summary>VDC dot clock in Hz.</summary>
    public const double DotClock = 16_000_000.0;

    /// <summary>The 16 RGBI colours (bit 0 = intensity, 1 = blue, 2 = green, 3 = red) as 0xFFRRGGBB.</summary>
    public static readonly uint[] Palette =
    {
        0xFF000000, 0xFF555555, 0xFF0000AA, 0xFF5555FF, 0xFF00AA00, 0xFF55FF55, 0xFF00AAAA, 0xFF55FFFF,
        0xFFAA0000, 0xFFFF5555, 0xFFAA00AA, 0xFFFF55FF, 0xFFAA5500, 0xFFFFFF55, 0xFFAAAAAA, 0xFFFFFFFF,
    };

    /// <summary>Bits that read as 1 in each register (unused bits).</summary>
    private static readonly byte[] ReadMask =
    {
        0x00, 0x00, 0x00, 0x00, 0x80, 0xE0, 0x00, 0x00, 0xFC, 0xE0, 0x80, 0xE0, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xE0, 0x00, 0x00, 0x00, 0x00, 0x0F, 0xE0, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0xF0,
    };

    /// <summary>Dots per system cycle in 16.16 fixed point (16 MHz / 985248 Hz).</summary>
    private const long DotsPerCycleFp = (long)(DotClock / 985248.0 * 65536);

    private readonly byte[] _regs = new byte[RegisterCount];
    private int _selected;
    private long _cycles;
    private long _readyAt;
    private long _dotAccumulator;
    private int _rasterLine;
    private int _frameCounter;
    private bool _pendingRender;

    /// <summary>The video RAM (16K by default; 64K for a C128DCR-style expansion).</summary>
    public byte[] Ram { get; }

    /// <summary>The rendered picture, <see cref="FrameWidth"/> x <see cref="FrameHeight"/> ARGB.</summary>
    public uint[] Frame { get; } = new uint[FrameWidth * FrameHeight];

    /// <summary>Raised after every rendered VDC frame.</summary>
    public event Action? FrameCompleted;

    /// <summary>Chip revision reported in status bits 0-2 (0 = 8563 R7A, 1 = 8563 R8/R9, 2 = 8568).</summary>
    public int Revision { get; set; } = 1;

    public Vdc8563(int ramSize = 16384)
    {
        if (ramSize != 16384 && ramSize != 65536) throw new ArgumentOutOfRangeException(nameof(ramSize), "VDC RAM is 16K or 64K");
        Ram = new byte[ramSize];
        Reset();
    }

    /// <summary>Number of completed VDC frames.</summary>
    public long FrameCount { get; private set; }

    /// <summary>Raster line of the VDC's own frame (0 = first displayed line).</summary>
    public int RasterLine => _rasterLine;

    /// <summary>True while the raster is outside the displayed rows (status bit 5).</summary>
    public bool VerticalBlank => _rasterLine >= DisplayedLines;

    /// <summary>True when the chip accepts a register access (status bit 7).</summary>
    public bool Ready => _cycles >= _readyAt;

    /// <summary>The register currently selected through $D600.</summary>
    public int SelectedRegister => _selected;

    /// <summary>Register <paramref name="index"/> as stored (0..36).</summary>
    public byte Register(int index) => _regs[index];

    private int CharHeight => (_regs[9] & 0x1F) + 1;
    private int Rows => _regs[6];
    private int Columns => _regs[1];
    private int DisplayedLines => Math.Min(Rows * CharHeight, 1 << 16);
    private int TotalLines => Math.Max(8, (_regs[4] + 1) * CharHeight + (_regs[5] & 0x1F));
    private int LineDots => Math.Max(64, (_regs[0] + 1) * 8);
    private int CharTotalWidth => ((_regs[22] >> 4) & 0x0F) + 1;
    private int CharDisplayedWidth => Math.Min(CharTotalWidth, (_regs[22] & 0x0F) + 1);
    private int DisplayBase => (_regs[12] << 8) | _regs[13];
    private int AttributeBase => (_regs[20] << 8) | _regs[21];
    private int CharacterBase => (_regs[28] & 0xE0) << 8;
    private int CursorAddress => (_regs[14] << 8) | _regs[15];
    private int UpdateAddress => (_regs[18] << 8) | _regs[19];
    private int BytesPerCharacter => CharHeight > 16 ? 32 : 16;
    private int RowStride => Columns + _regs[27];

    /// <summary>Resets the registers to the values the KERNAL programs for an 80 x 25 PAL text screen, clears the picture.</summary>
    public void Reset()
    {
        Array.Clear(_regs, 0, _regs.Length);
        _regs[0] = 126; _regs[1] = 80; _regs[2] = 102; _regs[3] = 0x49; _regs[4] = 39; _regs[5] = 0;
        _regs[6] = 25; _regs[7] = 32; _regs[8] = 0; _regs[9] = 7; _regs[10] = 0x20; _regs[11] = 7;
        _regs[20] = 0x08; _regs[22] = 0x78; _regs[23] = 8; _regs[24] = 0x20; _regs[25] = 0x40; _regs[26] = 0xF0;
        _regs[28] = 0x20; _regs[29] = 7; _regs[34] = 0x7D; _regs[35] = 0x64; _regs[36] = 5;
        _selected = 0;
        _readyAt = 0;
        _dotAccumulator = 0;
        _rasterLine = 0;
        _frameCounter = 0;
        _pendingRender = false;
        Array.Fill(Frame, Palette[0]);
    }

    #region Registers

    /// <summary>Reads $D600 (status) or $D601 (the selected register), with side effects (data register auto-increment).</summary>
    public byte Read(int port)
    {
        if ((port & 1) == 0)
            return Status();
        if (_selected == 31)
        {
            // Reading the data register returns the byte at the update address and increments it (the chip
            // pre-reads the next byte, which is what costs the time).
            byte v = Ram[UpdateAddress & (Ram.Length - 1)];
            SetUpdateAddress(UpdateAddress + 1);
            _readyAt = _cycles + (VerticalBlank ? 4 : 43);
            return v;
        }
        return Peek(port);
    }

    /// <summary>Reads without side effects.</summary>
    public byte Peek(int port)
    {
        if ((port & 1) == 0)
            return Status();
        if (_selected >= RegisterCount)
            return 0xFF;
        if (_selected == 31)
            return Ram[UpdateAddress & (Ram.Length - 1)];
        return (byte)(_regs[_selected] | ReadMask[_selected]);
    }

    private byte Status() => (byte)((Ready ? 0x80 : 0) | (VerticalBlank ? 0x20 : 0) | (Revision & 7));

    /// <summary>Writes $D600 (selects a register) or $D601 (the selected register).</summary>
    public void Write(int port, byte value)
    {
        if ((port & 1) == 0)
        {
            _selected = value & 0x3F;
            return;
        }
        if (_selected >= RegisterCount)
            return;
        switch (_selected)
        {
            case 16:
            case 17:
                return;                                            // light pen registers are read-only
            case 30:
                _regs[30] = value;
                BlockOperation();
                return;
            case 31:
                Ram[UpdateAddress & (Ram.Length - 1)] = value;
                _regs[31] = value;
                SetUpdateAddress(UpdateAddress + 1);
                _readyAt = _cycles + (VerticalBlank ? 4 : 43);
                return;
            default:
                _regs[_selected] = value;
                return;
        }
    }

    private void SetUpdateAddress(int address)
    {
        _regs[18] = (byte)(address >> 8);
        _regs[19] = (byte)address;
    }

    /// <summary>R30 written: copy (R24 bit 7 set) from the block start address or fill with R31, R30 bytes (0 = 256).</summary>
    private void BlockOperation()
    {
        int count = _regs[30] == 0 ? 256 : _regs[30];
        int mask = Ram.Length - 1;
        int dest = UpdateAddress;
        if ((_regs[24] & 0x80) != 0)
        {
            int src = (_regs[32] << 8) | _regs[33];
            byte last = _regs[31];
            for (int i = 0; i < count; i++)
            {
                last = Ram[(src + i) & mask];
                Ram[(dest + i) & mask] = last;
            }
            _regs[31] = last;
            src += count;
            _regs[32] = (byte)(src >> 8);
            _regs[33] = (byte)src;
            _readyAt = _cycles + count * 120 / 100;
        }
        else
        {
            byte fill = _regs[31];
            for (int i = 0; i < count; i++)
                Ram[(dest + i) & mask] = fill;
            _readyAt = _cycles + count * 66 / 100;
        }
        SetUpdateAddress(dest + count);
    }

    #endregion

    #region Clock

    /// <summary>One system cycle: advances the raster by ~16 dots; renders at the end of every VDC frame.</summary>
    public void Clock()
    {
        _cycles++;
        _dotAccumulator += DotsPerCycleFp;
        long lineDots = (long)LineDots << 16;
        while (_dotAccumulator >= lineDots)
        {
            _dotAccumulator -= lineDots;
            if (++_rasterLine >= TotalLines)
            {
                _rasterLine = 0;
                _frameCounter++;
                FrameCount++;
                Render();
                FrameCompleted?.Invoke();
            }
        }
    }

    #endregion

    #region Rendering

    /// <summary>Renders the whole picture from the current registers and RAM into <see cref="Frame"/>.</summary>
    public void Render()
    {
        var frame = Frame;
        uint background = Palette[_regs[26] & 0x0F];
        Array.Fill(frame, background);

        bool bitmap = (_regs[25] & 0x80) != 0;
        bool attributes = (_regs[25] & 0x40) != 0;
        bool semigraphic = (_regs[25] & 0x20) != 0;
        bool doublePixel = (_regs[25] & 0x10) != 0;
        bool reverseScreen = (_regs[24] & 0x40) != 0;
        bool blinkPhase = (_frameCounter & ((_regs[24] & 0x20) != 0 ? 8 : 16)) != 0;
        int charHeight = CharHeight;
        int rows = Rows;
        int columns = Columns;
        int stride = RowStride;
        int totalWidth = CharTotalWidth;
        int displayedWidth = CharDisplayedWidth;
        int pixelWidth = doublePixel ? 2 : 1;
        int cellWidth = totalWidth * pixelWidth;
        int mask = Ram.Length - 1;
        int displayBase = DisplayBase;
        int attributeBase = AttributeBase;
        int charBase = CharacterBase;
        int bytesPerChar = BytesPerCharacter;
        int cursorAddress = CursorAddress;
        int cursorMode = (_regs[10] >> 5) & 3;
        bool cursorVisible = cursorMode switch
        {
            0 => true,
            1 => false,
            2 => (_frameCounter & 16) != 0,
            _ => (_frameCounter & 32) != 0,
        };
        int cursorStart = _regs[10] & 0x1F, cursorEnd = _regs[11] & 0x1F;
        int underlineLine = _regs[29] & 0x1F;
        int defaultFg = _regs[26] >> 4;
        int defaultBg = _regs[26] & 0x0F;

        int maxColumns = Math.Min(columns, DisplayWidth / Math.Max(1, cellWidth));
        for (int row = 0; row < rows; row++)
        {
            int rowTop = row * charHeight;
            if (rowTop >= DisplayHeight) break;
            int rowAddress = displayBase + row * stride;
            int attrAddress = attributeBase + row * stride;
            for (int col = 0; col < maxColumns; col++)
            {
                int cell = rowAddress + col;
                byte attr = attributes ? Ram[(attrAddress + col) & mask] : (byte)0;
                int fg, bg;
                bool alt = false, reverse = reverseScreen, blink = false, underline = false;
                if (bitmap)
                {
                    fg = attributes ? attr >> 4 : defaultFg;
                    bg = attributes ? attr & 0x0F : defaultBg;
                }
                else
                {
                    fg = attributes ? attr & 0x0F : defaultFg;
                    bg = defaultBg;
                    if (attributes)
                    {
                        alt = (attr & 0x80) != 0;
                        reverse ^= (attr & 0x40) != 0;
                        underline = (attr & 0x20) != 0;
                        blink = (attr & 0x10) != 0 && blinkPhase;
                    }
                }
                bool cursorHere = !bitmap && cursorVisible && cell == cursorAddress;
                uint fgColor = Palette[fg], bgColor = Palette[bg];
                int glyphAddress = 0;
                if (!bitmap)
                {
                    int code = Ram[cell & mask] + (alt ? 256 : 0);
                    glyphAddress = charBase + code * bytesPerChar;
                }
                int x0 = DisplayX + col * cellWidth;
                for (int line = 0; line < charHeight; line++)
                {
                    int y = rowTop + line;
                    if (y >= DisplayHeight) break;
                    int bits;
                    if (bitmap)
                        bits = Ram[(displayBase + (rowTop + line) * stride + col) & mask];
                    else if (line < bytesPerChar)
                        bits = Ram[(glyphAddress + line) & mask];
                    else
                        bits = 0;
                    if (blink) bits = 0;
                    if (underline && line == underlineLine) bits = 0xFF;
                    bool invert = reverse ^ (cursorHere && line >= cursorStart && line <= cursorEnd);
                    int fb = (DisplayY + y) * FrameWidth + x0;
                    int lastBit = (bits >> (8 - Math.Min(8, displayedWidth))) & 1;
                    for (int px = 0; px < totalWidth; px++)
                    {
                        int bit;
                        if (px < displayedWidth && px < 8) bit = (bits >> (7 - px)) & 1;
                        else bit = semigraphic ? lastBit : 0;
                        uint color = (bit != 0) ^ invert ? fgColor : bgColor;
                        frame[fb] = color;
                        if (doublePixel) frame[fb + 1] = color;
                        fb += pixelWidth;
                    }
                }
            }
        }
    }

    /// <summary>
    /// The text on screen (rows x columns of the current display base), screen codes converted with
    /// <paramref name="convert"/>, one line per row with trailing spaces trimmed.
    /// </summary>
    public string GetScreenText(Func<byte, char> convert)
    {
        int rows = Rows, columns = Columns, stride = RowStride, mask = Ram.Length - 1;
        var sb = new System.Text.StringBuilder(rows * (columns + 1));
        var line = new char[columns];
        for (int row = 0; row < rows; row++)
        {
            int len = 0;
            for (int col = 0; col < columns; col++)
            {
                char c = convert(Ram[(DisplayBase + row * stride + col) & mask]);
                line[col] = c;
                if (c != ' ') len = col + 1;
            }
            sb.Append(line, 0, len).Append('\n');
        }
        return sb.ToString();
    }

    #endregion
}
