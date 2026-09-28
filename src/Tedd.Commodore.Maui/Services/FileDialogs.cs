using Microsoft.Maui.Storage;
#if WINDOWS
using Windows.Storage.Pickers;
#endif

namespace Tedd.Commodore.Maui.Services;

/// <summary>
/// Open and save dialogs. Opening goes through MAUI's own <see cref="FilePicker"/>; saving has no MAUI
/// equivalent, so it uses the platform picker (a head for another platform adds its branch below).
/// </summary>
public static class FileDialogs
{
    /// <summary>
    /// Asks for an existing file. <paramref name="extensions"/> are lower case and include the dot
    /// (".d64"); an empty list accepts anything. Returns null when the user cancels.
    /// </summary>
    public static async Task<string?> OpenAsync(string title, params string[] extensions)
    {
        var options = new PickOptions { PickerTitle = title };
        if (extensions.Length > 0)
        {
            options.FileTypes = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
            {
                [DevicePlatform.WinUI] = extensions,
                [DevicePlatform.macOS] = extensions.Select(e => e.TrimStart('.')).ToArray(),
                [DevicePlatform.MacCatalyst] = extensions.Select(e => e.TrimStart('.')).ToArray(),
            });
        }
        var result = await FilePicker.Default.PickAsync(options);
        return result?.FullPath;
    }

    /// <summary>
    /// Asks where to write a new file. Returns the full path, or null when the user cancels or the platform
    /// has no save dialog.
    /// </summary>
    public static async Task<string?> SaveAsync(string typeName, string extension, string suggestedName)
    {
#if WINDOWS
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = suggestedName,
        };
        picker.FileTypeChoices.Add(typeName, new List<string> { extension });
        // WinUI pickers must be told which window they belong to before they can be shown.
        var window = Microsoft.Maui.ApplicationModel.WindowStateManager.Default.GetActiveWindow();
        if (window is not null)
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
        var file = await picker.PickSaveFileAsync();
        return file?.Path;
#else
        await Task.CompletedTask;
        return null;
#endif
    }
}
