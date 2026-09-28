namespace Tedd.Commodore.Maui.Services;

/// <summary>
/// One at-most-once tool window (memory viewer, sprite viewer, ...). Showing it again brings the existing
/// window forward instead of opening a second one, which is what the WPF front-end's <c>Show</c> +
/// <c>Activate</c> pair did.
/// </summary>
public sealed class ToolWindow<TPage> where TPage : Page
{
    private Window? _window;

    /// <summary>The open page, or null while the window is closed.</summary>
    public TPage? Page { get; private set; }

    public bool IsOpen => _window is not null;

    /// <summary>Opens the window (creating the page with <paramref name="create"/>) or brings it to the front.</summary>
    public void Show(Func<TPage> create, string title, int width, int height)
    {
        var app = Application.Current;
        if (app is null) return;
        if (_window is not null)
        {
            app.ActivateWindow(_window);
            return;
        }

        var page = create();
        var window = new Window(page) { Title = title, Width = width, Height = height };
        window.Destroying += (_, _) =>
        {
            _window = null;
            Page = null;
        };
        Page = page;
        _window = window;
        app.OpenWindow(window);
    }

    /// <summary>Closes the window if it is open.</summary>
    public void Close()
    {
        if (_window is null) return;
        Application.Current?.CloseWindow(_window);
        _window = null;
        Page = null;
    }
}
