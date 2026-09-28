using System;
using System.Collections.Generic;

namespace Tedd.Commodore.Unity;

/// <summary>
/// Translates Unity's legacy <c>KeyCode</c> enum member names ("A", "Alpha1", "Return", "LeftShift", "Keypad8",
/// "UpArrow", "BackQuote", ...) to the W3C <c>KeyboardEvent.code</c> names the hosting layer's
/// <see cref="Hosting.KeyBindings"/> use ("KeyA", "Digit1", "Enter", "ShiftLeft", "Numpad8", "ArrowUp",
/// "Backquote", ...). This assembly has no Unity reference, so the enum is handled by name:
/// <c>UnityKeyCodes.ToWebCode(keyCode.ToString())</c>.
/// </summary>
public static class UnityKeyCodes
{
    private static readonly Dictionary<string, string> UnityToWeb = Build();
    private static readonly Dictionary<string, string> WebToUnity = Reverse(UnityToWeb);

    /// <summary>The W3C code for a Unity KeyCode name, or null when the key has no keyboard equivalent (mouse buttons, joystick buttons, unknown names).</summary>
    public static string? ToWebCode(string? unityKeyCodeName)
    {
        if (string.IsNullOrEmpty(unityKeyCodeName)) return null;
        return UnityToWeb.TryGetValue(unityKeyCodeName!, out var code) ? code : null;
    }

    /// <summary>The Unity KeyCode name that produces a W3C code, or null when Unity has no such key.</summary>
    public static string? FromWebCode(string? webCode)
    {
        if (string.IsNullOrEmpty(webCode)) return null;
        return WebToUnity.TryGetValue(webCode!, out var name) ? name : null;
    }

    /// <summary>Every Unity KeyCode name that has a mapping (for building the poll list once).</summary>
    public static IEnumerable<string> UnityKeyCodeNames => UnityToWeb.Keys;

    /// <summary>The full Unity name to W3C code table.</summary>
    public static IReadOnlyDictionary<string, string> All => UnityToWeb;

    private static Dictionary<string, string> Build()
    {
        var m = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (char c = 'A'; c <= 'Z'; c++)
            m[c.ToString()] = "Key" + c;
        for (int d = 0; d <= 9; d++)
        {
            m["Alpha" + d] = "Digit" + d;
            m["Keypad" + d] = "Numpad" + d;
        }
        for (int f = 1; f <= 15; f++)
            m["F" + f] = "F" + f;

        // Keypad
        m["KeypadPeriod"] = "NumpadDecimal";
        m["KeypadDivide"] = "NumpadDivide";
        m["KeypadMultiply"] = "NumpadMultiply";
        m["KeypadMinus"] = "NumpadSubtract";
        m["KeypadPlus"] = "NumpadAdd";
        m["KeypadEnter"] = "NumpadEnter";
        m["KeypadEquals"] = "NumpadEqual";

        // Navigation
        m["UpArrow"] = "ArrowUp";
        m["DownArrow"] = "ArrowDown";
        m["LeftArrow"] = "ArrowLeft";
        m["RightArrow"] = "ArrowRight";
        m["Insert"] = "Insert";
        m["Home"] = "Home";
        m["End"] = "End";
        m["PageUp"] = "PageUp";
        m["PageDown"] = "PageDown";

        // Editing / control
        m["Backspace"] = "Backspace";
        m["Delete"] = "Delete";
        m["Tab"] = "Tab";
        m["Return"] = "Enter";
        m["Pause"] = "Pause";
        m["Break"] = "Pause";
        m["Escape"] = "Escape";
        m["Space"] = "Space";
        m["Print"] = "PrintScreen";
        m["SysReq"] = "PrintScreen";
        m["Menu"] = "ContextMenu";
        m["Numlock"] = "NumLock";
        m["CapsLock"] = "CapsLock";
        m["ScrollLock"] = "ScrollLock";

        // Modifiers
        m["LeftShift"] = "ShiftLeft";
        m["RightShift"] = "ShiftRight";
        m["LeftControl"] = "ControlLeft";
        m["RightControl"] = "ControlRight";
        m["LeftAlt"] = "AltLeft";
        m["RightAlt"] = "AltRight";
        m["AltGr"] = "AltRight";
        m["LeftCommand"] = "MetaLeft";
        m["LeftApple"] = "MetaLeft";
        m["LeftWindows"] = "MetaLeft";
        m["LeftMeta"] = "MetaLeft";
        m["RightCommand"] = "MetaRight";
        m["RightApple"] = "MetaRight";
        m["RightWindows"] = "MetaRight";
        m["RightMeta"] = "MetaRight";

        // Punctuation keys as they are reported for a US layout
        m["BackQuote"] = "Backquote";
        m["Minus"] = "Minus";
        m["Equals"] = "Equal";
        m["LeftBracket"] = "BracketLeft";
        m["RightBracket"] = "BracketRight";
        m["Backslash"] = "Backslash";
        m["Semicolon"] = "Semicolon";
        m["Quote"] = "Quote";
        m["Comma"] = "Comma";
        m["Period"] = "Period";
        m["Slash"] = "Slash";

        // Unity reports these on layouts where the symbol is the key's primary character; map them to the key that
        // carries the symbol on a US keyboard so the default bindings still do something sensible.
        m["Exclaim"] = "Digit1";
        m["At"] = "Digit2";
        m["Hash"] = "Digit3";
        m["Dollar"] = "Digit4";
        m["Percent"] = "Digit5";
        m["Caret"] = "Digit6";
        m["Ampersand"] = "Digit7";
        m["Asterisk"] = "Digit8";
        m["LeftParen"] = "Digit9";
        m["RightParen"] = "Digit0";
        m["Underscore"] = "Minus";
        m["Plus"] = "Equal";
        m["LeftCurlyBracket"] = "BracketLeft";
        m["RightCurlyBracket"] = "BracketRight";
        m["Pipe"] = "Backslash";
        m["Colon"] = "Semicolon";
        m["DoubleQuote"] = "Quote";
        m["Less"] = "Comma";
        m["Greater"] = "Period";
        m["Question"] = "Slash";
        m["Tilde"] = "Backquote";

        return m;
    }

    private static Dictionary<string, string> Reverse(Dictionary<string, string> forward)
    {
        // First (canonical) Unity name wins for codes that several Unity names map to.
        var r = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in forward)
            if (!r.ContainsKey(pair.Value))
                r[pair.Value] = pair.Key;
        return r;
    }
}
