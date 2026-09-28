using Tedd.Commodore.Emulator.Media;

namespace Tedd.Commodore.Maui.Pages;

/// <summary>Picks one of the files inside a multi-entry T64 tape image.</summary>
public partial class SelectEntryPage : ContentPage
{
    private readonly TaskCompletionSource<int?> _result = new();
    private readonly List<EntryRow> _rows = new();

    private SelectEntryPage(T64Image image)
    {
        InitializeComponent();
        foreach (var e in image.Entries)
            _rows.Add(new EntryRow(e));
        List.ItemsSource = _rows;
        List.SelectedItem = _rows.Count > 0 ? _rows[0] : null;
    }

    /// <summary>Shows the dialog; the result is the entry index, or null when the user cancels.</summary>
    public static async Task<int?> ShowAsync(Page host, T64Image image)
    {
        var page = new SelectEntryPage(image);
        await host.Navigation.PushModalAsync(page);
        return await page._result.Task;
    }

    /// <summary>One directory entry of the tape image.</summary>
    public sealed class EntryRow
    {
        public EntryRow(T64Entry entry)
        {
            Name = entry.Name;
            Type = entry.FileType == 0x82 ? "PRG" : $"${entry.FileType:X2}";
            Start = $"${entry.StartAddress:X4}";
            End = $"${entry.EndAddress:X4}";
        }

        public string Name { get; }
        public string Type { get; }
        public string Start { get; }
        public string End { get; }
    }

    private void List_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        // Keep a selection: tapping the selected row again would otherwise clear it.
        if (e.CurrentSelection.Count == 0 && e.PreviousSelection.Count > 0)
            List.SelectedItem = e.PreviousSelection[0];
    }

    private async void Ok_Clicked(object? sender, EventArgs e)
    {
        int index = List.SelectedItem is EntryRow row ? _rows.IndexOf(row) : -1;
        if (index < 0) return;
        await CloseAsync(index);
    }

    private async void Cancel_Clicked(object? sender, EventArgs e) => await CloseAsync(null);

    protected override bool OnBackButtonPressed()
    {
        _ = CloseAsync(null);
        return true;
    }

    private async Task CloseAsync(int? index)
    {
        if (_result.Task.IsCompleted) return;
        await Navigation.PopModalAsync();
        _result.TrySetResult(index);
    }
}
