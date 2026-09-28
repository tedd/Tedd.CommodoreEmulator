namespace Tedd.Commodore.Embedding;

/// <summary>
/// Translates Avalonia's <c>Avalonia.Input.PhysicalKey</c> names (<c>KeyEventArgs.PhysicalKey.ToString()</c>) to the
/// W3C <c>KeyboardEvent.code</c> names the key bindings use. Avalonia named the enum after the W3C codes, so apart
/// from the letters ("A" is "KeyA") and the spelling "NumPad" for "Numpad" they are the same strings; this assembly
/// has no Avalonia reference, so the enum is handled by name:
/// <c>emulator.KeyDown(AvaloniaKeyCodes.ToWebCode(e.PhysicalKey.ToString()))</c>.
/// </summary>
public static class AvaloniaKeyCodes
{
    /// <summary>The W3C code for a <c>PhysicalKey</c> name, or null for <c>None</c> and empty input. W3C codes pass through unchanged.</summary>
    public static string? ToWebCode(string? physicalKeyName)
    {
        if (string.IsNullOrEmpty(physicalKeyName) || physicalKeyName == "None")
            return null;
        char c = physicalKeyName![0];
        if (physicalKeyName.Length == 1 && c is >= 'A' and <= 'Z')
            return "Key" + physicalKeyName;
        if (physicalKeyName.StartsWith("NumPad", System.StringComparison.Ordinal))
            return "Numpad" + physicalKeyName.Substring("NumPad".Length);
        return physicalKeyName;
    }
}
