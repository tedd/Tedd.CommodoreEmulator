using System.Runtime.Versioning;
using Tedd.Commodore.Hosting;

namespace Tedd.Commodore.Web.Interop;

/// <summary>
/// Connects the browser keyboard to an <see cref="EmulatorSession"/>: <c>keydown</c>/<c>keyup</c> on the
/// document are forwarded as W3C <c>KeyboardEvent.code</c> names (which is exactly what
/// <see cref="EmulatorSession.KeyDown"/> expects) while the focus element has focus; bound keys are
/// swallowed (<c>preventDefault</c>), everything is released when focus is lost.
/// Touch joystick / on-screen keys go straight to <see cref="EmulatorSession.SetJoystick"/> and
/// <see cref="EmulatorSession.SetKey"/> from the UI and need nothing here.
/// </summary>
[SupportedOSPlatform("browser")]
public sealed class BrowserInput
{
    private readonly Func<string, bool, bool> _onKey;
    private readonly Action _onBlur;
    private bool _attached;

    public BrowserInput()
    {
        // keep the delegates alive for the lifetime of the JS listeners
        _onKey = OnKey;
        _onBlur = OnBlur;
    }

    /// <summary>The session receiving the input (may be swapped at any time, e.g. after a ROM change).</summary>
    public EmulatorSession? Session { get; set; }

    /// <summary>Raised for every key event that reached the emulator (for on-screen feedback).</summary>
    public event Action<string, bool>? KeyRouted;

    public void Attach(string focusElementId)
    {
        C64Js.AttachInput(focusElementId, _onKey, _onBlur);
        _attached = true;
    }

    public void Detach()
    {
        if (!_attached) return;
        C64Js.DetachInput();
        _attached = false;
        Session?.ReleaseAllInput();
    }

    private bool OnKey(string code, bool down)
    {
        var s = Session;
        if (s is null || !s.Bindings.TryGet(code, out _)) return false;
        if (down) s.KeyDown(code); else s.KeyUp(code);
        KeyRouted?.Invoke(code, down);
        return true;
    }

    private void OnBlur() => Session?.ReleaseAllInput();
}
