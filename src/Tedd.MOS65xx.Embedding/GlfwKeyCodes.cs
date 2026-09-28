using System.Collections.Generic;

namespace Tedd.MOS65xx.Embedding;

/// <summary>
/// Translates GLFW key codes to the W3C <c>KeyboardEvent.code</c> names the key bindings use. GLFW's numbering
/// (<c>GLFW_KEY_*</c>) is also the value of Silk.NET's <c>Silk.NET.Input.Key</c>, OpenTK's <c>Keys</c> and
/// Raylib-cs's <c>KeyboardKey</c>, which covers the usual windowing layers of a .NET Vulkan program:
/// <c>emulator.KeyDown(GlfwKeyCodes.ToWebCode((int)key))</c>. GLFW key codes name the key at that position on a US
/// keyboard, whatever the layout, which is what the bindings expect.
/// </summary>
public static class GlfwKeyCodes
{
    private static readonly Dictionary<int, string> Codes = Build();

    /// <summary>The W3C code for a GLFW key code, or null for keys that have none (<c>GLFW_KEY_UNKNOWN</c>, unused numbers).</summary>
    public static string? ToWebCode(int glfwKey) => Codes.TryGetValue(glfwKey, out var code) ? code : null;

    /// <summary>The full GLFW key code to W3C code table.</summary>
    public static IReadOnlyDictionary<int, string> All => Codes;

    private static Dictionary<int, string> Build()
    {
        var m = new Dictionary<int, string>
        {
            [32] = "Space",
            [39] = "Quote",
            [44] = "Comma",
            [45] = "Minus",
            [46] = "Period",
            [47] = "Slash",
            [59] = "Semicolon",
            [61] = "Equal",
            [91] = "BracketLeft",
            [92] = "Backslash",
            [93] = "BracketRight",
            [96] = "Backquote",
            // WORLD_1 / WORLD_2: the extra key of ISO keyboards next to the left shift (GLFW reports it as one or the other depending on the platform).
            [161] = "IntlBackslash",
            [162] = "IntlBackslash",

            [256] = "Escape",
            [257] = "Enter",
            [258] = "Tab",
            [259] = "Backspace",
            [260] = "Insert",
            [261] = "Delete",
            [262] = "ArrowRight",
            [263] = "ArrowLeft",
            [264] = "ArrowDown",
            [265] = "ArrowUp",
            [266] = "PageUp",
            [267] = "PageDown",
            [268] = "Home",
            [269] = "End",
            [280] = "CapsLock",
            [281] = "ScrollLock",
            [282] = "NumLock",
            [283] = "PrintScreen",
            [284] = "Pause",

            [330] = "NumpadDecimal",
            [331] = "NumpadDivide",
            [332] = "NumpadMultiply",
            [333] = "NumpadSubtract",
            [334] = "NumpadAdd",
            [335] = "NumpadEnter",
            [336] = "NumpadEqual",

            [340] = "ShiftLeft",
            [341] = "ControlLeft",
            [342] = "AltLeft",
            [343] = "MetaLeft",
            [344] = "ShiftRight",
            [345] = "ControlRight",
            [346] = "AltRight",
            [347] = "MetaRight",
            [348] = "ContextMenu",
        };
        for (int d = 0; d <= 9; d++)
        {
            m[48 + d] = "Digit" + d;     // GLFW_KEY_0..9
            m[320 + d] = "Numpad" + d;   // GLFW_KEY_KP_0..9
        }
        for (char c = 'A'; c <= 'Z'; c++)
            m[c] = "Key" + c;            // GLFW_KEY_A..Z are the ASCII codes
        for (int f = 1; f <= 24; f++)
            m[289 + f] = "F" + f;        // GLFW_KEY_F1 = 290 .. F24 = 313 (F25 has no W3C code)
        return m;
    }
}
