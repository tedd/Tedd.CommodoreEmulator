using System;
using Tedd.Commodore.Emulator.C64;

namespace Tedd.Commodore.Hosting;

/// <summary>Joystick inputs (one bit each; diagonals are two actions).</summary>
public enum JoystickInput { Up, Down, Left, Right, Fire }

/// <summary>
/// Host-level commands that can be bound to keys. <see cref="ToggleColumns"/> (the C128's 40/80 DISPLAY key) and
/// <see cref="CapsLock"/> are applied by the session itself and then reported to the host.
/// </summary>
public enum SystemCommand { Restore, Reset, HardReset, Pause, Warp, Screenshot, MemoryViewer, SpriteViewer, ToggleColumns, CapsLock }

public enum InputActionKind { Key, Joystick, System }

/// <summary>
/// Something a physical key can be bound to: a C64 key (optionally with SHIFT), a joystick input on port 1 or
/// 2, or a host command. Serialised as a compact string ("key:A", "key:D1+shift", "joy2:fire", "sys:restore")
/// so bindings survive as plain JSON dictionaries.
/// </summary>
public readonly struct InputAction : IEquatable<InputAction>
{
    public InputActionKind Kind { get; }
    public C64Key Key { get; }
    public bool Shift { get; }
    public int JoystickPort { get; }
    public JoystickInput Joystick { get; }
    public SystemCommand Command { get; }

    private InputAction(InputActionKind kind, C64Key key, bool shift, int port, JoystickInput joy, SystemCommand cmd)
    {
        Kind = kind;
        Key = key;
        Shift = shift;
        JoystickPort = port;
        Joystick = joy;
        Command = cmd;
    }

    public static InputAction ForKey(C64Key key, bool shift = false) => new(InputActionKind.Key, key, shift, 0, default, default);
    public static InputAction ForJoystick(int port, JoystickInput input)
    {
        if (port is not (1 or 2)) throw new ArgumentOutOfRangeException(nameof(port));
        return new(InputActionKind.Joystick, default, false, port, input, default);
    }
    public static InputAction ForSystem(SystemCommand command) => new(InputActionKind.System, default, false, 0, default, command);

    /// <summary>Compact serialised form.</summary>
    public override string ToString() => Kind switch
    {
        InputActionKind.Key => "key:" + Key + (Shift ? "+shift" : ""),
        InputActionKind.Joystick => "joy" + JoystickPort + ":" + Joystick.ToString().ToLowerInvariant(),
        _ => "sys:" + Command.ToString().ToLowerInvariant(),
    };

    /// <summary>Human readable description for editors.</summary>
    public string Describe() => Kind switch
    {
        InputActionKind.Key => (Shift ? "SHIFT + " : "") + DescribeKey(Key),
        InputActionKind.Joystick => $"Joystick {JoystickPort} {Joystick}",
        _ => Command switch
        {
            SystemCommand.Restore => "RESTORE (NMI)",
            SystemCommand.HardReset => "Hard reset",
            SystemCommand.ToggleColumns => "40/80 DISPLAY key (C128)",
            SystemCommand.CapsLock => "CAPS LOCK key (C128)",
            _ => Command.ToString(),
        },
    };

    public static string DescribeKey(C64Key key) => key switch
    {
        C64Key.D0 => "0", C64Key.D1 => "1", C64Key.D2 => "2", C64Key.D3 => "3", C64Key.D4 => "4",
        C64Key.D5 => "5", C64Key.D6 => "6", C64Key.D7 => "7", C64Key.D8 => "8", C64Key.D9 => "9",
        C64Key.Delete => "INST/DEL", C64Key.Return => "RETURN", C64Key.CursorRight => "CRSR right", C64Key.CursorDown => "CRSR down",
        C64Key.LeftShift => "left SHIFT", C64Key.RightShift => "right SHIFT", C64Key.Plus => "+", C64Key.Minus => "-",
        C64Key.Period => ".", C64Key.Colon => ":", C64Key.At => "@", C64Key.Comma => ",", C64Key.Pound => "£",
        C64Key.Asterisk => "*", C64Key.Semicolon => ";", C64Key.Home => "CLR/HOME", C64Key.Equals => "=",
        C64Key.ArrowUp => "↑", C64Key.Slash => "/", C64Key.ArrowLeft => "←", C64Key.Control => "CTRL",
        C64Key.Space => "SPACE", C64Key.Commodore => "C=", C64Key.RunStop => "RUN/STOP",
        C64Key.Help => "HELP (C128)", C64Key.Tab => "TAB (C128)", C64Key.Escape => "ESC (C128)", C64Key.Alt => "ALT (C128)",
        C64Key.LineFeed => "LINE FEED (C128)", C64Key.NoScroll => "NO SCROLL (C128)",
        C64Key.Up => "cursor up (C128)", C64Key.Down => "cursor down (C128)", C64Key.Left => "cursor left (C128)", C64Key.Right => "cursor right (C128)",
        C64Key.KeypadEnter => "keypad ENTER (C128)", C64Key.KeypadPlus => "keypad + (C128)", C64Key.KeypadMinus => "keypad - (C128)", C64Key.KeypadPeriod => "keypad . (C128)",
        C64Key.Keypad0 => "keypad 0 (C128)", C64Key.Keypad1 => "keypad 1 (C128)", C64Key.Keypad2 => "keypad 2 (C128)", C64Key.Keypad3 => "keypad 3 (C128)",
        C64Key.Keypad4 => "keypad 4 (C128)", C64Key.Keypad5 => "keypad 5 (C128)", C64Key.Keypad6 => "keypad 6 (C128)", C64Key.Keypad7 => "keypad 7 (C128)",
        C64Key.Keypad8 => "keypad 8 (C128)", C64Key.Keypad9 => "keypad 9 (C128)",
        _ => key.ToString(),
    };

    public static bool TryParse(string? text, out InputAction action)
    {
        action = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim();
        int colon = text.IndexOf(':');
        if (colon < 0) return false;
        string head = text[..colon].ToLowerInvariant();
        string tail = text[(colon + 1)..];
        switch (head)
        {
            case "key":
            {
                bool shift = false;
                int plus = tail.IndexOf('+');
                if (plus >= 0)
                {
                    shift = tail[(plus + 1)..].Trim().Equals("shift", StringComparison.OrdinalIgnoreCase);
                    tail = tail[..plus];
                }
                if (!Enum.TryParse<C64Key>(tail.Trim(), ignoreCase: true, out var key)) return false;
                action = ForKey(key, shift);
                return true;
            }
            case "joy1":
            case "joy2":
            {
                if (!Enum.TryParse<JoystickInput>(tail.Trim(), ignoreCase: true, out var joy)) return false;
                action = ForJoystick(head[3] - '0', joy);
                return true;
            }
            case "sys":
            {
                if (!Enum.TryParse<SystemCommand>(tail.Trim(), ignoreCase: true, out var cmd)) return false;
                action = ForSystem(cmd);
                return true;
            }
            default:
                return false;
        }
    }

    public static InputAction Parse(string text) =>
        TryParse(text, out var a) ? a : throw new FormatException($"Invalid input action '{text}'");

    public bool Equals(InputAction other) =>
        Kind == other.Kind && Key == other.Key && Shift == other.Shift && JoystickPort == other.JoystickPort &&
        Joystick == other.Joystick && Command == other.Command;

    public override bool Equals(object? obj) => obj is InputAction a && Equals(a);
    public override int GetHashCode() => HashCode.Combine(Kind, Key, Shift, JoystickPort, Joystick, Command);
    public static bool operator ==(InputAction a, InputAction b) => a.Equals(b);
    public static bool operator !=(InputAction a, InputAction b) => !a.Equals(b);
}
