using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;

namespace Tedd.Commodore.Web.Interop;

/// <summary>
/// Raw bindings to <c>wwwroot/js/c64.js</c> (module name "c64"). Call <see cref="ImportAsync"/> once before
/// anything else. The higher level classes in this namespace (<see cref="BrowserVideoSink"/>,
/// <see cref="BrowserAudioSink"/>, <see cref="BrowserInput"/>, <see cref="BrowserLoop"/>) are what a host
/// normally uses; these bindings are public so other web hosts can build their own combination.
/// </summary>
[SupportedOSPlatform("browser")]
public static partial class C64Js
{
    public const string ModuleName = "c64";

    private static Task? _import;

    /// <summary>Imports the module. <paramref name="moduleUrl"/> should be absolute (e.g. built from the app's base URI) because relative specifiers resolve against the .NET runtime script, not the page.</summary>
    public static Task ImportAsync(string moduleUrl) => _import ??= JSHost.ImportAsync(ModuleName, moduleUrl);

    // ---- video
    [JSImport("attachCanvas", ModuleName)]
    public static partial void AttachCanvas(string canvasId, int width, int height);

    /// <summary>Draws one RGBA frame (width * height * 4 bytes) on the attached canvas.</summary>
    [JSImport("presentFrame", ModuleName)]
    public static partial void PresentFrame([JSMarshalAs<JSType.MemoryView>] Span<byte> rgba);

    // ---- audio
    /// <summary>Creates/resumes the AudioContext and worklet (call from a user gesture). Returns the context sample rate.</summary>
    [JSImport("audioStart", ModuleName)]
    [return: JSMarshalAs<JSType.Promise<JSType.Number>>]
    public static partial Task<int> AudioStart();

    [JSImport("audioState", ModuleName)]
    public static partial string AudioState();

    /// <summary>Queues little-endian 16-bit mono PCM at the context sample rate.</summary>
    [JSImport("audioWrite", ModuleName)]
    public static partial void AudioWrite([JSMarshalAs<JSType.MemoryView>] Span<byte> pcm16);

    [JSImport("audioClear", ModuleName)]
    public static partial void AudioClear();

    [JSImport("audioSuspend", ModuleName)]
    [return: JSMarshalAs<JSType.Promise<JSType.Void>>]
    public static partial Task AudioSuspend();

    [JSImport("audioResume", ModuleName)]
    [return: JSMarshalAs<JSType.Promise<JSType.Void>>]
    public static partial Task AudioResume();

    // ---- input
    /// <summary>Routes document key events to <paramref name="onKey"/>(code, isDown) while <paramref name="focusElementId"/> has focus; the callback returns true to swallow the event.</summary>
    [JSImport("attachInput", ModuleName)]
    public static partial void AttachInput(string focusElementId,
        [JSMarshalAs<JSType.Function<JSType.String, JSType.Boolean, JSType.Boolean>>] Func<string, bool, bool> onKey,
        [JSMarshalAs<JSType.Function>] Action onBlur);

    [JSImport("detachInput", ModuleName)]
    public static partial void DetachInput();

    [JSImport("focusElement", ModuleName)]
    public static partial void FocusElement(string id);

    // ---- loop
    /// <summary>Starts requestAnimationFrame; each frame calls the [JSExport] <see cref="BrowserLoop.Tick"/> (or <paramref name="fallbackTick"/> when the exports cannot be resolved).</summary>
    [JSImport("startLoop", ModuleName)]
    [return: JSMarshalAs<JSType.Promise<JSType.Void>>]
    public static partial Task StartLoop([JSMarshalAs<JSType.Function<JSType.Number>>] Action<double> fallbackTick);

    [JSImport("stopLoop", ModuleName)]
    public static partial void StopLoop();

    [JSImport("now", ModuleName)]
    public static partial double Now();

    // ---- misc
    [JSImport("requestFullscreen", ModuleName)]
    public static partial void RequestFullscreen(string elementId);

    [JSImport("downloadCanvas", ModuleName)]
    public static partial void DownloadCanvas(string canvasId, string fileName);

    [JSImport("downloadBytes", ModuleName)]
    public static partial void DownloadBytes([JSMarshalAs<JSType.Array<JSType.Number>>] byte[] bytes, string fileName, string mime);

    /// <summary>
    /// Makes <paramref name="elementId"/> a drop target; <paramref name="onFile"/>(name) is called per dropped file,
    /// and the callback fetches the bytes with <see cref="TakeDroppedFile"/> (callbacks cannot receive arrays).
    /// </summary>
    [JSImport("attachDrop", ModuleName)]
    public static partial void AttachDrop(string elementId,
        [JSMarshalAs<JSType.Function<JSType.String>>] Action<string> onFile);

    /// <summary>Returns and forgets the content of a dropped file announced through <see cref="AttachDrop"/>.</summary>
    [JSImport("takeDroppedFile", ModuleName)]
    [return: JSMarshalAs<JSType.Array<JSType.Number>>]
    public static partial byte[]? TakeDroppedFile(string name);

    [JSImport("storageGet", ModuleName)]
    public static partial string? StorageGet(string key);

    [JSImport("storageSet", ModuleName)]
    public static partial bool StorageSet(string key, string value);

    [JSImport("storageRemove", ModuleName)]
    public static partial void StorageRemove(string key);

    [JSImport("isTouchDevice", ModuleName)]
    public static partial bool IsTouchDevice();
}
