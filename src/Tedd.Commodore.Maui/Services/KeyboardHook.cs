namespace Tedd.Commodore.Maui.Services;

/// <summary>One physical key press or release, named by its W3C <c>KeyboardEvent.code</c>.</summary>
public sealed class PhysicalKeyEvent
{
    public PhysicalKeyEvent(string code, bool isRepeat)
    {
        Code = code;
        IsRepeat = isRepeat;
    }

    /// <summary>"KeyA", "Digit1", "Numpad8", "ShiftLeft", ... - what <see cref="Hosting.KeyBindings"/> maps.</summary>
    public string Code { get; }

    /// <summary>True for the auto-repeats the OS generates while a key is held down.</summary>
    public bool IsRepeat { get; }

    /// <summary>Set to stop the key reaching the rest of the UI (menus, focus navigation, text boxes).</summary>
    public bool Handled { get; set; }
}

/// <summary>
/// Reads physical keys off a window before anything else in the UI sees them, which is what an emulator needs:
/// cursor keys, Tab and Escape must reach the C64 rather than move the focus or close a dialog.
///
/// MAUI has no cross-platform key input, so this is where a head for another platform plugs in; on a platform
/// without an implementation nothing is raised and the emulator is simply not keyboard driven.
/// </summary>
public sealed class KeyboardHook : IDisposable
{
#if WINDOWS
    private Microsoft.UI.Xaml.UIElement? _element;
#endif

    private KeyboardHook()
    {
    }

    public event Action<PhysicalKeyEvent>? KeyDown;
    public event Action<PhysicalKeyEvent>? KeyUp;

    /// <summary>
    /// True while an Alt key is held. Read from the platform rather than tracked from the events above,
    /// because the modifier keys themselves never arrive as key events - the window manager takes them.
    /// </summary>
    public bool AltDown =>
#if WINDOWS
        IsHeld(Windows.System.VirtualKey.Menu);
#else
        false;
#endif

    /// <summary>True while a Ctrl key is held; read from the platform, like <see cref="AltDown"/>.</summary>
    public bool ControlDown =>
#if WINDOWS
        IsHeld(Windows.System.VirtualKey.Control);
#else
        false;
#endif

    /// <summary>
    /// Starts listening on <paramref name="window"/>. The window must already have a handler (call this from
    /// <see cref="Page.Loaded"/> or later), otherwise nothing is hooked and null is returned.
    /// </summary>
    public static KeyboardHook? Attach(Window window)
    {
        var hook = new KeyboardHook();
#if WINDOWS
        if (window.Handler?.PlatformView is not Microsoft.UI.Xaml.Window platform || platform.Content is null)
            return null;
        hook._element = platform.Content;
        hook._element.PreviewKeyDown += hook.OnPreviewKeyDown;
        hook._element.PreviewKeyUp += hook.OnPreviewKeyUp;
        return hook;
#else
        _ = window;
        return hook;
#endif
    }

    public void Dispose()
    {
#if WINDOWS
        if (_element is not null)
        {
            _element.PreviewKeyDown -= OnPreviewKeyDown;
            _element.PreviewKeyUp -= OnPreviewKeyUp;
            _element = null;
        }
#endif
    }

#if WINDOWS
    private static bool IsHeld(Windows.System.VirtualKey key) =>
        (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key)
            & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;

    /// <summary>
    /// True while a menu, flyout or dropdown is open. These handlers run before the popup sees the key (that
    /// is the point of them), so without this check an open menu could not be walked with the arrow keys -
    /// they would go to the C64 instead.
    /// </summary>
    private bool PopupOpen()
    {
        if (_element is not Microsoft.UI.Xaml.FrameworkElement { XamlRoot: { } root }) return false;
        return Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(root).Count > 0;
    }

    private void OnPreviewKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (PopupOpen()) return;
        if (!WinUIKeyCodes.TryGetCode(e.OriginalKey, out var code)) return;
        var args = new PhysicalKeyEvent(code, e.KeyStatus.WasKeyDown);
        KeyDown?.Invoke(args);
        if (args.Handled) e.Handled = true;
    }

    private void OnPreviewKeyUp(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (PopupOpen()) return;
        if (!WinUIKeyCodes.TryGetCode(e.OriginalKey, out var code)) return;
        var args = new PhysicalKeyEvent(code, false);
        KeyUp?.Invoke(args);
        if (args.Handled) e.Handled = true;
    }
#endif
}
