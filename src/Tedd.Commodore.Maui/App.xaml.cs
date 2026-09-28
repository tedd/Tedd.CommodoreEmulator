namespace Tedd.Commodore.Maui;

/// <summary>
/// The application. Everything else hangs off <see cref="Pages.MainPage"/>, which owns the emulator session;
/// the tool windows are separate MAUI windows opened from its menu.
/// </summary>
public partial class App : Application
{
    public App()
    {
        InitializeComponent();
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        // A desktop menu bar is only shown for a page hosted in a NavigationPage (or a Shell), so the emulator
        // page is wrapped in one. Its navigation bar has to stay enabled - that is the strip the menu is drawn
        // in - but the page has no Title, so all it shows is the menu.
        var page = new Pages.MainPage();
        return new Window(new NavigationPage(page))
        {
            Title = "Tedd.Commodore - Commodore 64",
            Width = 900,
            Height = 700,
        };
    }
}
