using System;
using System.Collections.Generic;
using System.IO;
using Tedd.Commodore.Emulator.C64;
using Tedd.Commodore.Emulator.Machines;

namespace Tedd.Commodore.Hosting;

/// <summary>
/// Maps physical keys to <see cref="InputAction"/>s. Physical keys are identified by the W3C
/// <c>KeyboardEvent.code</c> names ("KeyA", "Digit1", "Enter", "Numpad8", "ArrowLeft", ...), which are layout
/// independent and can be produced by every host (browsers natively, WPF/SDL/Unity through a small table).
/// Stored as a JSON object { "code": "action", ... } (see <see cref="InputAction"/> for the action syntax).
/// </summary>
public sealed class KeyBindings
{
    private readonly Dictionary<string, InputAction> _map = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised after any change.</summary>
    public event Action? Changed;

    public IReadOnlyDictionary<string, InputAction> All => _map;

    public int Count => _map.Count;

    public bool TryGet(string code, out InputAction action) => _map.TryGetValue(code, out action);

    public InputAction? Get(string code) => _map.TryGetValue(code, out var a) ? a : null;

    public void Set(string code, InputAction action)
    {
        _map[code] = action;
        Changed?.Invoke();
    }

    public bool Remove(string code)
    {
        bool removed = _map.Remove(code);
        if (removed) Changed?.Invoke();
        return removed;
    }

    public void Clear()
    {
        _map.Clear();
        Changed?.Invoke();
    }

    /// <summary>All codes currently bound to the given action.</summary>
    public IEnumerable<string> CodesFor(InputAction action)
    {
        foreach (var (code, a) in _map)
            if (a == action) yield return code;
    }

    /// <summary>Replaces the whole table with another one (used by editors for "apply"/"reset to default").</summary>
    public void CopyFrom(KeyBindings other)
    {
        _map.Clear();
        foreach (var (k, v) in other._map) _map[k] = v;
        Changed?.Invoke();
    }

    public KeyBindings Clone()
    {
        var c = new KeyBindings();
        foreach (var (k, v) in _map) c._map[k] = v;
        return c;
    }

    #region Serialisation

    public string ToJson()
    {
        var dict = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in _map) dict[k] = v.ToString();
        return FlatJson.Write(dict);
    }

    public static KeyBindings FromJson(string json)
    {
        var b = new KeyBindings();
        foreach (var (k, v) in FlatJson.Read(json))
            if (InputAction.TryParse(v, out var action))
                b._map[k] = action;
        return b;
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, ToJson());
    }

    public static KeyBindings Load(string path) => FromJson(File.ReadAllText(path));

    /// <summary>Loads the file if it exists, otherwise returns the defaults.</summary>
    public static KeyBindings LoadOrDefault(string path)
    {
        try
        {
            if (File.Exists(path)) return Load(path);
        }
        catch (Exception)
        {
            // fall through to defaults
        }
        return CreateDefault();
    }

    #endregion

    /// <summary>
    /// The default positional-ish layout: letters, digits and punctuation where the PC keyboard has them,
    /// Escape = RUN/STOP, Tab = C=, Backspace = INST/DEL, Home/End = CLR/HOME, cursor keys, F1-F8,
    /// numeric keypad = joystick 2 (8/2/4/6 + 0/5 fire, 7/9/1/3 diagonals are two actions in the host),
    /// Page Up = RESTORE, F11 = reset, F12 = screenshot, Pause = freeze, Alt+W warp (host shortcuts).
    /// The C128's extra keys are bound where the PC keyboard has room (Numpad 1/3/7/9, Page Down = HELP,
    /// Scroll Lock = NO SCROLL, Left Alt = ALT, Caps Lock = CAPS LOCK); with <paramref name="model"/> =
    /// <see cref="MachineModel.C128"/> the layout follows the C128 keyboard more closely: Escape = ESC,
    /// Tab = TAB, End = RUN/STOP, Right Ctrl = C=, the whole numeric keypad is the keypad and F9 toggles 40/80.
    /// </summary>
    public static KeyBindings CreateDefault(MachineModel model = MachineModel.C64)
    {
        var b = new KeyBindings();
        void K(string code, C64Key key, bool shift = false) => b._map[code] = InputAction.ForKey(key, shift);
        void J(string code, int port, JoystickInput input) => b._map[code] = InputAction.ForJoystick(port, input);
        void S(string code, SystemCommand cmd) => b._map[code] = InputAction.ForSystem(cmd);

        for (char c = 'A'; c <= 'Z'; c++)
            K("Key" + c, Enum.Parse<C64Key>(c.ToString()));
        K("Digit0", C64Key.D0); K("Digit1", C64Key.D1); K("Digit2", C64Key.D2); K("Digit3", C64Key.D3); K("Digit4", C64Key.D4);
        K("Digit5", C64Key.D5); K("Digit6", C64Key.D6); K("Digit7", C64Key.D7); K("Digit8", C64Key.D8); K("Digit9", C64Key.D9);
        K("Space", C64Key.Space); K("Enter", C64Key.Return); K("NumpadEnter", C64Key.Return);
        K("Backspace", C64Key.Delete); K("Delete", C64Key.Delete); K("Insert", C64Key.Delete, true);
        K("Escape", C64Key.RunStop); K("Tab", C64Key.Commodore);
        K("ControlLeft", C64Key.Control); K("ControlRight", C64Key.Control);
        K("ShiftLeft", C64Key.LeftShift); K("ShiftRight", C64Key.RightShift);
        K("Home", C64Key.Home); K("End", C64Key.Home, true);
        K("ArrowDown", C64Key.CursorDown); K("ArrowUp", C64Key.CursorDown, true);
        K("ArrowRight", C64Key.CursorRight); K("ArrowLeft", C64Key.CursorRight, true);
        K("F1", C64Key.F1); K("F2", C64Key.F1, true); K("F3", C64Key.F3); K("F4", C64Key.F3, true);
        K("F5", C64Key.F5); K("F6", C64Key.F5, true); K("F7", C64Key.F7); K("F8", C64Key.F7, true);
        K("Equal", C64Key.Plus); K("Minus", C64Key.Minus); K("NumpadAdd", C64Key.Plus); K("NumpadSubtract", C64Key.Minus);
        K("Comma", C64Key.Comma); K("Period", C64Key.Period); K("Slash", C64Key.Slash); K("NumpadDivide", C64Key.Slash);
        K("Semicolon", C64Key.Semicolon); K("Quote", C64Key.Colon); K("BracketLeft", C64Key.At); K("BracketRight", C64Key.Asterisk);
        K("NumpadMultiply", C64Key.Asterisk); K("Backquote", C64Key.ArrowLeft); K("Backslash", C64Key.Pound);
        K("IntlBackslash", C64Key.Pound); K("NumpadDecimal", C64Key.Period);
        J("Numpad8", 2, JoystickInput.Up); J("Numpad2", 2, JoystickInput.Down); J("Numpad4", 2, JoystickInput.Left);
        J("Numpad6", 2, JoystickInput.Right); J("Numpad0", 2, JoystickInput.Fire); J("Numpad5", 2, JoystickInput.Fire);
        J("AltRight", 2, JoystickInput.Fire);
        S("PageUp", SystemCommand.Restore); S("F11", SystemCommand.Reset); S("F12", SystemCommand.Screenshot); S("Pause", SystemCommand.Pause);
        // C128 keys that have a free PC key (they do nothing on a C64).
        K("Numpad1", C64Key.Keypad1); K("Numpad3", C64Key.Keypad3); K("Numpad7", C64Key.Keypad7); K("Numpad9", C64Key.Keypad9);
        K("PageDown", C64Key.Help); K("ScrollLock", C64Key.NoScroll); K("AltLeft", C64Key.Alt);
        S("CapsLock", SystemCommand.CapsLock);
        if (model == MachineModel.C128)
        {
            K("Escape", C64Key.Escape); K("Tab", C64Key.Tab); K("End", C64Key.RunStop); K("ControlRight", C64Key.Commodore);
            K("Numpad8", C64Key.Keypad8); K("Numpad2", C64Key.Keypad2); K("Numpad4", C64Key.Keypad4); K("Numpad6", C64Key.Keypad6);
            K("Numpad0", C64Key.Keypad0); K("Numpad5", C64Key.Keypad5); K("NumpadEnter", C64Key.KeypadEnter);
            K("NumpadAdd", C64Key.KeypadPlus); K("NumpadSubtract", C64Key.KeypadMinus); K("NumpadDecimal", C64Key.KeypadPeriod);
            K("ArrowUp", C64Key.Up); K("ArrowDown", C64Key.Down); K("ArrowLeft", C64Key.Left); K("ArrowRight", C64Key.Right);
            S("F9", SystemCommand.ToggleColumns);
        }
        return b;
    }
}

/// <summary>The W3C KeyboardEvent.code names understood by the hosts, for editors and mapping tables.</summary>
public static class KeyCodes
{
    public static readonly string[] All =
    {
        "Escape", "F1", "F2", "F3", "F4", "F5", "F6", "F7", "F8", "F9", "F10", "F11", "F12", "Pause", "ScrollLock", "PrintScreen",
        "Backquote", "Digit1", "Digit2", "Digit3", "Digit4", "Digit5", "Digit6", "Digit7", "Digit8", "Digit9", "Digit0", "Minus", "Equal", "Backspace",
        "Tab", "KeyQ", "KeyW", "KeyE", "KeyR", "KeyT", "KeyY", "KeyU", "KeyI", "KeyO", "KeyP", "BracketLeft", "BracketRight", "Backslash",
        "CapsLock", "KeyA", "KeyS", "KeyD", "KeyF", "KeyG", "KeyH", "KeyJ", "KeyK", "KeyL", "Semicolon", "Quote", "Enter",
        "ShiftLeft", "IntlBackslash", "KeyZ", "KeyX", "KeyC", "KeyV", "KeyB", "KeyN", "KeyM", "Comma", "Period", "Slash", "ShiftRight",
        "ControlLeft", "MetaLeft", "AltLeft", "Space", "AltRight", "MetaRight", "ContextMenu", "ControlRight",
        "Insert", "Home", "PageUp", "Delete", "End", "PageDown", "ArrowUp", "ArrowLeft", "ArrowDown", "ArrowRight",
        "NumLock", "NumpadDivide", "NumpadMultiply", "NumpadSubtract", "Numpad7", "Numpad8", "Numpad9", "NumpadAdd",
        "Numpad4", "Numpad5", "Numpad6", "Numpad1", "Numpad2", "Numpad3", "NumpadEnter", "Numpad0", "NumpadDecimal",
    };

    /// <summary>Friendly display name for a code ("KeyA" -> "A", "Digit1" -> "1", "Numpad8" -> "Numpad 8").</summary>
    public static string Display(string code)
    {
        if (code.StartsWith("Key", StringComparison.Ordinal) && code.Length == 4) return code[3..];
        if (code.StartsWith("Digit", StringComparison.Ordinal) && code.Length == 6) return code[5..];
        if (code.StartsWith("Numpad", StringComparison.Ordinal)) return "Numpad " + code[6..];
        if (code.StartsWith("Arrow", StringComparison.Ordinal)) return "Arrow " + code[5..];
        return code switch
        {
            "Backquote" => "`", "Minus" => "-", "Equal" => "=", "BracketLeft" => "[", "BracketRight" => "]", "Backslash" => "\\",
            "Semicolon" => ";", "Quote" => "'", "Comma" => ",", "Period" => ".", "Slash" => "/", "IntlBackslash" => "< (intl)",
            "ShiftLeft" => "Left Shift", "ShiftRight" => "Right Shift", "ControlLeft" => "Left Ctrl", "ControlRight" => "Right Ctrl",
            "AltLeft" => "Left Alt", "AltRight" => "Right Alt", "MetaLeft" => "Left Win", "MetaRight" => "Right Win",
            _ => code,
        };
    }
}
