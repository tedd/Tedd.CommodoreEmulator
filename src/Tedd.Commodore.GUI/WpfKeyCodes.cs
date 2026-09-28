using System.Collections.Generic;
using System.Windows.Input;

namespace Tedd.Commodore.GUI;

/// <summary>
/// Maps WPF <see cref="Key"/> values to the W3C <c>KeyboardEvent.code</c> names used by
/// <see cref="Tedd.Commodore.Hosting.KeyBindings"/> ("KeyA", "Digit1", "Enter", "ShiftLeft", "Numpad8", "F1", ...).
/// WPF keys are virtual keys, so the OEM punctuation keys are named after their US layout position; keys WPF
/// cannot tell apart (Enter / numpad Enter) share one code. Keys without a W3C name fall back to the WPF enum
/// name so they can still be bound.
/// </summary>
public static class WpfKeyCodes
{
    private static readonly Dictionary<Key, string> Map = BuildMap();

    private static Dictionary<Key, string> BuildMap()
    {
        var m = new Dictionary<Key, string>();
        for (var k = Key.A; k <= Key.Z; k++) m[k] = "Key" + (char)('A' + (k - Key.A));
        for (var k = Key.D0; k <= Key.D9; k++) m[k] = "Digit" + (k - Key.D0);
        for (var k = Key.NumPad0; k <= Key.NumPad9; k++) m[k] = "Numpad" + (k - Key.NumPad0);
        for (var k = Key.F1; k <= Key.F24; k++) m[k] = "F" + (1 + (k - Key.F1));

        m[Key.Escape] = "Escape";
        m[Key.Tab] = "Tab";
        m[Key.CapsLock] = "CapsLock";
        m[Key.LeftShift] = "ShiftLeft";
        m[Key.RightShift] = "ShiftRight";
        m[Key.LeftCtrl] = "ControlLeft";
        m[Key.RightCtrl] = "ControlRight";
        m[Key.LeftAlt] = "AltLeft";
        m[Key.RightAlt] = "AltRight";
        m[Key.LWin] = "MetaLeft";
        m[Key.RWin] = "MetaRight";
        m[Key.Apps] = "ContextMenu";
        m[Key.Space] = "Space";
        m[Key.Enter] = "Enter";            // also numpad Enter (WPF does not distinguish them)
        m[Key.Back] = "Backspace";
        m[Key.Insert] = "Insert";
        m[Key.Delete] = "Delete";
        m[Key.Home] = "Home";
        m[Key.End] = "End";
        m[Key.PageUp] = "PageUp";
        m[Key.PageDown] = "PageDown";
        m[Key.Up] = "ArrowUp";
        m[Key.Down] = "ArrowDown";
        m[Key.Left] = "ArrowLeft";
        m[Key.Right] = "ArrowRight";
        m[Key.PrintScreen] = "PrintScreen";
        m[Key.Scroll] = "ScrollLock";
        m[Key.Pause] = "Pause";
        m[Key.NumLock] = "NumLock";
        m[Key.Clear] = "Numpad5";          // numpad 5 with Num Lock off
        m[Key.Multiply] = "NumpadMultiply";
        m[Key.Add] = "NumpadAdd";
        m[Key.Subtract] = "NumpadSubtract";
        m[Key.Decimal] = "NumpadDecimal";
        m[Key.Divide] = "NumpadDivide";
        m[Key.Separator] = "NumpadComma";
        m[Key.OemTilde] = "Backquote";
        m[Key.OemMinus] = "Minus";
        m[Key.OemPlus] = "Equal";
        m[Key.OemOpenBrackets] = "BracketLeft";
        m[Key.OemCloseBrackets] = "BracketRight";
        m[Key.OemPipe] = "Backslash";
        m[Key.OemSemicolon] = "Semicolon";
        m[Key.OemQuotes] = "Quote";
        m[Key.OemComma] = "Comma";
        m[Key.OemPeriod] = "Period";
        m[Key.OemQuestion] = "Slash";
        m[Key.OemBackslash] = "IntlBackslash";
        return m;
    }

    /// <summary>All explicitly mapped keys (WPF key, W3C code).</summary>
    public static IReadOnlyDictionary<Key, string> All => Map;

    /// <summary>
    /// The physical key of a key event: <see cref="KeyEventArgs.SystemKey"/> for Alt combinations (where
    /// <see cref="KeyEventArgs.Key"/> is <see cref="Key.System"/>) and the IME/dead-key processed keys.
    /// </summary>
    public static Key ResolveKey(KeyEventArgs e) => e.Key switch
    {
        Key.System => e.SystemKey,
        Key.ImeProcessed => e.ImeProcessedKey,
        Key.DeadCharProcessed => e.DeadCharProcessedKey,
        _ => e.Key,
    };

    /// <summary>Translates a WPF key to its W3C code. Returns false for placeholder keys (None, System, IME...).</summary>
    public static bool TryGetCode(Key key, out string code)
    {
        if (Map.TryGetValue(key, out var mapped))
        {
            code = mapped;
            return true;
        }
        switch (key)
        {
            case Key.None:
            case Key.System:
            case Key.ImeProcessed:
            case Key.DeadCharProcessed:
            case Key.ImeAccept:
            case Key.ImeConvert:
            case Key.ImeModeChange:
            case Key.ImeNonConvert:
            case Key.KanaMode:      // == HangulMode
            case Key.KanjiMode:     // == HanjaMode
            case Key.JunjaMode:
            case Key.FinalMode:
                code = "";
                return false;
            default:
                code = key.ToString();   // e.g. "Oem8": no W3C name, but still bindable
                return true;
        }
    }

    /// <summary>Translates the physical key of a key event to its W3C code.</summary>
    public static bool TryGetCode(KeyEventArgs e, out string code) => TryGetCode(ResolveKey(e), out code);
}
