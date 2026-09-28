using System;
using Tedd.Commodore.Emulator.Video;

namespace Tedd.Commodore.Tests.Support;

/// <summary>
/// 16 KiB VIC bank plus 1 KiB color RAM with helpers to set up screens, a synthetic character set, bitmaps and
/// sprites for the VIC-II tests.
/// </summary>
public sealed class FakeVicMemory : IVicMemory
{
    public const int DefaultScreen = 0x0400;   // $D018 = $14: VM13-10 = 0001
    public const int DefaultCharset = 0x1000;  // $D018 = $14: CB13-11 = 010
    public const int DefaultBitmap = 0x2000;   // $D018 bit 3 set: CB13 = 1

    // Synthetic character set
    public const byte CharBlank = 0;
    public const byte CharSolid = 1;
    public const byte CharCheckerboard = 2;   // rows alternate $AA / $55
    public const byte CharLeftHalf = 3;       // $F0
    public const byte CharTopHalf = 4;        // rows 0-3 $FF, rows 4-7 $00
    public const byte CharPairs01 = 5;        // $55 on every row ("01" multicolor pairs)
    public const byte CharA = 65;             // a real 'A' shape

    public readonly byte[] Ram = new byte[0x4000];
    public readonly byte[] ColorRam = new byte[0x400];

    /// <summary>Number of <see cref="ReadVic"/> calls.</summary>
    public long ReadCount;

    public byte ReadVic(int address14)
    {
        ReadCount++;
        return Ram[address14 & 0x3FFF];
    }

    public byte PeekVic(int address14) => Ram[address14 & 0x3FFF];

    public byte ReadColor(int address10) => (byte)(ColorRam[address10 & 0x3FF] & 0x0F);

    public void Fill(int address, int length, byte value)
    {
        for (int i = 0; i < length; i++)
            Ram[(address + i) & 0x3FFF] = value;
    }

    public void FillScreen(byte ch, int screenBase = DefaultScreen) => Fill(screenBase, 1000, ch);

    public void FillColor(byte color)
    {
        for (int i = 0; i < ColorRam.Length; i++)
            ColorRam[i] = (byte)(color & 0x0F);
    }

    public void SetChar(int column, int row, byte ch, int screenBase = DefaultScreen) =>
        Ram[(screenBase + row * 40 + column) & 0x3FFF] = ch;

    public void SetColor(int column, int row, byte color) => ColorRam[row * 40 + column] = (byte)(color & 0x0F);

    /// <summary>Fills every row of the screen with a character taken from <paramref name="charForRow"/>.</summary>
    public void FillRows(Func<int, byte> charForRow, int screenBase = DefaultScreen)
    {
        for (int row = 0; row < 25; row++)
            for (int col = 0; col < 40; col++)
                SetChar(col, row, charForRow(row), screenBase);
    }

    /// <summary>Installs the synthetic character set at <paramref name="charBase"/> (256 chars x 8 bytes).</summary>
    public void InstallCharset(int charBase = DefaultCharset)
    {
        Fill(charBase, 2048, 0);
        SetCharShape(CharSolid, new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF }, charBase);
        SetCharShape(CharCheckerboard, new byte[] { 0xAA, 0x55, 0xAA, 0x55, 0xAA, 0x55, 0xAA, 0x55 }, charBase);
        SetCharShape(CharLeftHalf, new byte[] { 0xF0, 0xF0, 0xF0, 0xF0, 0xF0, 0xF0, 0xF0, 0xF0 }, charBase);
        SetCharShape(CharTopHalf, new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x00, 0x00, 0x00, 0x00 }, charBase);
        SetCharShape(CharPairs01, new byte[] { 0x55, 0x55, 0x55, 0x55, 0x55, 0x55, 0x55, 0x55 }, charBase);
        SetCharShape(CharA, new byte[] { 0x18, 0x3C, 0x66, 0x7E, 0x66, 0x66, 0x66, 0x00 }, charBase);
    }

    public void SetCharShape(int ch, byte[] rows, int charBase = DefaultCharset)
    {
        for (int i = 0; i < 8; i++)
            Ram[(charBase + ch * 8 + i) & 0x3FFF] = rows[i];
    }

    public void FillBitmap(byte value, int bitmapBase = DefaultBitmap) => Fill(bitmapBase, 8000, value);

    /// <summary>Sets the 8 bytes of one 8x8 bitmap cell (cell (column,row) at base + (row*40+column)*8).</summary>
    public void SetBitmapCell(int column, int row, byte[] rows, int bitmapBase = DefaultBitmap)
    {
        for (int i = 0; i < 8; i++)
            Ram[(bitmapBase + (row * 40 + column) * 8 + i) & 0x3FFF] = rows[i];
    }

    /// <summary>Stores 63 bytes of sprite data in 64 byte block <paramref name="block"/> (the pointer value).</summary>
    public void SetSpriteData(int block, byte[] data)
    {
        for (int i = 0; i < 63; i++)
            Ram[(block * 64 + i) & 0x3FFF] = i < data.Length ? data[i] : (byte)0;
    }

    public void SetSpritePointer(int sprite, byte block, int screenBase = DefaultScreen) =>
        Ram[(screenBase + 0x3F8 + sprite) & 0x3FFF] = block;

    public static byte[] SolidSprite()
    {
        var d = new byte[63];
        Array.Fill(d, (byte)0xFF);
        return d;
    }

    /// <summary>Sprite whose every row is the three given bytes.</summary>
    public static byte[] RowSprite(byte b0, byte b1, byte b2)
    {
        var d = new byte[63];
        for (int i = 0; i < 21; i++)
        {
            d[i * 3] = b0;
            d[i * 3 + 1] = b1;
            d[i * 3 + 2] = b2;
        }
        return d;
    }
}
