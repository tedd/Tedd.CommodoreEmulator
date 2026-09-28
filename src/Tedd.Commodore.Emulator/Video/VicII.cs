using System;
using System.Runtime.CompilerServices;
using Tedd.Commodore.Emulator.Bus;

namespace Tedd.Commodore.Emulator.Video;

/// <summary>
/// Cycle-exact MOS 6569 (PAL-B) VIC-II video controller.
///
/// Everything follows Christian Bauer's "The MOS 6567/6569 video controller (VIC-II) and its application in the
/// Commodore 64" (vic-ii.txt, referenced below by section number). Each call to <see cref="Clock"/> executes
/// exactly one of the 63 cycles of a raster line (3.6.3): the first clock phase (φ1) memory access of the VIC
/// (sprite pointer/data, refresh, character generator or idle access), the BA/AEC lines, the internal counters
/// (VC/VCBASE/RC/VMLI, 3.7.2; MC/MCBASE, 3.8.1), the border flip-flops (3.9), the 8 pixels of that cycle and
/// finally the second phase (φ2) access (video matrix/color RAM c-access or sprite data s-access).
///
/// <para><b>X coordinate / cycle mapping (6569).</b> A raster line has 63 cycles of 8 pixels = 504 pixels, X
/// coordinates $000-$1F7. Per the table in 3.6.1 ("First X coo. of a line" = 404 = $194) the first pixel of
/// cycle 1 is X = $194, i.e. cycle 1 covers $194-$19B (φ1 = $194-$197, φ2 = $198-$19B), cycle n starts at
/// $194 + 8(n-1). The X counter wraps from $1F7 to $000 in the middle of cycle 13 (cycle 13 = $1F4-$1F7,
/// $000-$003), so cycle 14 starts at $004, cycle 16 at $014, and the left edge of the 40 column display window
/// (X = $18, 3.9) is pixel 4 of cycle 16: exactly where the graphics data read by the first g-access (cycle 16
/// φ1, 3.6.3) start to be shifted out with XSCROLL = 0 (the g-access of cycle n is displayed from pixel
/// 4 + XSCROLL of cycle n, i.e. column k occupies X = $18 + 8k + XSCROLL .. +7, cf. the "Graph." row of the
/// 6569 diagram in 3.6.3). The same X coordinate is used for the sprite X compare (3.8.1 rule 6) and the border
/// compares (3.9), so a sprite with X = $18 starts at the display window's left edge.
/// <see cref="XToFrameColumn"/> / <see cref="FrameColumnToX"/> convert between X coordinates and frame
/// buffer columns.</para>
///
/// <para><b>Frame buffer layout.</b> <see cref="Frame"/> holds <see cref="FrameWidth"/> x
/// <see cref="FrameHeight"/> = 504 x 312 ARGB pixels (0xFFRRGGBB, <see cref="Palette"/>): pixel
/// x = (cycle - 1) * 8 + pixel index, y = raster line. The whole line and all 312 lines are rendered; horizontal
/// and vertical blanking are not modelled and simply show the border color (the border flip-flop is set there).
/// Frame column 0 is X = $194; the display window (CSEL = 1) is at columns 124..443 (X $18..$157) and, with
/// RSEL = 1, lines 51..250.</para>
///
/// <para><b>Visible area.</b> <see cref="VisibleArea"/> = (92, 16, 384, 272) is the rectangle VICE shows for
/// PAL: 384 x 272 pixels with the 320 x 200 display window at offset (32, 35): columns 92..475 = X $1F0..$1F7,
/// $000..$177, lines 16..287 (VICE's first/last displayed PAL line $10/$11F).</para>
///
/// <para>Timing summary (all from 3.6.3 / 3.7.2 / 3.8.1 / 3.9 / 3.10 of vic-ii.txt):
/// raster counter and raster IRQ compare in cycle 1 (cycle 2 in line 0); sprite 3-7 pointer fetches in
/// cycles 1,3,5,7,9 with the data fetches in the following φ2/φ1/φ2; DRAM refresh 11-15; VC/VMLI load in
/// cycle 14; MCBASE update in 15/16 (DMA off when MCBASE = 63); c-accesses in the φ2 of 15-54 on bad lines;
/// g-accesses in the φ1 of 16-55; sprite Y compare / DMA on in 55 and 56; sprite display check and RC = 7
/// idle check in 58; sprite 0-2 pointer fetches in 58, 60, 62; vertical border flip-flop check in cycle 63.
/// BA is asserted from cycle 12 to 54 on bad lines and from three cycles before a sprite's pointer fetch until
/// its last data fetch; AEC follows BA three cycles later.</para>
/// </summary>
public sealed partial class VicII : IClockable
{
    /// <summary>Frame buffer width in pixels: 63 cycles x 8 pixels (3.6.3).</summary>
    public const int FrameWidth = 504;
    /// <summary>Frame buffer height in pixels: the 312 raster lines of the 6569 (3.6.1).</summary>
    public const int FrameHeight = 312;
    /// <summary>Cycles per raster line of the 6569 (3.6.1).</summary>
    public const int CyclesPerLine = 63;
    /// <summary>Raster lines per frame of the 6569 (3.6.1).</summary>
    public const int LinesPerFrame = 312;

    /// <summary>X coordinate of the first pixel of cycle 1 (3.6.1: "First X coo. of a line" = 404 for the 6569).</summary>
    public const int FirstXOfLine = 0x194;
    /// <summary>Highest X coordinate of a line; the counter wraps from here to $000 (3.6.3, 6569 diagram).</summary>
    public const int LastX = 0x1F7;

    /// <summary>Left edge (X coordinate) of the 40 column display window, CSEL = 1 (3.9).</summary>
    public const int DisplayWindowLeft40 = 0x18;
    /// <summary>Left edge (X coordinate) of the 38 column display window, CSEL = 0 (3.9).</summary>
    public const int DisplayWindowLeft38 = 0x1F;
    /// <summary>Right comparison value (first border X) for CSEL = 1 (3.9).</summary>
    public const int DisplayWindowRight40 = 0x158;
    /// <summary>Right comparison value (first border X) for CSEL = 0 (3.9).</summary>
    public const int DisplayWindowRight38 = 0x14F;
    /// <summary>First display line for RSEL = 1 (25 rows, 3.9).</summary>
    public const int DisplayWindowTop25 = 0x33;
    /// <summary>First display line for RSEL = 0 (24 rows, 3.9).</summary>
    public const int DisplayWindowTop24 = 0x37;
    /// <summary>Bottom comparison value (first border line) for RSEL = 1 (3.9).</summary>
    public const int DisplayWindowBottom25 = 0xFB;
    /// <summary>Bottom comparison value (first border line) for RSEL = 0 (3.9).</summary>
    public const int DisplayWindowBottom24 = 0xF7;

    // IRQ latch bits ($D019 / $D01A, 3.12)
    public const byte IrqRaster = 0x01;
    public const byte IrqSpriteData = 0x02;
    public const byte IrqSpriteSprite = 0x04;
    public const byte IrqLightPen = 0x08;

    /// <summary>The 16 C64 colors as 0xFFRRGGBB (Pepto's PAL palette).</summary>
    public static readonly uint[] Palette =
    {
        0xFF000000, 0xFFFFFFFF, 0xFF68372B, 0xFF70A4B2, 0xFF6F3D86, 0xFF588D43, 0xFF352879, 0xFFB8C76F,
        0xFF6F4F25, 0xFF433900, 0xFF9A6759, 0xFF444444, 0xFF6C6C6C, 0xFF9AD284, 0xFF6C5EB5, 0xFF959595,
    };

    /// <summary>
    /// The standard visible PAL picture inside <see cref="Frame"/> (VICE's 384 x 272 window): the 320 x 200
    /// display window (CSEL = RSEL = 1, frame columns 124..443, lines 51..250) sits at offset (32, 35) inside it.
    /// </summary>
    public static readonly (int X, int Y, int Width, int Height) VisibleArea =
        (XToFrameColumn(DisplayWindowLeft40) - 32, DisplayWindowTop25 - 35, 384, 272);

    /// <summary>Converts an X coordinate (0..$1F7, 3.6.3) to a frame buffer column (0..503).</summary>
    public static int XToFrameColumn(int x) => (x - FirstXOfLine + FrameWidth) % FrameWidth;

    /// <summary>Converts a frame buffer column (0..503) to the VIC X coordinate (0..$1F7) of that pixel.</summary>
    public static int FrameColumnToX(int column) => (column + FirstXOfLine) % FrameWidth;

    /// <summary>Cycle (1..63) in which the pointer (p-) access of each sprite takes place (3.6.3).</summary>
    private static readonly int[] SpritePointerCycle = { 58, 60, 62, 1, 3, 5, 7, 9 };

    /// <summary>First cycle in which BA is asserted for each sprite: three cycles before the first s-access (3.6.3, 3.8.1).</summary>
    private static readonly int[] SpriteBaStart = { 55, 57, 59, 61, 63, 2, 4, 6 };

    private readonly IVicMemory _memory;

    // Registers as written ($00-$2E); read-back rules are applied in Peek.
    private readonly byte[] _regs = new byte[0x2F];

    // Position
    private int _cycle;          // 1..63, the cycle currently/last executed (0 after Reset)
    private int _line;           // 0..311, the raster line being rendered
    private int _raster;         // RASTER register value (3.14.?): updated in cycle 1, in line 0 in cycle 2
    private int _rasterCompare;  // 9 bit raster compare value from $D012 + $D011 bit 7
    private long _frameCount;

    // IRQ
    private byte _irqFlags;      // $D019 bits 0-3

    // Bad line / display logic (3.5, 3.7.2)
    private bool _denLatch;      // DEN was set in some cycle of raster line $30
    private bool _badLine;
    private bool _displayState;
    private int _vc, _vcBase, _rc, _vmli;
    private readonly byte[] _vbuf = new byte[64];   // video matrix line buffer (40 entries used; VMLI is 6 bits)
    private readonly byte[] _cbuf = new byte[64];   // color line buffer
    private byte _refresh;       // REF counter for the r-accesses ($3Fxx)
    private byte _lastColor;     // last color RAM nibble seen (used when the bus is not yet available)

    // BA / AEC
    private bool _ba;
    private int _baCount;        // consecutive cycles BA has been asserted, including the current one

    // Border unit (3.9)
    private bool _mainBorder = true;
    private bool _verticalBorder = true;

    // Graphics data sequencer (3.7.3)
    private byte _fetchG, _fetchV, _fetchC; private bool _fetchValid;   // g-access of the current cycle
    private byte _prevG, _prevV, _prevC; private bool _prevValid;       // g-access of the previous cycle
    private int _gShift;         // 8 bit shift register
    private bool _gOdd;          // pixel parity since the last load (multicolor pairs)
    private int _gPair;          // current multicolor bit pair
    private byte _vLatch, _cLatch;

    // Sprites (3.8)
    private readonly int[] _sprX = new int[8];
    private readonly bool[] _sprDma = new bool[8];
    private readonly bool[] _sprDisplay = new bool[8];
    private readonly bool[] _sprExpFF = new bool[8];
    private readonly int[] _sprMc = new int[8];
    private readonly int[] _sprMcBase = new int[8];
    private readonly byte[] _sprPointer = new byte[8];
    private readonly uint[] _sprShift = new uint[8];     // 24 bit shift register
    private readonly bool[] _sprActive = new bool[8];    // X matched, shifting out
    private readonly int[] _sprShifted = new int[8];     // bits shifted out so far
    private readonly int[] _sprSub = new int[8];         // pixel sub counter (X expansion / multicolor)
    private int _sprDisplayMask;

    // Light pen
    private bool _lightPen;
    private bool _lightPenLatched;

    // VIC-IIe (8564/8566, Commodore 128) additions: $D02F keyboard lines K0-K2, $D030 clock control
    private readonly bool _c128;
    private int _keyboardLines = 7;
    private bool _fastMode;
    private bool _testBit;

    /// <summary>The rendered frame, <see cref="FrameWidth"/> x <see cref="FrameHeight"/> ARGB pixels.</summary>
    public uint[] Frame { get; } = new uint[FrameWidth * FrameHeight];

    /// <summary>Raised at the end of the last cycle of line 311.</summary>
    public event Action? FrameCompleted;

    public VicII(IVicMemory memory) : this(memory, false) { }

    /// <param name="memory">The memory the VIC reads from.</param>
    /// <param name="c128">
    /// true = VIC-IIe (8564/8566) as used in the Commodore 128: registers $D02F (keyboard lines K0-K2) and $D030
    /// (2 MHz mode) exist; false = the 6569 of the C64 where those addresses read $FF.
    /// </param>
    public VicII(IVicMemory memory, bool c128)
    {
        _memory = memory ?? throw new ArgumentNullException(nameof(memory));
        _c128 = c128;
        Reset();
    }

    /// <summary>True for the VIC-IIe (C128) variant with the $D02F/$D030 registers.</summary>
    public bool IsVicIIe => _c128;

    /// <summary>
    /// Levels of the three extra keyboard row lines K0-K2 of the C128 (bits 0-2 of $D02F, 1 = high). The C128
    /// feeds them to <see cref="C64.Keyboard.ExtendedRowLevels"/>. Always 7 on a C64.
    /// </summary>
    public int KeyboardLines => _keyboardLines;

    /// <summary>
    /// 2 MHz mode ($D030 bit 0, VIC-IIe only). The 8502 then runs two cycles per VIC cycle and the VIC gets no
    /// bus access: it keeps its timing (raster counter, interrupts) but performs no memory fetches and never
    /// asserts BA; the graphics data reads as $FF (an unloaded bus), which is why FAST also blanks the screen.
    /// </summary>
    public bool FastMode => _fastMode;

    /// <summary>BA line, true = asserted (the CPU must stop at its next read cycle).</summary>
    public bool Ba => _ba;

    /// <summary>AEC line, true = the VIC owns the bus in the second clock phase (three cycles after BA, 3.6.3).</summary>
    public bool Aec => _baCount >= 4;

    /// <summary>IRQ output, true = asserted: bit 7 of $D019, i.e. any enabled interrupt latch bit is set (3.12).</summary>
    public bool Irq => (_irqFlags & _regs[0x1A] & 0x0F) != 0;

    /// <summary>Raster line currently being rendered (0..311).</summary>
    public int RasterLine => _line;

    /// <summary>Cycle within the raster line (1..63) executed by the last <see cref="Clock"/> call (0 after reset).</summary>
    public int RasterCycle => _cycle;

    /// <summary>Value of the RASTER register as the CPU sees it (changes in cycle 1, in line 0 in cycle 2).</summary>
    public int RasterRegister => _raster;

    /// <summary>Number of completed frames.</summary>
    public long FrameCount => _frameCount;

    /// <summary>True while the bad line condition (3.5) holds in the current cycle.</summary>
    public bool BadLine => _badLine;

    /// <summary>True in display state, false in idle state (3.7.1).</summary>
    public bool DisplayState => _displayState;

    /// <summary>Main border flip-flop (3.9).</summary>
    public bool MainBorderFlipFlop => _mainBorder;

    /// <summary>Vertical border flip-flop (3.9).</summary>
    public bool VerticalBorderFlipFlop => _verticalBorder;

    /// <summary>The memory this VIC reads from.</summary>
    public IVicMemory Memory => _memory;

    // ------------------------------------------------------------------------------------------------------
    // Sprite state for debuggers (3.8). None of these have side effects; <paramref name="n"/> is 0..7.
    // ------------------------------------------------------------------------------------------------------

    /// <summary>X coordinate of sprite <paramref name="n"/> (0..511): $D000 + 2n with the MSB from $D010.</summary>
    public int SpriteX(int n) => _sprX[n & 7];

    /// <summary>True while the sprite DMA of sprite <paramref name="n"/> is on (3.8.1 rules 3-5).</summary>
    public bool SpriteDma(int n) => _sprDma[n & 7];

    /// <summary>True while sprite <paramref name="n"/> is in display state, i.e. its data is shifted out (3.8.1 rule 4).</summary>
    public bool SpriteDisplayed(int n) => _sprDisplay[n & 7];

    /// <summary>Y expansion flip-flop of sprite <paramref name="n"/>; cleared it makes MCBASE stall for one line (3.8.1).</summary>
    public bool SpriteExpansionFlipFlop(int n) => _sprExpFF[n & 7];

    /// <summary>MC of sprite <paramref name="n"/>: the offset (0..63) of its next s-access within the 64 byte block.</summary>
    public int SpriteMc(int n) => _sprMc[n & 7];

    /// <summary>MCBASE of sprite <paramref name="n"/>: the MC value reloaded at the start of each raster line.</summary>
    public int SpriteMcBase(int n) => _sprMcBase[n & 7];

    /// <summary>Sprite pointer of sprite <paramref name="n"/> as last read by a p-access (0 before the first one).</summary>
    public byte SpritePointer(int n) => _sprPointer[n & 7];

    /// <summary>The 24 bit shift register of sprite <paramref name="n"/>, the three bytes of the current line.</summary>
    public uint SpriteShiftRegister(int n) => _sprShift[n & 7];

    /// <summary>Resets all registers and internal state (registers read 0, sprites off, idle state).</summary>
    public void Reset()
    {
        Array.Clear(_regs, 0, _regs.Length);
        _cycle = 0;
        _line = 0;
        _raster = 0;
        _rasterCompare = 0;
        _irqFlags = 0;
        _denLatch = false;
        _badLine = false;
        _displayState = false;
        _vc = _vcBase = _rc = _vmli = 0;
        Array.Clear(_vbuf, 0, _vbuf.Length);
        Array.Clear(_cbuf, 0, _cbuf.Length);
        _refresh = 0xFF;
        _lastColor = 0;
        _ba = false;
        _baCount = 0;
        _mainBorder = true;
        _verticalBorder = true;
        _fetchValid = _prevValid = false;
        _fetchG = _fetchV = _fetchC = _prevG = _prevV = _prevC = 0;
        _gShift = 0;
        _gOdd = false;
        _gPair = 0;
        _vLatch = _cLatch = 0;
        for (int n = 0; n < 8; n++)
        {
            _sprX[n] = 0;
            _sprDma[n] = false;
            _sprDisplay[n] = false;
            _sprExpFF[n] = true;   // 3.8.1 rule 1: set as long as MxYE is cleared
            _sprMc[n] = _sprMcBase[n] = 0;
            _sprPointer[n] = 0;
            _sprShift[n] = 0;
            _sprActive[n] = false;
            _sprShifted[n] = _sprSub[n] = 0;
        }
        _sprDisplayMask = 0;
        _lightPen = false;
        _lightPenLatched = false;
        _keyboardLines = 7;
        _fastMode = false;
        _testBit = false;
    }

    // ------------------------------------------------------------------------------------------------------
    // Registers (3.2)
    // ------------------------------------------------------------------------------------------------------

    /// <summary>Reads register <paramref name="reg"/> (0..63) with side effects ($D01E/$D01F are cleared).</summary>
    public byte Read(int reg)
    {
        reg &= 0x3F;
        if (reg == 0x1E || reg == 0x1F)
        {
            // 3.8.2: the collision registers are cleared automatically on reading.
            byte v = _regs[reg];
            _regs[reg] = 0;
            return v;
        }
        return Peek(reg);
    }

    /// <summary>Reads register <paramref name="reg"/> (0..63) without side effects.</summary>
    public byte Peek(int reg)
    {
        reg &= 0x3F;
        if (reg >= 0x2F)
        {
            // 3.2: $D02F-$D03F are not connected on the 6569 and read $FF. The VIC-IIe has two more registers:
            // $D02F (K0-K2 keyboard lines, bits 3-7 read 1) and $D030 (bit 0 = 2 MHz, bit 1 = test, rest read 1).
            if (!_c128) return 0xFF;
            if (reg == 0x2F) return (byte)(0xF8 | _keyboardLines);
            if (reg == 0x30) return (byte)(0xFC | (_fastMode ? 1 : 0) | (_testBit ? 2 : 0));
            return 0xFF;
        }
        switch (reg)
        {
            case 0x11: return (byte)((_regs[0x11] & 0x7F) | ((_raster & 0x100) >> 1)); // RST8 = raster bit 8
            case 0x12: return (byte)_raster;                                          // RASTER bits 0-7
            case 0x16: return (byte)(_regs[0x16] | 0xC0);                             // bits 6-7 unused, read 1
            case 0x18: return (byte)(_regs[0x18] | 0x01);                             // bit 0 unused, read 1
            case 0x19: return (byte)((_irqFlags & 0x0F) | 0x70 | (Irq ? 0x80 : 0));   // bits 4-6 read 1, bit 7 = IRQ
            case 0x1A: return (byte)(_regs[0x1A] | 0xF0);                             // bits 4-7 unused, read 1
            case >= 0x20 and <= 0x2E: return (byte)(_regs[reg] | 0xF0);              // color registers: upper nibble reads 1
            default: return _regs[reg];
        }
    }

    /// <summary>Writes register <paramref name="reg"/> (0..63).</summary>
    public void Write(int reg, byte value)
    {
        reg &= 0x3F;
        if (reg >= 0x2F)
        {
            // 3.2: writes to $D02F-$D03F are ignored on the 6569; the VIC-IIe latches K0-K2 and the clock bits.
            if (!_c128) return;
            if (reg == 0x2F) _keyboardLines = value & 7;
            else if (reg == 0x30) { _fastMode = (value & 1) != 0; _testBit = (value & 2) != 0; }
            return;
        }
        switch (reg)
        {
            case < 0x10:
                _regs[reg] = value;
                if ((reg & 1) == 0)
                    UpdateSpriteX(reg >> 1);
                break;
            case 0x10:
                _regs[0x10] = value;
                for (int n = 0; n < 8; n++)
                    UpdateSpriteX(n);
                break;
            case 0x11:
                _regs[0x11] = value;
                _rasterCompare = (_rasterCompare & 0xFF) | ((value & 0x80) << 1);
                // 3.5: the DEN bit is latched if it is set in any cycle of raster line $30.
                if (_raster == 0x30 && (value & 0x10) != 0)
                    _denLatch = true;
                break;
            case 0x12:
                _regs[0x12] = value;
                _rasterCompare = (_rasterCompare & 0x100) | value;
                break;
            case 0x13:
            case 0x14:
                break;                                             // light pen latches are read-only
            case 0x17:
                _regs[0x17] = value;
                // 3.8.1 rule 1: the expansion flip-flop is set as long as the MxYE bit is cleared.
                for (int n = 0; n < 8; n++)
                    if ((value & (1 << n)) == 0)
                        _sprExpFF[n] = true;
                break;
            case 0x19:
                _irqFlags &= (byte)~(value & 0x0F);                // 3.12: writing a 1 bit acknowledges that latch
                break;
            case 0x1A:
                _regs[0x1A] = (byte)(value & 0x0F);
                break;
            case 0x1E:
            case 0x1F:
                break;                                             // collision registers are read-only
            case >= 0x20:
                _regs[reg] = (byte)(value & 0x0F);
                break;
            default:
                _regs[reg] = value;
                break;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void UpdateSpriteX(int n) => _sprX[n] = _regs[n << 1] | (((_regs[0x10] >> n) & 1) << 8);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SetIrq(byte bit) => _irqFlags |= bit;

    /// <summary>
    /// Light pen input (LP pin, true = asserted). A false → true transition latches the current X coordinate / 2
    /// into $D013 and the raster line into $D014 and sets the ILP interrupt latch, once per frame (3.11).
    /// </summary>
    public void SetLightPen(bool asserted)
    {
        if (asserted && !_lightPen && !_lightPenLatched)
        {
            _lightPenLatched = true;
            int x = FrameColumnToX(_cycle > 0 ? (_cycle - 1) * 8 : 0);
            _regs[0x13] = (byte)(x >> 1);
            _regs[0x14] = (byte)_raster;
            SetIrq(IrqLightPen);
        }
        _lightPen = asserted;
    }

    // ------------------------------------------------------------------------------------------------------
    // Clock
    // ------------------------------------------------------------------------------------------------------

    /// <summary>Executes one system cycle: φ1 VIC access, BA/AEC, IRQ, 8 pixels, φ2 VIC access.</summary>
    public void Clock()
    {
        // Advance the position counters.
        if (_cycle >= CyclesPerLine)
        {
            _cycle = 1;
            if (++_line >= LinesPerFrame)
                _line = 0;
        }
        else
        {
            _cycle++;
        }
        int cycle = _cycle;
        bool line0 = _line == 0;

        // RASTER counter: incremented in cycle 1 of every line, except that the wrap to line 0 happens in
        // cycle 2 (3.12: "The test for reaching the interrupt raster line is done in cycle 0 of every line
        // (for line 0, in cycle 1)" - the document counts from 0 there; a raster IRQ therefore appears at
        // cycle 1 of the compare line and at cycle 2 in line 0).
        if (cycle == (line0 ? 2 : 1))
        {
            _raster = _line;
            if (_raster == _rasterCompare)
                SetIrq(IrqRaster);
        }

        if (cycle == 1 && line0)
        {
            // 3.7.2 rule 1: VCBASE is reset to zero once per frame outside the bad line range (line 0).
            _vcBase = 0;
            _refresh = 0xFF;
            _lightPenLatched = false;
        }

        // 3.5: bad line condition; DEN must have been set in some cycle of raster line $30.
        bool den = (_regs[0x11] & 0x10) != 0;
        if (_raster == 0x30)
        {
            if (cycle == 1) _denLatch = den;
            else if (den) _denLatch = true;
        }
        _badLine = _denLatch && _raster >= 0x30 && _raster <= 0xF7 && (_raster & 7) == (_regs[0x11] & 7);
        // 3.7.1: the transition from idle to display state occurs as soon as there is a bad line condition.
        if (_badLine)
            _displayState = true;

        // Move the g-access of the previous cycle into the pipeline slot used for XSCROLL >= 4 loads.
        _prevG = _fetchG; _prevV = _fetchV; _prevC = _fetchC; _prevValid = _fetchValid;
        _fetchValid = false;

        // First clock phase: counters and VIC accesses (3.6.3 table, 3.7.2, 3.8.1).
        bool ecm = (_regs[0x11] & 0x40) != 0;
        switch (cycle)
        {
            case 1: SpritePointerAccess(3); break;
            case 2: SpriteDataAccess(3); break;
            case 3: SpritePointerAccess(4); break;
            case 4: SpriteDataAccess(4); break;
            case 5: SpritePointerAccess(5); break;
            case 6: SpriteDataAccess(5); break;
            case 7: SpritePointerAccess(6); break;
            case 8: SpriteDataAccess(6); break;
            case 9: SpritePointerAccess(7); break;
            case 10: SpriteDataAccess(7); break;
            case 11:
            case 12:
            case 13:
                RefreshAccess();
                break;
            case 14:
                // 3.7.2 rule 2: VCBASE -> VC, VMLI cleared, RC cleared on a bad line.
                _vc = _vcBase;
                _vmli = 0;
                if (_badLine) _rc = 0;
                RefreshAccess();
                break;
            case 15:
                // 3.8.1 rule 7: MCBASE += 2 if the expansion flip-flop is set.
                for (int n = 0; n < 8; n++)
                    if (_sprDma[n] && _sprExpFF[n])
                        _sprMcBase[n] = (_sprMcBase[n] + 2) & 0x3F;
                RefreshAccess();
                break;
            case 16:
                // 3.8.1 rule 8: MCBASE += 1 if the flip-flop is set; DMA off when MCBASE reaches 63. The display
                // flag is turned off in cycle 58 when the DMA is found off (the last fetched sprite line is still
                // displayed in this line, which gives the 21 lines of a sprite).
                for (int n = 0; n < 8; n++)
                {
                    if (!_sprDma[n]) continue;
                    if (_sprExpFF[n])
                        _sprMcBase[n] = (_sprMcBase[n] + 1) & 0x3F;
                    if (_sprMcBase[n] == 63)
                        _sprDma[n] = false;
                }
                GraphicsAccess(ecm);
                break;
            case >= 17 and <= 54:
                GraphicsAccess(ecm);
                break;
            case 55:
                GraphicsAccess(ecm);
                // 3.8.1 rules 1/2: expansion flip-flop inverted in cycle 55 if MxYE is set (set if cleared).
                for (int n = 0; n < 8; n++)
                    _sprExpFF[n] = (_regs[0x17] & (1 << n)) == 0 || !_sprExpFF[n];
                SpriteDmaCheck();
                break;
            case 56:
                Fetch(ecm ? 0x39FF : 0x3FFF);                      // idle access (3.6.3 "i")
                SpriteDmaCheck();
                break;
            case 57:
                Fetch(ecm ? 0x39FF : 0x3FFF);                      // idle access
                break;
            case 58:
                // 3.7.2 rule 5: RC = 7 -> idle state and VC -> VCBASE; RC incremented in display state.
                if (_rc == 7)
                {
                    _displayState = false;
                    _vcBase = _vc;
                }
                if (_badLine)
                    _displayState = true;
                if (_displayState)
                    _rc = (_rc + 1) & 7;
                // 3.8.1 rule 4: MCBASE -> MC; display on when DMA is on and Y matches.
                for (int n = 0; n < 8; n++)
                {
                    _sprMc[n] = _sprMcBase[n];
                    if (_sprDma[n])
                    {
                        if (_regs[(n << 1) | 1] == (byte)_raster)
                        {
                            _sprDisplay[n] = true;
                            _sprDisplayMask |= 1 << n;
                        }
                    }
                    else
                    {
                        _sprDisplay[n] = false;
                        _sprActive[n] = false;
                        _sprDisplayMask &= ~(1 << n);
                    }
                }
                SpritePointerAccess(0);
                break;
            case 59: SpriteDataAccess(0); break;
            case 60: SpritePointerAccess(1); break;
            case 61: SpriteDataAccess(1); break;
            case 62: SpritePointerAccess(2); break;
            case 63:
                // 3.9 rules 2/3: vertical border flip-flop checked against the top/bottom lines in cycle 63.
                if (_raster == ((_regs[0x11] & 8) != 0 ? DisplayWindowBottom25 : DisplayWindowBottom24))
                    _verticalBorder = true;
                if (_raster == ((_regs[0x11] & 8) != 0 ? DisplayWindowTop25 : DisplayWindowTop24) && den)
                    _verticalBorder = false;
                SpriteDataAccess(2);
                break;
        }

        // BA: cycles 12-54 on a bad line (three cycles before the first c-access in 15, until the last in 54)
        // and, for every sprite with DMA on, from three cycles before its pointer fetch until its last s-access
        // (3.6.3 diagrams; a DMA turned on in cycle 55 pulls BA low in the same cycle for sprite 0).
        bool ba = _badLine && cycle >= 12 && cycle <= 54;
        if (!ba)
        {
            for (int n = 0; n < 8; n++)
            {
                if (!_sprDma[n]) continue;
                int d = cycle - SpriteBaStart[n];
                if (d < 0) d += CyclesPerLine;
                if (d < 5) { ba = true; break; }
            }
        }
        if (_fastMode) ba = false;                                 // 2 MHz mode: the VIC never takes the bus
        _ba = ba;
        _baCount = ba ? _baCount + 1 : 0;

        // The 8 pixels of this cycle.
        RenderCycle();

        // Second clock phase accesses.
        switch (cycle)
        {
            case 1: SpriteDataAccess(3); break;
            case 2: SpriteDataAccess(3); break;
            case 3: SpriteDataAccess(4); break;
            case 4: SpriteDataAccess(4); break;
            case 5: SpriteDataAccess(5); break;
            case 6: SpriteDataAccess(5); break;
            case 7: SpriteDataAccess(6); break;
            case 8: SpriteDataAccess(6); break;
            case 9: SpriteDataAccess(7); break;
            case 10: SpriteDataAccess(7); break;
            case >= 15 and <= 54: MatrixAccess(); break;
            case 58: SpriteDataAccess(0); break;
            case 59: SpriteDataAccess(0); break;
            case 60: SpriteDataAccess(1); break;
            case 61: SpriteDataAccess(1); break;
            case 62: SpriteDataAccess(2); break;
            case 63: SpriteDataAccess(2); break;
        }

        if (cycle == CyclesPerLine && _line == LinesPerFrame - 1)
        {
            _frameCount++;
            FrameCompleted?.Invoke();
        }
    }

    /// <summary>A VIC memory access; in 2 MHz mode the VIC gets no bus cycle and sees an unloaded bus ($FF).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte Fetch(int address14) => _fastMode ? (byte)0xFF : _memory.ReadVic(address14);

    /// <summary>3.8.1 rule 3 (cycles 55 and 56): sprite enabled and Y = RASTER bits 0-7 turns the DMA on.</summary>
    private void SpriteDmaCheck()
    {
        byte enable = _regs[0x15];
        byte rasterLow = (byte)_raster;
        for (int n = 0; n < 8; n++)
        {
            int bit = 1 << n;
            if ((enable & bit) != 0 && !_sprDma[n] && _regs[(n << 1) | 1] == rasterLow)
            {
                _sprDma[n] = true;
                _sprMcBase[n] = 0;
                if ((_regs[0x17] & bit) != 0)
                    _sprExpFF[n] = false;
            }
        }
    }

    /// <summary>p-access: sprite pointer from the last 8 bytes of the video matrix (3.8.1; always done).</summary>
    private void SpritePointerAccess(int n)
    {
        int vm = (_regs[0x18] & 0xF0) << 6;
        _sprPointer[n] = Fetch(vm | 0x3F8 | n);
    }

    /// <summary>
    /// s-access: one byte of sprite data at pointer * 64 + MC into the 24 bit shift register (first byte to the
    /// upper 8 bits), MC incremented; only when the DMA of the sprite is on (3.8.1 rule 5).
    /// </summary>
    private void SpriteDataAccess(int n)
    {
        if (!_sprDma[n]) return;
        byte data = Fetch((_sprPointer[n] << 6) | _sprMc[n]);
        _sprShift[n] = ((_sprShift[n] << 8) | data) & 0xFFFFFF;
        _sprMc[n] = (_sprMc[n] + 1) & 0x3F;
    }

    /// <summary>r-access: DRAM refresh, address $3Fxx from the decrementing REF counter (3.6.3).</summary>
    private void RefreshAccess()
    {
        Fetch(0x3F00 | _refresh);
        _refresh--;
    }

    /// <summary>
    /// g-access (3.7.3): character generator / bitmap data in display state (VC and VMLI incremented
    /// afterwards, 3.7.2 rule 4), $3FFF ($39FF with ECM) in idle state. The data is displayed from pixel
    /// 4 + XSCROLL of this cycle (see the class remarks).
    /// </summary>
    private void GraphicsAccess(bool ecm)
    {
        int address;
        byte v = 0, c = 0;
        if (_displayState)
        {
            v = _vbuf[_vmli];
            c = _cbuf[_vmli];
            if ((_regs[0x11] & 0x20) != 0)
                address = ((_regs[0x18] & 0x08) << 10) | (_vc << 3) | _rc;        // BMM: CB13 | VC | RC
            else
                address = ((_regs[0x18] & 0x0E) << 10) | (v << 3) | _rc;          // CB13-11 | D7-0 | RC
            if (ecm)
                address &= 0x39FF;                                                // ECM: address bits 9 and 10 cleared
            _vc = (_vc + 1) & 0x3FF;
            _vmli = (_vmli + 1) & 0x3F;
        }
        else
        {
            address = ecm ? 0x39FF : 0x3FFF;
        }
        _fetchG = Fetch(address);
        _fetchV = v;
        _fetchC = c;
        _fetchValid = true;
    }

    /// <summary>
    /// c-access (3.7.2 rule 3, 3.7.3): video matrix byte and color nibble at VC into the line buffer at VMLI in
    /// the φ2 of cycles 15-54 while the bad line condition holds. If the CPU has not yet released the bus (AEC
    /// not yet asserted because BA went low less than three cycles ago) the VIC reads $FF from the data bus.
    /// </summary>
    private void MatrixAccess()
    {
        if (!_badLine) return;
        if (Aec)
        {
            int vm = (_regs[0x18] & 0xF0) << 6;
            _vbuf[_vmli] = Fetch(vm | _vc);
            _lastColor = _fastMode ? _lastColor : (byte)(_memory.ReadColor(_vc) & 0x0F);
            _cbuf[_vmli] = _lastColor;
        }
        else
        {
            _vbuf[_vmli] = 0xFF;
            _cbuf[_vmli] = _lastColor;
        }
    }
}
