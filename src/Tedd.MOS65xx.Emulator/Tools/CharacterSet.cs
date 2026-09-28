using System;
using System.Collections.Generic;
using Tedd.MOS65xx.Emulator.Video;

namespace Tedd.MOS65xx.Emulator.Tools;

/// <summary>
/// A block of 8 x 8 text characters - a character generator ROM image, a file the user picked, or the 2 KiB the
/// VIC-II is reading right now - plus the facts a character set viewer wants to show about it: where the block is
/// visible to the CPU and to the VIC, which PETSCII codes print a given screen code, and what each code draws in
/// the two ROM sets.
///
/// Capturing from a machine does not touch the bus: <see cref="FromVic"/> reads through
/// <see cref="IVicMemory.PeekVic"/>. The instance owns its copy of the data, so it stays valid (and stable) while
/// the machine keeps running.
/// </summary>
public sealed class CharacterSet
{
    /// <summary>Character width in pixels. One bit per pixel, MSB leftmost.</summary>
    public const int CharacterWidth = 8;

    /// <summary>Character height in pixels, one byte per row.</summary>
    public const int CharacterHeight = 8;

    /// <summary>Bytes per character: one per pixel row.</summary>
    public const int BytesPerCharacter = CharacterHeight;

    /// <summary>Characters in one set: $00-$7F plus their reverse video copies at $80-$FF.</summary>
    public const int CharactersPerSet = 256;

    /// <summary>Bytes in one character set (2 KiB) - the granularity $D018 selects inside the VIC bank.</summary>
    public const int SetSize = CharactersPerSet * BytesPerCharacter;

    /// <summary>Bytes in a character generator ROM: set 1 (uppercase/graphics) followed by set 2 (lowercase).</summary>
    public const int RomSize = 2 * SetSize;

    /// <summary>Offset of the lowercase/uppercase set inside a ROM image.</summary>
    public const int LowercaseSetOffset = SetSize;

    /// <summary>Where the ROM appears to the CPU while CHAREN ($01 bit 2) is 0 and LORAM or HIRAM is 1.</summary>
    public const int CpuBase = 0xD000;

    /// <summary>Where the ROM is shadowed inside VIC banks 0 and 2 ($1000, i.e. $1000 and $9000 to the CPU).</summary>
    public const int VicShadowBase = 0x1000;

    private readonly byte[] _data;

    /// <param name="data">Glyph bytes, 8 per character; the array is copied.</param>
    /// <param name="name">Where the data came from, for display.</param>
    public CharacterSet(byte[] data, string name)
    {
        if (data is null) throw new ArgumentNullException(nameof(data));
        if (data.Length == 0 || data.Length % BytesPerCharacter != 0)
            throw new ArgumentException($"A character set is a multiple of {BytesPerCharacter} bytes (got {data.Length}).", nameof(data));
        _data = (byte[])data.Clone();
        Name = name;
        Crc32 = ComputeCrc32(_data);
    }

    /// <summary>Where the data came from: a file name, a ROM description or "VIC $xxxx".</summary>
    public string Name { get; }

    /// <summary>The glyph bytes, 8 per character.</summary>
    public ReadOnlySpan<byte> Data => _data;

    /// <summary>Number of characters in the block.</summary>
    public int Count => _data.Length / BytesPerCharacter;

    /// <summary>Size of the block in bytes.</summary>
    public int Size => _data.Length;

    /// <summary>True for a full 4 KiB ROM image, i.e. both character sets.</summary>
    public bool IsRomImage => _data.Length == RomSize;

    /// <summary>CRC-32 (IEEE) of the block, so a character ROM dump can be identified against a database.</summary>
    public uint Crc32 { get; }

    /// <summary>Start of the block inside the 16 KiB VIC bank, or -1 when it was not captured from a VIC.</summary>
    public int VicAddress { get; private set; } = -1;

    /// <summary>Start of the VIC bank in the CPU address space, or -1 when not captured from a VIC.</summary>
    public int BankBase { get; private set; } = -1;

    /// <summary>True when the VIC was reading the character ROM shadow rather than RAM.</summary>
    public bool IsRomShadow { get; private set; }

    /// <summary>The 8 bytes of one character, top row first.</summary>
    public ReadOnlySpan<byte> Glyph(int index)
    {
        if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
        return new ReadOnlySpan<byte>(_data, index * BytesPerCharacter, BytesPerCharacter);
    }

    /// <summary>
    /// Decodes one character into <see cref="CharacterWidth"/> x <see cref="CharacterHeight"/> color indices,
    /// row by row: set bits become <paramref name="ink"/> and clear bits <paramref name="paper"/>, which is what
    /// the VIC does in standard text mode (the color RAM nibble on foreground, background color 0 behind it).
    /// </summary>
    public void Render(int index, Span<byte> pixels, byte ink = 1, byte paper = 0)
    {
        if (pixels.Length < CharacterWidth * CharacterHeight)
            throw new ArgumentException($"Need {CharacterWidth * CharacterHeight} pixels.", nameof(pixels));
        var glyph = Glyph(index);
        for (int row = 0; row < CharacterHeight; row++)
        {
            byte bits = glyph[row];
            int at = row * CharacterWidth;
            for (int x = 0; x < CharacterWidth; x++)
                pixels[at + x] = ((bits >> (7 - x)) & 1) != 0 ? ink : paper;
        }
    }

    /// <summary>Number of set pixels in a character; 0 for a blank one, 64 for a solid block.</summary>
    public int SolidPixelCount(int index)
    {
        var glyph = Glyph(index);
        int count = 0;
        for (int i = 0; i < glyph.Length; i++)
        {
            int b = glyph[i];
            while (b != 0) { count += b & 1; b >>= 1; }
        }
        return count;
    }

    /// <summary>True when the character has no set pixels at all.</summary>
    public bool IsBlank(int index) => SolidPixelCount(index) == 0;

    /// <summary>
    /// How many of the 128 characters at $80-$FF of the set at <paramref name="setOffset"/> are not the exact
    /// inverse of their counterpart at $00-$7F. A character set normally stores the reverse video half inverted,
    /// so this is 0 - but real ROM dumps are not always byte for byte (a glyph and its reverse can differ in a
    /// row), and a custom set may put unrelated glyphs there, which is then what CHR$(18) draws.
    /// </summary>
    public int InvertedMismatchCount(int setOffset)
    {
        if (setOffset < 0 || setOffset + SetSize > _data.Length)
            throw new ArgumentOutOfRangeException(nameof(setOffset), $"a whole {SetSize} byte set must start at {setOffset}");
        const int half = CharactersPerSet / 2 * BytesPerCharacter;
        int mismatches = 0;
        for (int c = 0; c < CharactersPerSet / 2; c++)
        {
            int lo = setOffset + c * BytesPerCharacter;
            for (int i = 0; i < BytesPerCharacter; i++)
                if (_data[lo + i] != (byte)~_data[lo + half + i]) { mismatches++; break; }
        }
        return mismatches;
    }

    #region capture

    /// <summary>Wraps a character generator ROM image (4096 bytes) or a single 2048 byte set.</summary>
    public static CharacterSet FromRom(byte[] characterRom, string name) => new(characterRom, name);

    /// <summary>
    /// Reads the 2 KiB the VIC-II is currently using as its character generator: the block at
    /// <see cref="CharacterBaseOf"/>($D018) inside the VIC bank. That is the ROM shadow in banks 0 and 2 at
    /// $1000 and $1800, and RAM everywhere else - which is how software installs its own character set.
    /// </summary>
    /// <param name="vic">The VIC to read through; registers and memory are peeked, never read.</param>
    /// <param name="vicBank">The 16 KiB bank the VIC sees (0..3), used to report CPU addresses.</param>
    public static CharacterSet FromVic(VicII vic, int vicBank = 0)
    {
        if (vic is null) throw new ArgumentNullException(nameof(vic));
        int charBase = CharacterBaseOf(vic.Peek(0x18));
        var data = new byte[SetSize];
        for (int i = 0; i < data.Length; i++)
            data[i] = vic.Memory.PeekVic(charBase + i);
        int bankBase = (vicBank & 3) << 14;
        return new CharacterSet(data, $"VIC ${bankBase + charBase:X4}")
        {
            VicAddress = charBase,
            BankBase = bankBase,
            // The PLA maps the ROM into $1000-$1FFF of the even banks; $3000 in Ultimax mode is ROMH, not the ROM.
            IsRomShadow = (charBase & 0x3000) == 0x1000 && (vicBank & 1) == 0,
        };
    }

    /// <summary>The character generator base inside the VIC bank: $D018 bits 3-1, in 2 KiB steps.</summary>
    public static int CharacterBaseOf(byte d018) => (d018 & 0x0E) << 10;

    /// <summary>The $D018 value that selects <paramref name="characterBase"/>, keeping the video matrix bits.</summary>
    public static byte D018For(int characterBase, byte d018) => (byte)((d018 & 0xF1) | ((characterBase >> 10) & 0x0E));

    #endregion

    #region PETSCII

    /// <summary>
    /// The screen code a PETSCII code prints, or -1 for the two control code blocks ($00-$1F and $80-$9F).
    /// This is the KERNAL's mapping: letters move down by $40, the shifted/graphics halves alias each other, and
    /// $FF (pi) is the odd one out that ends up at $5E.
    /// </summary>
    public static int ScreenCodeOf(int petscii)
    {
        int p = petscii & 0xFF;
        return p switch
        {
            < 0x20 => -1,          // control codes
            < 0x40 => p,           // $20-$3F: space, punctuation, digits
            < 0x60 => p - 0x40,    // $40-$5F: @ A-Z [ pound ] up-arrow left-arrow
            < 0x80 => p - 0x20,    // $60-$7F: shifted letters / graphics
            < 0xA0 => -1,          // control codes
            < 0xC0 => p - 0x40,    // $A0-$BF: C= graphics
            < 0xFF => p - 0x80,    // $C0-$FE: aliases of $40-$7E
            _ => 0x5E,             // $FF (pi) shares the glyph at $5E
        };
    }

    private static readonly int[][] PetsciiCodes = BuildPetsciiCodes();

    private static int[][] BuildPetsciiCodes()
    {
        var lists = new List<int>[0x80];
        for (int i = 0; i < lists.Length; i++) lists[i] = new List<int>();
        for (int p = 0; p < 0x100; p++)
        {
            int screen = ScreenCodeOf(p);
            if (screen >= 0) lists[screen].Add(p);
        }
        var table = new int[lists.Length][];
        for (int i = 0; i < table.Length; i++) table[i] = lists[i].ToArray();
        return table;
    }

    /// <summary>
    /// The PETSCII codes that print <paramref name="screenCode"/> (CHR$ through the screen editor), in ascending
    /// order. Empty for $80-$FF: those are the reverse video copies, printed by turning reverse on with CHR$(18).
    /// </summary>
    public static ReadOnlySpan<int> PetsciiCodesFor(int screenCode) =>
        (screenCode & 0xFF) < 0x80 ? PetsciiCodes[screenCode & 0x7F] : Array.Empty<int>();

    /// <summary>
    /// What a screen code draws in the given ROM set: the letter, digit or symbol it is, or the key combination
    /// that produces the graphics character. $80-$FF are the reverse video copies of $00-$7F.
    /// </summary>
    /// <param name="screenCode">The screen code (0..255).</param>
    /// <param name="lowercaseSet">true for set 2 (lowercase/uppercase), false for set 1 (uppercase/graphics).</param>
    public static string Describe(int screenCode, bool lowercaseSet)
    {
        int code = screenCode & 0xFF;
        string text = DescribeGlyph(code & 0x7F, lowercaseSet);
        return code >= 0x80 ? text + ", reverse video" : text;
    }

    private static string DescribeGlyph(int code, bool lowercaseSet)
    {
        if (code == 0x00) return "'@'";
        if (code <= 0x1A) return "'" + (char)((lowercaseSet ? 'a' : 'A') + code - 1) + "'";
        if (code == 0x1B) return "'['";
        if (code == 0x1C) return "pound sign";
        if (code == 0x1D) return "']'";
        if (code == 0x1E) return "up arrow";
        if (code == 0x1F) return "left arrow";
        if (code == 0x20) return "space";
        if (code < 0x40) return "'" + (char)code + "'";
        if (code >= 0x41 && code <= 0x5A)
            return lowercaseSet ? "'" + (char)('A' + code - 0x41) + "'" : "graphics, SHIFT + " + (char)('A' + code - 0x41);
        if (code >= 0x61 && code <= 0x7A) return "graphics, C= + " + (char)('A' + code - 0x61);
        return "graphics";
    }

    #endregion

    #region CRC-32

    private static readonly uint[] Crc32Table = BuildCrc32Table();

    private static uint[] BuildCrc32Table()
    {
        var table = new uint[256];
        for (uint i = 0; i < table.Length; i++)
        {
            uint c = i;
            for (int bit = 0; bit < 8; bit++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[i] = c;
        }
        return table;
    }

    private static uint ComputeCrc32(byte[] data)
    {
        uint crc = 0xFFFFFFFFu;
        for (int i = 0; i < data.Length; i++)
            crc = Crc32Table[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }

    #endregion
}
