using Windows.System;

namespace Tedd.Commodore.Maui.Services;

/// <summary>
/// Maps WinUI <see cref="VirtualKey"/> values to the W3C <c>KeyboardEvent.code</c> names used by
/// <see cref="Tedd.Commodore.Hosting.KeyBindings"/> ("KeyA", "Digit1", "Enter", "ShiftLeft", "Numpad8", "F1", ...).
///
/// Virtual keys are named after their position on a US layout, so the OEM punctuation keys are mapped by their
/// virtual key number (WinUI's enum has no names for them); keys the platform cannot tell apart (Enter and
/// numpad Enter) share one code. Keys without a W3C name fall back to the enum name so they stay bindable.
/// Use <see cref="Microsoft.UI.Xaml.Input.KeyRoutedEventArgs.OriginalKey"/>, not <c>Key</c>: only the former
/// distinguishes the left and right modifier keys and the numeric keypad.
/// </summary>
internal static class WinUIKeyCodes
{
    private static readonly Dictionary<VirtualKey, string> Map = BuildMap();

    private static Dictionary<VirtualKey, string> BuildMap()
    {
        var m = new Dictionary<VirtualKey, string>();
        for (var k = VirtualKey.A; k <= VirtualKey.Z; k++) m[k] = "Key" + (char)('A' + (k - VirtualKey.A));
        for (var k = VirtualKey.Number0; k <= VirtualKey.Number9; k++) m[k] = "Digit" + (k - VirtualKey.Number0);
        for (var k = VirtualKey.NumberPad0; k <= VirtualKey.NumberPad9; k++) m[k] = "Numpad" + (k - VirtualKey.NumberPad0);
        for (var k = VirtualKey.F1; k <= VirtualKey.F24; k++) m[k] = "F" + (1 + (k - VirtualKey.F1));

        m[VirtualKey.Escape] = "Escape";
        m[VirtualKey.Tab] = "Tab";
        m[VirtualKey.CapitalLock] = "CapsLock";
        m[VirtualKey.LeftShift] = "ShiftLeft";
        m[VirtualKey.RightShift] = "ShiftRight";
        m[VirtualKey.LeftControl] = "ControlLeft";
        m[VirtualKey.RightControl] = "ControlRight";
        m[VirtualKey.LeftMenu] = "AltLeft";
        m[VirtualKey.RightMenu] = "AltRight";
        m[VirtualKey.LeftWindows] = "MetaLeft";
        m[VirtualKey.RightWindows] = "MetaRight";
        m[VirtualKey.Application] = "ContextMenu";
        m[VirtualKey.Space] = "Space";
        m[VirtualKey.Enter] = "Enter";            // also numpad Enter (WinUI does not distinguish them)
        m[VirtualKey.Back] = "Backspace";
        m[VirtualKey.Insert] = "Insert";
        m[VirtualKey.Delete] = "Delete";
        m[VirtualKey.Home] = "Home";
        m[VirtualKey.End] = "End";
        m[VirtualKey.PageUp] = "PageUp";
        m[VirtualKey.PageDown] = "PageDown";
        m[VirtualKey.Up] = "ArrowUp";
        m[VirtualKey.Down] = "ArrowDown";
        m[VirtualKey.Left] = "ArrowLeft";
        m[VirtualKey.Right] = "ArrowRight";
        m[VirtualKey.Snapshot] = "PrintScreen";
        m[VirtualKey.Scroll] = "ScrollLock";
        m[VirtualKey.Pause] = "Pause";
        m[VirtualKey.NumberKeyLock] = "NumLock";
        m[VirtualKey.Clear] = "Numpad5";          // numpad 5 with Num Lock off
        m[VirtualKey.Multiply] = "NumpadMultiply";
        m[VirtualKey.Add] = "NumpadAdd";
        m[VirtualKey.Subtract] = "NumpadSubtract";
        m[VirtualKey.Decimal] = "NumpadDecimal";
        m[VirtualKey.Divide] = "NumpadDivide";
        m[VirtualKey.Separator] = "NumpadComma";

        // The OEM keys, by virtual key code: WinUI's enum stops naming keys here.
        m[(VirtualKey)186] = "Semicolon";        // VK_OEM_1
        m[(VirtualKey)187] = "Equal";            // VK_OEM_PLUS
        m[(VirtualKey)188] = "Comma";            // VK_OEM_COMMA
        m[(VirtualKey)189] = "Minus";            // VK_OEM_MINUS
        m[(VirtualKey)190] = "Period";           // VK_OEM_PERIOD
        m[(VirtualKey)191] = "Slash";            // VK_OEM_2
        m[(VirtualKey)192] = "Backquote";        // VK_OEM_3
        m[(VirtualKey)219] = "BracketLeft";      // VK_OEM_4
        m[(VirtualKey)220] = "Backslash";        // VK_OEM_5
        m[(VirtualKey)221] = "BracketRight";     // VK_OEM_6
        m[(VirtualKey)222] = "Quote";            // VK_OEM_7
        m[(VirtualKey)226] = "IntlBackslash";    // VK_OEM_102
        return m;
    }

    /// <summary>Translates a virtual key to its W3C code. False for placeholder and IME keys.</summary>
    public static bool TryGetCode(VirtualKey key, out string code)
    {
        if (Map.TryGetValue(key, out var mapped))
        {
            code = mapped;
            return true;
        }
        switch (key)
        {
            case VirtualKey.None:
            case VirtualKey.Menu:            // the unsided Alt/Ctrl/Shift: OriginalKey always gives the sided ones
            case VirtualKey.Control:
            case VirtualKey.Shift:
            case VirtualKey.Accept:
            case VirtualKey.Convert:
            case VirtualKey.NonConvert:
            case VirtualKey.ModeChange:
            case VirtualKey.Kana:            // == Hangul
            case VirtualKey.Kanji:           // == Hanja
            case VirtualKey.Junja:
            case VirtualKey.Final:
                code = "";
                return false;
            default:
                code = key.ToString();       // no W3C name, but still bindable
                return true;
        }
    }
}
