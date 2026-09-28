using System;
using System.Text;

namespace Tedd.Commodore.Emulator.Media;

/// <summary>
/// Selects which of the two C64 character sets a PETSCII or screen code is interpreted in.
/// The set is chosen by VIC-II register $D018 (character generator base) and toggled with SHIFT+C= on the
/// keyboard; both halves live in the 4K character ROM ("Commodore 64 Programmer's Reference Guide",
/// appendix E "Screen Display Codes" and appendix F "ASCII and CHR$ Codes").
/// </summary>
public enum PetsciiCharset
{
    /// <summary>
    /// The power-on set (first 2K of the character ROM): PETSCII $41-$5A / screen codes 1-26 show as
    /// upper case letters and $C1-$DA / screen codes 65-90 show as graphics symbols.
    /// </summary>
    UppercaseGraphics,

    /// <summary>
    /// The second set (last 2K of the character ROM): PETSCII $41-$5A / screen codes 1-26 show as lower
    /// case letters and $C1-$DA / screen codes 65-90 show as upper case letters.
    /// </summary>
    LowercaseUppercase,
}

/// <summary>
/// PETSCII (the CBM variant of ASCII used by the KERNAL, BASIC and all Commodore file formats), screen
/// code (the byte values stored in the video matrix at $0400) and ASCII/Unicode conversion helpers.
///
/// The PETSCII layout (appendix F of the C64 Programmer's Reference Guide):
/// <code>
///   $00-$1F control codes ($0D = RETURN, $11/$91 cursor down/up, $12/$92 RVS on/off, $93 CLR, $0E/$8E charset)
///   $20-$3F same as ASCII (space, punctuation, digits)
///   $40-$5F '@', letters (lower case in the second set, upper case in the first), '[', '£', ']', '↑', '←'
///   $60-$7F same glyphs as $C0-$DF
///   $80-$9F control codes (colours, function keys, shifted RETURN)
///   $A0-$BF graphics (shifted space at $A0), same glyphs as $E0-$FF
///   $C0-$DF horizontal bar, upper case letters (second set) / graphics (first set), ...
///   $E0-$FF same glyphs as $A0-$BF ($FF = π)
/// </code>
/// Screen codes (appendix E): 0 = '@', 1-26 letters, 27-31 '[' '£' ']' '↑' '←', 32-63 same as ASCII,
/// 64-95 graphics or (second set) horizontal bar + upper case letters, 96-127 graphics, bit 7 = reverse video.
/// </summary>
public static class Petscii
{
    /// <summary>PETSCII RETURN (CHR$(13)); what '\n' is converted to when typing.</summary>
    public const byte Return = 0x0D;

    /// <summary>Value used for characters that have no representation in the target set.</summary>
    public const char Unmappable = '?';

    /// <summary>
    /// Converts one PETSCII code to the closest ASCII/Unicode character.
    /// Letters $41-$5A become 'A'-'Z' in <see cref="PetsciiCharset.UppercaseGraphics"/> (the set the machine boots in,
    /// so tape/disk names such as "FROGGER 64+" read as written) and 'a'-'z' in
    /// <see cref="PetsciiCharset.LowercaseUppercase"/>; $C1-$DA (and the $61-$7A aliases) become 'A'-'Z' in both.
    /// RETURN ($0D, $8D) becomes '\n'; shifted space ($A0) becomes ' '; other control and graphics codes become
    /// <see cref="Unmappable"/>.
    /// </summary>
    public static char PetsciiToAscii(byte petscii) => PetsciiToAscii(petscii, PetsciiCharset.UppercaseGraphics);

    /// <inheritdoc cref="PetsciiToAscii(byte)"/>
    public static char PetsciiToAscii(byte petscii, PetsciiCharset charset)
    {
        switch (petscii)
        {
            case 0x0D:
            case 0x8D:
                return '\n';
            case 0xA0:
            case 0xE0: // shifted space (alias of $A0), see appendix F: $E0-$FF repeats $A0-$BF
                return ' ';
            case 0x5C:
                return '£';
            case 0x5E:
                return '↑';
            case 0x5F:
                return '←';
            case 0xFF:
                return 'π';
        }

        if (petscii >= 0x20 && petscii <= 0x40)
            return (char)petscii; // space, punctuation, digits, '@' are shared with ASCII
        if (petscii >= 0x41 && petscii <= 0x5A)
            return charset == PetsciiCharset.UppercaseGraphics ? (char)petscii : (char)(petscii + 0x20);
        if (petscii == 0x5B || petscii == 0x5D)
            return (char)petscii; // '[' and ']'
        if (petscii >= 0xC1 && petscii <= 0xDA)
            return (char)(petscii - 0x80); // shifted letters: 'A'-'Z' in the second set, graphics in the first (mapped to letters anyway)
        if (petscii >= 0x61 && petscii <= 0x7A)
            return (char)(petscii - 0x20); // $60-$7F is an alias of $C0-$DF (appendix F)
        return Unmappable;
    }

    /// <summary>
    /// Converts an ASCII/Unicode character to PETSCII: 'a'-'z' become $41-$5A, 'A'-'Z' become $C1-$DA (so text typed
    /// in the second character set keeps its case), '\n' and '\r' become RETURN ($0D), '£' '↑' '←' 'π' become
    /// $5C $5E $5F $FF, ASCII $20-$40 '[' ']' map to themselves, everything else becomes '?' ($3F).
    /// This is the inverse of <see cref="PetsciiToAscii(byte, PetsciiCharset)"/> with <see cref="PetsciiCharset.LowercaseUppercase"/>.
    /// </summary>
    public static byte AsciiToPetscii(char c)
    {
        switch (c)
        {
            case '\n':
            case '\r':
                return Return;
            case '£':
                return 0x5C;
            case '↑':
            case '^':
                return 0x5E;
            case '←':
                return 0x5F;
            case 'π':
                return 0xFF;
            case '[':
            case ']':
                return (byte)c;
        }

        if (c >= 'a' && c <= 'z')
            return (byte)(c - 0x20);
        if (c >= 'A' && c <= 'Z')
            return (byte)(c + 0x80);
        if (c >= 0x20 && c <= 0x40)
            return (byte)c;
        return (byte)Unmappable;
    }

    /// <summary>Converts a string to PETSCII, one byte per character (see <see cref="AsciiToPetscii(char)"/>).</summary>
    public static byte[] AsciiToPetscii(string text)
    {
        if (text is null) throw new ArgumentNullException(nameof(text));
        var result = new byte[text.Length];
        for (int i = 0; i < text.Length; i++)
            result[i] = AsciiToPetscii(text[i]);
        return result;
    }

    /// <summary>Converts a PETSCII byte sequence to a string (see <see cref="PetsciiToAscii(byte, PetsciiCharset)"/>).</summary>
    public static string PetsciiToAscii(ReadOnlySpan<byte> petscii, PetsciiCharset charset = PetsciiCharset.UppercaseGraphics)
    {
        var sb = new StringBuilder(petscii.Length);
        for (int i = 0; i < petscii.Length; i++)
            sb.Append(PetsciiToAscii(petscii[i], charset));
        return sb.ToString();
    }

    /// <summary>
    /// Converts text to the PETSCII codes that would be produced by typing it on the C64 keyboard, suitable for
    /// stuffing into the KERNAL keyboard buffer ($0277-$0280, count in $C6, max 10 characters per batch on an
    /// unmodified KERNAL - the caller must split longer strings). '\n' / '\r' become RETURN ($0D).
    /// With <paramref name="preserveCase"/> false (the default) both 'a' and 'A' produce the unshifted key code
    /// $41-$5A, which is what BASIC expects for keywords ("RUN") in the power-on character set; with it true
    /// upper case letters produce the shifted codes $C1-$DA (only sensible in the lower/upper case set).
    /// </summary>
    public static byte[] AsciiToKeyboardBuffer(string text, bool preserveCase = false)
    {
        if (text is null) throw new ArgumentNullException(nameof(text));
        var result = new byte[text.Length];
        for (int i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (!preserveCase && c >= 'A' && c <= 'Z')
                c = (char)(c + 0x20);
            result[i] = AsciiToPetscii(c);
        }
        return result;
    }

    /// <summary>
    /// Converts a PETSCII code to the screen code the KERNAL would store in the video matrix when printing it
    /// (the table used by the KERNAL screen editor, $E716 "output to screen"; also documented on codebase64
    /// "PETSCII to screencode conversion"). Control codes ($00-$1F, $80-$9F) do not normally reach the screen;
    /// they map to $80-$9F / $80-$9F (the reverse-video block) which is what POKEing them shows.
    /// </summary>
    public static byte PetsciiToScreenCode(byte petscii)
    {
        // $FF (π) is displayed with the same glyph as $DE, screen code $5E (KERNAL special-cases it).
        if (petscii == 0xFF)
            return 0x5E;
        switch (petscii >> 5)
        {
            case 0: return (byte)(petscii | 0x80); // $00-$1F -> $80-$9F
            case 1: return petscii;                // $20-$3F -> $20-$3F
            case 2: return (byte)(petscii & 0x1F); // $40-$5F -> $00-$1F
            case 3: return (byte)(petscii - 0x20); // $60-$7F -> $40-$5F
            case 4: return petscii;                // $80-$9F -> $80-$9F
            case 5: return (byte)(petscii - 0x40); // $A0-$BF -> $60-$7F
            case 6: return (byte)(petscii - 0x80); // $C0-$DF -> $40-$5F
            default: return (byte)(petscii - 0x80); // $E0-$FE -> $60-$7E
        }
    }

    /// <summary>
    /// Converts a screen code (video matrix byte) back to the PETSCII code that prints it. The reverse video bit
    /// (bit 7) is stripped first, so the result is always in the printable ranges.
    /// </summary>
    public static byte ScreenCodeToPetscii(byte screenCode)
    {
        screenCode &= 0x7F;
        switch (screenCode >> 5)
        {
            case 0: return (byte)(screenCode | 0x40); // 0-31 -> $40-$5F
            case 1: return screenCode;                // 32-63 -> $20-$3F
            case 2: return (byte)(screenCode + 0x80); // 64-95 -> $C0-$DF
            default: return (byte)(screenCode + 0x40); // 96-127 -> $A0-$BF
        }
    }

    /// <summary>
    /// Converts one screen code to ASCII: 0 = '@', 1-26 = letters ('A'-'Z' in the power-on set, 'a'-'z' in the
    /// second set), 27-31 = '[' '£' ']' '↑' '←', 32-63 as ASCII, 65-90 = 'A'-'Z', 96 = shifted space (' '),
    /// other graphics = '?'. The reverse video bit 7 is ignored.
    /// </summary>
    public static char ScreenCodeToAscii(byte screenCode, PetsciiCharset charset = PetsciiCharset.UppercaseGraphics)
        => PetsciiToAscii(ScreenCodeToPetscii(screenCode), charset);

    /// <summary>
    /// Decodes a run of screen codes (for example one 40-byte row of the video matrix at $0400) to a string, using
    /// <see cref="ScreenCodeToAscii(byte, PetsciiCharset)"/> for every byte. Reverse video (bit 7) is stripped.
    /// </summary>
    public static string ScreenCodesToAscii(ReadOnlySpan<byte> screenCodes) => ScreenCodesToAscii(screenCodes, PetsciiCharset.UppercaseGraphics);

    /// <inheritdoc cref="ScreenCodesToAscii(ReadOnlySpan{byte})"/>
    public static string ScreenCodesToAscii(ReadOnlySpan<byte> screenCodes, PetsciiCharset charset)
    {
        var sb = new StringBuilder(screenCodes.Length);
        for (int i = 0; i < screenCodes.Length; i++)
            sb.Append(ScreenCodeToAscii(screenCodes[i], charset));
        return sb.ToString();
    }

    /// <summary>Converts an ASCII character to the screen code that displays it (via <see cref="AsciiToPetscii(char)"/>).</summary>
    public static byte AsciiToScreenCode(char c) => PetsciiToScreenCode(AsciiToPetscii(c));
}
