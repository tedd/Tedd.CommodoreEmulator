using System;
using System.Collections.Generic;

namespace Tedd.Commodore.Emulator.C64;

/// <summary>
/// Keys of the C64 keyboard identified by their position in the 8x8 matrix: value = row * 8 + column, where
/// rows are driven by CIA1 port A (PA0-PA7) and columns are read on CIA1 port B (PB0-PB7).
/// (C64 Programmer's Reference Guide, keyboard matrix table.)
/// </summary>
public enum C64Key : byte
{
    // Row 0 (PA0)
    Delete = 0, Return = 1, CursorRight = 2, F7 = 3, F1 = 4, F3 = 5, F5 = 6, CursorDown = 7,
    // Row 1 (PA1)
    D3 = 8, W = 9, A = 10, D4 = 11, Z = 12, S = 13, E = 14, LeftShift = 15,
    // Row 2 (PA2)
    D5 = 16, R = 17, D = 18, D6 = 19, C = 20, F = 21, T = 22, X = 23,
    // Row 3 (PA3)
    D7 = 24, Y = 25, G = 26, D8 = 27, B = 28, H = 29, U = 30, V = 31,
    // Row 4 (PA4)
    D9 = 32, I = 33, J = 34, D0 = 35, M = 36, K = 37, O = 38, N = 39,
    // Row 5 (PA5)
    Plus = 40, P = 41, L = 42, Minus = 43, Period = 44, Colon = 45, At = 46, Comma = 47,
    // Row 6 (PA6)
    Pound = 48, Asterisk = 49, Semicolon = 50, Home = 51, RightShift = 52, Equals = 53, ArrowUp = 54, Slash = 55,
    // Row 7 (PA7)
    D1 = 56, ArrowLeft = 57, Control = 58, D2 = 59, Space = 60, Commodore = 61, Q = 62, RunStop = 63,

    // The 24 extra keys of the Commodore 128 sit on three more row lines (K0-K2) that the VIC-IIe drives through
    // $D02F instead of CIA 1; a C64 never selects those rows, so these keys simply do nothing there.
    // Row 8 (K0)
    Help = 64, Keypad8 = 65, Keypad5 = 66, Tab = 67, Keypad2 = 68, Keypad4 = 69, Keypad7 = 70, Keypad1 = 71,
    // Row 9 (K1)
    Escape = 72, KeypadPlus = 73, KeypadMinus = 74, LineFeed = 75, KeypadEnter = 76, Keypad6 = 77, Keypad9 = 78, Keypad3 = 79,
    // Row 10 (K2)
    Alt = 80, Keypad0 = 81, KeypadPeriod = 82, Up = 83, Down = 84, Left = 85, Right = 86, NoScroll = 87,
}

/// <summary>
/// The keyboard matrix. Any number of keys can be held. The matrix is passive, so it can be read in both
/// directions (rows driven → columns read, and columns driven → rows read); both are computed exactly like the
/// wiring does it, including "ghosting" when several keys are held.
/// </summary>
public sealed class Keyboard
{
    /// <summary>Rows driven by CIA 1 port A.</summary>
    public const int CiaRows = 8;
    /// <summary>Total rows including the three C128 rows driven by the VIC-IIe (K0-K2).</summary>
    public const int Rows = 11;

    private readonly bool[] _pressed = new bool[Rows * 8];
    private readonly byte[] _rowMasks = new byte[Rows];  // for each row (PA / K line) the columns (PB bits) pressed
    private readonly byte[] _columnMasks = new byte[8]; // for each column (PB line) the CIA rows (PA bits) pressed

    /// <summary>
    /// Levels of the three extra row lines K0-K2 of the C128 (bits 0-2, 1 = high = not selected), as driven by
    /// the VIC-IIe's $D02F. A C64 leaves them high, so the extended keys never show up there.
    /// </summary>
    public int ExtendedRowLevels = 7;

    /// <summary>The RESTORE key is not part of the matrix; it pulses the NMI line.</summary>
    public bool RestorePressed { get; private set; }

    public bool IsPressed(C64Key key) => _pressed[(int)key];

    public void Press(C64Key key) => Set(key, true);
    public void Release(C64Key key) => Set(key, false);
    public void Set(C64Key key, bool pressed)
    {
        int k = (int)key;
        if (_pressed[k] == pressed) return;
        _pressed[k] = pressed;
        int row = k >> 3, col = k & 7;
        if (pressed)
        {
            _rowMasks[row] |= (byte)(1 << col);
            if (row < CiaRows) _columnMasks[col] |= (byte)(1 << row);
        }
        else
        {
            _rowMasks[row] &= (byte)~(1 << col);
            if (row < CiaRows) _columnMasks[col] &= (byte)~(1 << row);
        }
    }

    public void SetRestore(bool pressed) => RestorePressed = pressed;

    public void ReleaseAll()
    {
        Array.Clear(_pressed, 0, _pressed.Length);
        Array.Clear(_rowMasks, 0, _rowMasks.Length);
        Array.Clear(_columnMasks, 0, _columnMasks.Length);
        RestorePressed = false;
    }

    /// <summary>
    /// Column levels (CIA1 port B input) given the row levels driven on CIA1 port A: a pressed key connects its
    /// row line to its column line, so a column reads low when any of its pressed keys sits on a row that is low.
    /// The C128's K0-K2 rows (<see cref="ExtendedRowLevels"/>) take part in the same way.
    /// </summary>
    public byte ReadColumns(byte rowLevels)
    {
        int result = 0xFF;
        int low = ~rowLevels & 0xFF;
        while (low != 0)
        {
            int row = TrailingZeroCount(low);
            low &= low - 1;
            result &= ~_rowMasks[row];
        }
        low = ~ExtendedRowLevels & 7;
        while (low != 0)
        {
            int row = TrailingZeroCount(low);
            low &= low - 1;
            result &= ~_rowMasks[CiaRows + row];
        }
        return (byte)result;
    }

    /// <summary>
    /// Row levels (CIA1 port A input) given the column levels driven on CIA1 port B (the matrix is symmetric).
    /// Only the eight CIA rows can be read back; the K0-K2 lines are outputs of the VIC-IIe.
    /// </summary>
    public byte ReadRows(byte columnLevels)
    {
        int result = 0xFF;
        int low = ~columnLevels & 0xFF;
        while (low != 0)
        {
            int col = TrailingZeroCount(low);
            low &= low - 1;
            result &= ~_columnMasks[col];
        }
        return (byte)result;
    }

    /// <summary>Index of the lowest set bit of a non-zero value (System.Numerics.BitOperations is not in netstandard2.1).</summary>
    private static int TrailingZeroCount(int value)
    {
        int n = 0;
        while ((value & 1) == 0)
        {
            value >>= 1;
            n++;
        }
        return n;
    }

    /// <summary>
    /// Maps a printable character to the key (and shift requirement) that produces it on the C64.
    /// Returns false for characters without a key.
    /// </summary>
    public static bool TryMapChar(char c, out C64Key key, out bool shift)
    {
        shift = false;
        if (c >= 'a' && c <= 'z') c = char.ToUpperInvariant(c);
        else if (c >= 'A' && c <= 'Z') shift = true;
        if (Chars.TryGetValue(c, out var mapped))
        {
            key = mapped.Key;
            shift |= mapped.Shift;
            return true;
        }
        key = default;
        return false;
    }

    private static readonly Dictionary<char, (C64Key Key, bool Shift)> Chars = new()
    {
        ['A'] = (C64Key.A, false), ['B'] = (C64Key.B, false), ['C'] = (C64Key.C, false), ['D'] = (C64Key.D, false),
        ['E'] = (C64Key.E, false), ['F'] = (C64Key.F, false), ['G'] = (C64Key.G, false), ['H'] = (C64Key.H, false),
        ['I'] = (C64Key.I, false), ['J'] = (C64Key.J, false), ['K'] = (C64Key.K, false), ['L'] = (C64Key.L, false),
        ['M'] = (C64Key.M, false), ['N'] = (C64Key.N, false), ['O'] = (C64Key.O, false), ['P'] = (C64Key.P, false),
        ['Q'] = (C64Key.Q, false), ['R'] = (C64Key.R, false), ['S'] = (C64Key.S, false), ['T'] = (C64Key.T, false),
        ['U'] = (C64Key.U, false), ['V'] = (C64Key.V, false), ['W'] = (C64Key.W, false), ['X'] = (C64Key.X, false),
        ['Y'] = (C64Key.Y, false), ['Z'] = (C64Key.Z, false),
        ['0'] = (C64Key.D0, false), ['1'] = (C64Key.D1, false), ['2'] = (C64Key.D2, false), ['3'] = (C64Key.D3, false),
        ['4'] = (C64Key.D4, false), ['5'] = (C64Key.D5, false), ['6'] = (C64Key.D6, false), ['7'] = (C64Key.D7, false),
        ['8'] = (C64Key.D8, false), ['9'] = (C64Key.D9, false),
        [' '] = (C64Key.Space, false), ['\r'] = (C64Key.Return, false), ['\n'] = (C64Key.Return, false),
        ['+'] = (C64Key.Plus, false), ['-'] = (C64Key.Minus, false), ['.'] = (C64Key.Period, false),
        [':'] = (C64Key.Colon, false), ['@'] = (C64Key.At, false), [','] = (C64Key.Comma, false),
        ['£'] = (C64Key.Pound, false), ['*'] = (C64Key.Asterisk, false), [';'] = (C64Key.Semicolon, false),
        ['='] = (C64Key.Equals, false), ['/'] = (C64Key.Slash, false),
        ['!'] = (C64Key.D1, true), ['"'] = (C64Key.D2, true), ['#'] = (C64Key.D3, true), ['$'] = (C64Key.D4, true),
        ['%'] = (C64Key.D5, true), ['&'] = (C64Key.D6, true), ['\''] = (C64Key.D7, true), ['('] = (C64Key.D8, true),
        [')'] = (C64Key.D9, true), ['<'] = (C64Key.Comma, true), ['>'] = (C64Key.Period, true), ['?'] = (C64Key.Slash, true),
        ['['] = (C64Key.Colon, true), [']'] = (C64Key.Semicolon, true), ['^'] = (C64Key.ArrowUp, false), ['_'] = (C64Key.ArrowLeft, false),
    };
}

/// <summary>Digital joystick on a control port (active-low bits 0-4: up, down, left, right, fire).</summary>
public sealed class Joystick
{
    public bool Up, Down, Left, Right, Fire;

    /// <summary>Port pin levels: 1 = released, 0 = pressed.</summary>
    public byte Levels => (byte)(0xFF & ~((Up ? 1 : 0) | (Down ? 2 : 0) | (Left ? 4 : 0) | (Right ? 8 : 0) | (Fire ? 16 : 0)));

    public void Clear() => Up = Down = Left = Right = Fire = false;
}
