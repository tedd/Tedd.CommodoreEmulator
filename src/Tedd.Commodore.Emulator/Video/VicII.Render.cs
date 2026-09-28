namespace Tedd.Commodore.Emulator.Video;

/// <summary>
/// Pixel generation: graphics data sequencer (3.7.3), sprite data sequencers (3.8.1 rule 6), priority and
/// collision detection (3.8.2) and the border unit (3.9), for the 8 pixels of one cycle.
/// </summary>
public sealed partial class VicII
{
    /// <summary>Renders the 8 pixels of the current cycle into <see cref="Frame"/>.</summary>
    private void RenderCycle()
    {
        int fb = _line * FrameWidth + (_cycle - 1) * 8;
        // X coordinate of pixel 0 of this cycle: $194 + 8 * (cycle - 1), wrapping from $1F7 to $000 (3.6.3).
        int x = FirstXOfLine + (_cycle - 1) * 8;
        if (x >= FrameWidth) x -= FrameWidth;

        byte r11 = _regs[0x11];
        byte r16 = _regs[0x16];
        bool den = (r11 & 0x10) != 0;
        int mode = ((r11 & 0x40) >> 4) | ((r11 & 0x20) >> 4) | ((r16 & 0x10) >> 4);   // ECM<<2 | BMM<<1 | MCM
        int loadAt = 4 + (r16 & 7);                                                    // pixel at which the g-data is loaded
        bool csel = (r16 & 8) != 0;
        int left = csel ? DisplayWindowLeft40 : DisplayWindowLeft38;
        int right = csel ? DisplayWindowRight40 : DisplayWindowRight38;
        bool rsel = (r11 & 8) != 0;
        int top = rsel ? DisplayWindowTop25 : DisplayWindowTop24;
        int bottom = rsel ? DisplayWindowBottom25 : DisplayWindowBottom24;
        int borderColor = _regs[0x20];
        int bg0 = _regs[0x21];

        for (int p = 0; p < 8; p++, x++)
        {
            if (x > LastX) x = 0;

            // Border unit (3.9), comparisons on the X coordinate of the pixel about to be displayed:
            // rule 1: right compare sets the main flip-flop;
            // rules 4/5: left compare sets/resets the vertical flip-flop on the bottom/top line (reset needs DEN);
            // rule 6: left compare resets the main flip-flop if the vertical flip-flop is not set.
            if (x == right)
                _mainBorder = true;
            if (x == left)
            {
                if (_raster == bottom) _verticalBorder = true;
                if (_raster == top && den) _verticalBorder = false;
                if (!_verticalBorder) _mainBorder = false;
            }

            // Graphics data sequencer: reload with the g-access data at pixel 4 + XSCROLL (data fetched in this
            // cycle for XSCROLL 0-3, in the previous cycle for XSCROLL 4-7); shifted by one bit every pixel.
            if (p == loadAt)
            {
                if (_fetchValid) { _gShift = _fetchG; _vLatch = _fetchV; _cLatch = _fetchC; _gOdd = false; }
            }
            else if (p == loadAt - 8)
            {
                if (_prevValid) { _gShift = _prevG; _vLatch = _prevV; _cLatch = _prevC; _gOdd = false; }
            }
            int bit = (_gShift >> 7) & 1;
            if (!_gOdd) _gPair = (_gShift >> 6) & 3;           // multicolor: two bits at a time, shown for 2 pixels
            int pair = _gPair;
            _gShift = (_gShift << 1) & 0xFF;
            _gOdd = !_gOdd;

            // 3.7.3: while the vertical border flip-flop is set the sequencer outputs the background color only.
            if (_verticalBorder) { bit = 0; pair = 0; }

            int color;
            bool foreground;
            switch (mode)
            {
                default: // 0: standard text mode (3.7.3.1)
                    foreground = bit != 0;
                    color = foreground ? _cLatch : bg0;
                    break;
                case 1: // multicolor text mode (3.7.3.2): MC flag = bit 3 of the color nibble
                    if ((_cLatch & 8) != 0)
                    {
                        foreground = pair >= 2;
                        color = pair switch { 0 => bg0, 1 => _regs[0x22], 2 => _regs[0x23], _ => _cLatch & 7 };
                    }
                    else
                    {
                        foreground = bit != 0;
                        color = foreground ? _cLatch & 7 : bg0;
                    }
                    break;
                case 2: // standard bitmap mode (3.7.3.3)
                    foreground = bit != 0;
                    color = foreground ? _vLatch >> 4 : _vLatch & 0x0F;
                    break;
                case 3: // multicolor bitmap mode (3.7.3.4)
                    foreground = pair >= 2;
                    color = pair switch { 0 => bg0, 1 => _vLatch >> 4, 2 => _vLatch & 0x0F, _ => _cLatch };
                    break;
                case 4: // ECM text mode (3.7.3.5): background color selected by the upper two bits of the char code
                    foreground = bit != 0;
                    color = foreground ? _cLatch : _regs[0x21 + (_vLatch >> 6)];
                    break;
                case 5: // invalid text mode (3.7.3.6): black, foreground as in multicolor text mode
                    foreground = (_cLatch & 8) != 0 ? pair >= 2 : bit != 0;
                    color = 0;
                    break;
                case 6: // invalid bitmap mode 1 (3.7.3.7): black, foreground as in standard bitmap mode
                    foreground = bit != 0;
                    color = 0;
                    break;
                case 7: // invalid bitmap mode 2 (3.7.3.8): black, foreground as in multicolor bitmap mode
                    foreground = pair >= 2;
                    color = 0;
                    break;
            }

            if (_sprDisplayMask != 0)
                color = RenderSprites(x, color, foreground);

            // 3.9: the main border flip-flop covers the sequencer outputs with the border color.
            if (_mainBorder)
                color = borderColor;

            Frame[fb + p] = Palette[color];
        }
    }

    /// <summary>
    /// Sprite data sequencers for one pixel (3.8.1 rule 6): each displayed sprite starts shifting when the
    /// X coordinate matches; MxXE halves the shift rate, MxMC shifts two bits at a time. Returns the composited
    /// color (lower sprite number in front; MxDP puts the sprite behind foreground graphics) and records
    /// sprite-sprite / sprite-graphics collisions (3.8.2), raising the IRQ latch only when the register was
    /// empty (i.e. the first collision since it was read).
    /// </summary>
    private int RenderSprites(int x, int gfxColor, bool foreground)
    {
        int mask = 0;
        int topSprite = -1;
        int topColor = 0;
        byte mcReg = _regs[0x1C];
        byte xeReg = _regs[0x1D];

        for (int n = 0; n < 8; n++)
        {
            int bitN = 1 << n;
            if ((_sprDisplayMask & bitN) == 0)
                continue;
            if (!_sprActive[n])
            {
                if (_sprX[n] != x)
                    continue;
                _sprActive[n] = true;
                _sprShifted[n] = 0;
                _sprSub[n] = 0;
            }

            bool mc = (mcReg & bitN) != 0;
            int c = -1;
            if (mc)
            {
                switch ((_sprShift[n] >> 22) & 3)
                {
                    case 1: c = _regs[0x25]; break;      // "01": sprite multicolor 0
                    case 2: c = _regs[0x27 + n]; break;  // "10": sprite color
                    case 3: c = _regs[0x26]; break;      // "11": sprite multicolor 1
                }
            }
            else if ((_sprShift[n] & 0x800000) != 0)
            {
                c = _regs[0x27 + n];
            }
            if (c >= 0)
            {
                mask |= bitN;
                if (topSprite < 0) { topSprite = n; topColor = c; }
            }

            // Advance: one bit per pixel, every second pixel with X expansion, two bits per step in multicolor.
            int period = ((xeReg & bitN) != 0 ? 2 : 1) << (mc ? 1 : 0);
            if (++_sprSub[n] >= period)
            {
                _sprSub[n] = 0;
                int nb = mc ? 2 : 1;
                _sprShift[n] = (_sprShift[n] << nb) & 0xFFFFFF;
                _sprShifted[n] += nb;
                if (_sprShifted[n] >= 24)
                    _sprActive[n] = false;
            }
        }

        if (mask == 0)
            return gfxColor;

        if ((mask & (mask - 1)) != 0)
        {
            // 3.8.2: two or more sprites with non-transparent pixels at the same position.
            if (_regs[0x1E] == 0) SetIrq(IrqSpriteSprite);
            _regs[0x1E] |= (byte)mask;
        }
        if (foreground)
        {
            // 3.8.2: sprite pixel over a foreground graphics pixel (multicolor: only "10" and "11").
            if (_regs[0x1F] == 0) SetIrq(IrqSpriteData);
            _regs[0x1F] |= (byte)mask;
            if ((_regs[0x1B] & (1 << topSprite)) != 0)
                return gfxColor;                                   // MxDP = 1: graphics in front of the sprite
        }
        return topColor;
    }
}
