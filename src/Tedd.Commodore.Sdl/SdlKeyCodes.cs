using System;
using System.Collections.Generic;
using static SDL2.SDL;

namespace Tedd.Commodore.Sdl;

/// <summary>
/// Translates SDL scancodes to W3C <c>KeyboardEvent.code</c> names (and back), which is what
/// <see cref="Tedd.Commodore.Hosting.KeyBindings"/> and <see cref="Tedd.Commodore.Hosting.EmulatorSession.KeyDown"/> expect.
/// </summary>
/// <remarks>
/// SDL scancodes are USB HID usage ids, i.e. physical key positions independent of the active keyboard layout,
/// which is exactly what the W3C code names describe, so the translation is a fixed table. It follows the
/// UI Events KeyboardEvent code Values specification and the USB-usage-to-code table browsers use (Chromium's
/// <c>dom_code_data.inc</c>): both the US <c>\|</c> key and the ISO <c>#~</c> key next to Enter are "Backslash", the key
/// between left Shift and Z on ISO keyboards is "IntlBackslash", and the Japanese keys map to IntlRo/IntlYen/KanaMode/
/// Convert/NonConvert. Keys without a W3C code (KP_A..F, media keys SDL only, ...) translate to null.
/// </remarks>
public static class SdlKeyCodes
{
    private static readonly string?[] Table = BuildTable();
    private static readonly Dictionary<string, SDL_Scancode> Reverse = BuildReverse();

    /// <summary>The W3C code name for <paramref name="scancode"/>, or null when the key has no W3C equivalent.</summary>
    public static string? ToCode(SDL_Scancode scancode)
    {
        int i = (int)scancode;
        return (uint)i < (uint)Table.Length ? Table[i] : null;
    }

    /// <summary>Tries to translate a scancode; false when the key has no W3C code.</summary>
    public static bool TryGetCode(SDL_Scancode scancode, out string code)
    {
        code = ToCode(scancode)!;
        return code is not null;
    }

    /// <summary>
    /// The scancode for a W3C code name (case-insensitive), or <c>SDL_SCANCODE_UNKNOWN</c>. Where several scancodes share a
    /// code ("Backslash", "AltRight", "AudioVolumeMute", "NumpadEqual", "ContextMenu") the primary/US one is returned.
    /// </summary>
    public static SDL_Scancode ToScancode(string code) =>
        Reverse.TryGetValue(code, out var s) ? s : SDL_Scancode.SDL_SCANCODE_UNKNOWN;

    /// <summary>All (scancode, code) pairs in the table.</summary>
    public static IEnumerable<(SDL_Scancode Scancode, string Code)> All
    {
        get
        {
            for (int i = 0; i < Table.Length; i++)
                if (Table[i] is { } code)
                    yield return ((SDL_Scancode)i, code);
        }
    }

    private static string?[] BuildTable()
    {
        var t = new string?[(int)SDL_Scancode.SDL_NUM_SCANCODES];
        void M(SDL_Scancode s, string code) => t[(int)s] = code;

        // Writing system keys
        for (int i = 0; i < 26; i++)
            M(SDL_Scancode.SDL_SCANCODE_A + i, "Key" + (char)('A' + i));
        for (int i = 1; i <= 9; i++)
            M(SDL_Scancode.SDL_SCANCODE_1 + (i - 1), "Digit" + i);
        M(SDL_Scancode.SDL_SCANCODE_0, "Digit0");
        M(SDL_Scancode.SDL_SCANCODE_MINUS, "Minus");
        M(SDL_Scancode.SDL_SCANCODE_EQUALS, "Equal");
        M(SDL_Scancode.SDL_SCANCODE_LEFTBRACKET, "BracketLeft");
        M(SDL_Scancode.SDL_SCANCODE_RIGHTBRACKET, "BracketRight");
        M(SDL_Scancode.SDL_SCANCODE_BACKSLASH, "Backslash");
        M(SDL_Scancode.SDL_SCANCODE_NONUSHASH, "Backslash");      // ISO #~ next to Enter: same code as US \| per W3C
        M(SDL_Scancode.SDL_SCANCODE_SEMICOLON, "Semicolon");
        M(SDL_Scancode.SDL_SCANCODE_APOSTROPHE, "Quote");
        M(SDL_Scancode.SDL_SCANCODE_GRAVE, "Backquote");
        M(SDL_Scancode.SDL_SCANCODE_COMMA, "Comma");
        M(SDL_Scancode.SDL_SCANCODE_PERIOD, "Period");
        M(SDL_Scancode.SDL_SCANCODE_SLASH, "Slash");
        M(SDL_Scancode.SDL_SCANCODE_NONUSBACKSLASH, "IntlBackslash"); // ISO key between left Shift and Z
        M(SDL_Scancode.SDL_SCANCODE_INTERNATIONAL1, "IntlRo");
        M(SDL_Scancode.SDL_SCANCODE_INTERNATIONAL2, "KanaMode");
        M(SDL_Scancode.SDL_SCANCODE_INTERNATIONAL3, "IntlYen");
        M(SDL_Scancode.SDL_SCANCODE_INTERNATIONAL4, "Convert");
        M(SDL_Scancode.SDL_SCANCODE_INTERNATIONAL5, "NonConvert");
        M(SDL_Scancode.SDL_SCANCODE_LANG1, "Lang1");
        M(SDL_Scancode.SDL_SCANCODE_LANG2, "Lang2");
        M(SDL_Scancode.SDL_SCANCODE_LANG3, "Lang3");
        M(SDL_Scancode.SDL_SCANCODE_LANG4, "Lang4");
        M(SDL_Scancode.SDL_SCANCODE_LANG5, "Lang5");

        // Functional keys
        M(SDL_Scancode.SDL_SCANCODE_RETURN, "Enter");
        M(SDL_Scancode.SDL_SCANCODE_ESCAPE, "Escape");
        M(SDL_Scancode.SDL_SCANCODE_BACKSPACE, "Backspace");
        M(SDL_Scancode.SDL_SCANCODE_TAB, "Tab");
        M(SDL_Scancode.SDL_SCANCODE_SPACE, "Space");
        M(SDL_Scancode.SDL_SCANCODE_CAPSLOCK, "CapsLock");
        M(SDL_Scancode.SDL_SCANCODE_LCTRL, "ControlLeft");
        M(SDL_Scancode.SDL_SCANCODE_LSHIFT, "ShiftLeft");
        M(SDL_Scancode.SDL_SCANCODE_LALT, "AltLeft");
        M(SDL_Scancode.SDL_SCANCODE_LGUI, "MetaLeft");
        M(SDL_Scancode.SDL_SCANCODE_RCTRL, "ControlRight");
        M(SDL_Scancode.SDL_SCANCODE_RSHIFT, "ShiftRight");
        M(SDL_Scancode.SDL_SCANCODE_RALT, "AltRight");
        M(SDL_Scancode.SDL_SCANCODE_RGUI, "MetaRight");
        M(SDL_Scancode.SDL_SCANCODE_MODE, "AltRight");            // AltGr reported as "mode" on some platforms
        M(SDL_Scancode.SDL_SCANCODE_APPLICATION, "ContextMenu");
        M(SDL_Scancode.SDL_SCANCODE_MENU, "ContextMenu");
        M(SDL_Scancode.SDL_SCANCODE_POWER, "Power");
        M(SDL_Scancode.SDL_SCANCODE_SLEEP, "Sleep");
        M(SDL_Scancode.SDL_SCANCODE_EJECT, "Eject");

        // Control pad
        M(SDL_Scancode.SDL_SCANCODE_INSERT, "Insert");
        M(SDL_Scancode.SDL_SCANCODE_HOME, "Home");
        M(SDL_Scancode.SDL_SCANCODE_PAGEUP, "PageUp");
        M(SDL_Scancode.SDL_SCANCODE_DELETE, "Delete");
        M(SDL_Scancode.SDL_SCANCODE_END, "End");
        M(SDL_Scancode.SDL_SCANCODE_PAGEDOWN, "PageDown");
        M(SDL_Scancode.SDL_SCANCODE_RIGHT, "ArrowRight");
        M(SDL_Scancode.SDL_SCANCODE_LEFT, "ArrowLeft");
        M(SDL_Scancode.SDL_SCANCODE_DOWN, "ArrowDown");
        M(SDL_Scancode.SDL_SCANCODE_UP, "ArrowUp");

        // Numeric keypad
        M(SDL_Scancode.SDL_SCANCODE_NUMLOCKCLEAR, "NumLock");
        M(SDL_Scancode.SDL_SCANCODE_KP_DIVIDE, "NumpadDivide");
        M(SDL_Scancode.SDL_SCANCODE_KP_MULTIPLY, "NumpadMultiply");
        M(SDL_Scancode.SDL_SCANCODE_KP_MINUS, "NumpadSubtract");
        M(SDL_Scancode.SDL_SCANCODE_KP_PLUS, "NumpadAdd");
        M(SDL_Scancode.SDL_SCANCODE_KP_ENTER, "NumpadEnter");
        for (int i = 1; i <= 9; i++)
            M(SDL_Scancode.SDL_SCANCODE_KP_1 + (i - 1), "Numpad" + i);
        M(SDL_Scancode.SDL_SCANCODE_KP_0, "Numpad0");
        M(SDL_Scancode.SDL_SCANCODE_KP_PERIOD, "NumpadDecimal");
        M(SDL_Scancode.SDL_SCANCODE_KP_EQUALS, "NumpadEqual");
        M(SDL_Scancode.SDL_SCANCODE_KP_EQUALSAS400, "NumpadEqual");
        M(SDL_Scancode.SDL_SCANCODE_KP_COMMA, "NumpadComma");
        M(SDL_Scancode.SDL_SCANCODE_KP_LEFTPAREN, "NumpadParenLeft");
        M(SDL_Scancode.SDL_SCANCODE_KP_RIGHTPAREN, "NumpadParenRight");
        M(SDL_Scancode.SDL_SCANCODE_KP_BACKSPACE, "NumpadBackspace");
        M(SDL_Scancode.SDL_SCANCODE_KP_CLEAR, "NumpadClear");
        M(SDL_Scancode.SDL_SCANCODE_KP_CLEARENTRY, "NumpadClearEntry");
        M(SDL_Scancode.SDL_SCANCODE_KP_HASH, "NumpadHash");
        M(SDL_Scancode.SDL_SCANCODE_KP_MEMADD, "NumpadMemoryAdd");
        M(SDL_Scancode.SDL_SCANCODE_KP_MEMCLEAR, "NumpadMemoryClear");
        M(SDL_Scancode.SDL_SCANCODE_KP_MEMRECALL, "NumpadMemoryRecall");
        M(SDL_Scancode.SDL_SCANCODE_KP_MEMSTORE, "NumpadMemoryStore");
        M(SDL_Scancode.SDL_SCANCODE_KP_MEMSUBTRACT, "NumpadMemorySubtract");

        // Function keys
        for (int i = 1; i <= 12; i++)
            M(SDL_Scancode.SDL_SCANCODE_F1 + (i - 1), "F" + i);
        for (int i = 13; i <= 24; i++)
            M(SDL_Scancode.SDL_SCANCODE_F13 + (i - 13), "F" + i);
        M(SDL_Scancode.SDL_SCANCODE_PRINTSCREEN, "PrintScreen");
        M(SDL_Scancode.SDL_SCANCODE_SCROLLLOCK, "ScrollLock");
        M(SDL_Scancode.SDL_SCANCODE_PAUSE, "Pause");

        // Editing / legacy (Sun type 5 keyboards etc.)
        M(SDL_Scancode.SDL_SCANCODE_HELP, "Help");
        M(SDL_Scancode.SDL_SCANCODE_SELECT, "Select");
        M(SDL_Scancode.SDL_SCANCODE_AGAIN, "Again");
        M(SDL_Scancode.SDL_SCANCODE_UNDO, "Undo");
        M(SDL_Scancode.SDL_SCANCODE_CUT, "Cut");
        M(SDL_Scancode.SDL_SCANCODE_COPY, "Copy");
        M(SDL_Scancode.SDL_SCANCODE_PASTE, "Paste");
        M(SDL_Scancode.SDL_SCANCODE_FIND, "Find");
        M(SDL_Scancode.SDL_SCANCODE_CANCEL, "Abort");
        M(SDL_Scancode.SDL_SCANCODE_PRIOR, "Props");

        // Media / browser / launcher keys
        M(SDL_Scancode.SDL_SCANCODE_MUTE, "AudioVolumeMute");
        M(SDL_Scancode.SDL_SCANCODE_AUDIOMUTE, "AudioVolumeMute");
        M(SDL_Scancode.SDL_SCANCODE_VOLUMEUP, "AudioVolumeUp");
        M(SDL_Scancode.SDL_SCANCODE_VOLUMEDOWN, "AudioVolumeDown");
        M(SDL_Scancode.SDL_SCANCODE_AUDIONEXT, "MediaTrackNext");
        M(SDL_Scancode.SDL_SCANCODE_AUDIOPREV, "MediaTrackPrevious");
        M(SDL_Scancode.SDL_SCANCODE_AUDIOSTOP, "MediaStop");
        M(SDL_Scancode.SDL_SCANCODE_AUDIOPLAY, "MediaPlayPause");
        M(SDL_Scancode.SDL_SCANCODE_MEDIASELECT, "MediaSelect");
        M(SDL_Scancode.SDL_SCANCODE_MAIL, "LaunchMail");
        M(SDL_Scancode.SDL_SCANCODE_COMPUTER, "LaunchApp1");
        M(SDL_Scancode.SDL_SCANCODE_CALCULATOR, "LaunchApp2");
        M(SDL_Scancode.SDL_SCANCODE_AC_SEARCH, "BrowserSearch");
        M(SDL_Scancode.SDL_SCANCODE_AC_HOME, "BrowserHome");
        M(SDL_Scancode.SDL_SCANCODE_AC_BACK, "BrowserBack");
        M(SDL_Scancode.SDL_SCANCODE_AC_FORWARD, "BrowserForward");
        M(SDL_Scancode.SDL_SCANCODE_AC_STOP, "BrowserStop");
        M(SDL_Scancode.SDL_SCANCODE_AC_REFRESH, "BrowserRefresh");
        M(SDL_Scancode.SDL_SCANCODE_AC_BOOKMARKS, "BrowserFavorites");

        return t;
    }

    private static Dictionary<string, SDL_Scancode> BuildReverse()
    {
        // First occurrence wins, so the primary key (US Backslash, right Alt, Mute, ...) is the one returned.
        var r = new Dictionary<string, SDL_Scancode>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < Table.Length; i++)
            if (Table[i] is { } code)
                r.TryAdd(code, (SDL_Scancode)i);
        return r;
    }
}
