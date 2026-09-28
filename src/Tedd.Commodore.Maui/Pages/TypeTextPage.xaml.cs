namespace Tedd.Commodore.Maui.Pages;

/// <summary>Asks for a block of text to push through the KERNAL keyboard buffer.</summary>
public partial class TypeTextPage : ContentPage
{
    private readonly TaskCompletionSource<string?> _result = new();

    private TypeTextPage()
    {
        InitializeComponent();
        Input.Text = "LOAD\"*\",8,1\n";
    }

    /// <summary>Shows the dialog; the result is the text to type, or null when the user cancels.</summary>
    public static async Task<string?> ShowAsync(Page host)
    {
        var page = new TypeTextPage();
        await host.Navigation.PushModalAsync(page);
        return await page._result.Task;
    }

    private async void Ok_Clicked(object? sender, EventArgs e) => await CloseAsync((Input.Text ?? "").Replace("\r\n", "\n"));

    private async void Cancel_Clicked(object? sender, EventArgs e) => await CloseAsync(null);

    protected override bool OnBackButtonPressed()
    {
        _ = CloseAsync(null);
        return true;
    }

    private async Task CloseAsync(string? text)
    {
        if (_result.Task.IsCompleted) return;
        await Navigation.PopModalAsync();
        _result.TrySetResult(text);
    }
}
