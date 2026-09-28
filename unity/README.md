# Tedd.Commodore in Unity

`unity/com.tedd.commodore` is a Unity package (UPM layout) that embeds the cycle-exact C64 emulator in a Unity
scene: the picture goes to a `Texture2D`, the SID plays through an `AudioSource`, and the keyboard is forwarded
through the emulator's key bindings. It works with Unity 2021.3 or newer (any scripting backend; the assemblies
target .NET Standard 2.1 and contain no platform code).

```
unity/com.tedd.commodore/
  package.json                    UPM manifest (com.tedd.commodore 0.1.0)
  Runtime/Tedd.Commodore.asmdef   assembly definition referencing the plugin DLLs
  Runtime/C64Emulator.cs          the MonoBehaviour (compiled by Unity)
  Runtime/Plugins/                the three DLLs (build output, not committed)
```

## 1. Build the DLLs

From the repository root (needs the .NET SDK the solution uses):

```
pwsh tools/build-unity-package.ps1        # Windows (PowerShell 7) - or: powershell -File tools\build-unity-package.ps1
sh   tools/build-unity-package.sh         # macOS / Linux
```

The script runs `dotnet build -c Release -f netstandard2.1` for `Tedd.Commodore.Emulator`, `Tedd.Commodore.Hosting`
and `Tedd.Commodore.Unity` and copies `Tedd.Commodore.Emulator.dll`, `Tedd.Commodore.Hosting.dll` and
`Tedd.Commodore.Unity.dll` (plus their `.pdb`) into `unity/com.tedd.commodore/Runtime/Plugins/`. Re-run it whenever
the emulator changes; Unity reimports the plugins automatically.

## 2. Add the package to a project

Either

* Unity: **Window > Package Manager > + > Add package from disk...** and pick
  `unity/com.tedd.commodore/package.json` (Unity records a `file:` reference in `Packages/manifest.json`), or
* copy the whole `com.tedd.commodore` folder into your project's `Packages/` directory, or
* add `"com.tedd.commodore": "file:../../path/to/Tedd.Commodore/unity/com.tedd.commodore"` to `Packages/manifest.json`.

The package's assembly definition references `UnityEngine.UI` (the built-in uGUI package) for the optional
`RawImage` target. If your project does not have `com.unity.ugui`, remove `"UnityEngine.UI"` from the
`references` list in `Runtime/Tedd.Commodore.asmdef`; the `RawImage` field is compiled only when uGUI is present.

## 3. ROM images

The C64 ROMs are not distributed with the emulator. You need:

| ROM | Size | Common file names |
|---|---|---|
| BASIC V2 | 8192 | `basic.901226-01.bin`, `basic.bin` |
| KERNAL | 8192 | `kernal.901227-03.bin`, `kernal.bin` |
| Character generator | 4096 | `characters.901225-01.bin`, `chargen.bin` |
| 1541 DOS (optional, for .d64) | 16384 | `1541-II.251968-03.bin`, `1541.bin` |

Provide them in one of two ways:

* **TextAssets**: rename the files with a `.bytes` extension (`basic.bytes`, ...), put them anywhere under
  `Assets/`, and drag them onto the `C64Emulator` component's ROM fields. Works on every platform.
* **StreamingAssets**: put them in `Assets/StreamingAssets/C64/` as `basic.bin`, `kernal.bin`, `chargen.bin`
  and (optionally) `1541.bin`. The folder and file names are editable on the component. This reads with
  `File.ReadAllBytes`, which does not work on Android/WebGL where StreamingAssets is not a plain folder; use
  TextAssets there.

## 4. Scene setup

1. Create a GameObject (a **Quad** is convenient: 384 x 272 picture, so give it a 1.41 : 1 scale) and add
   **Tedd.Commodore > C64 Emulator**. An `AudioSource` is added automatically; it needs no clip.
2. Assign the ROM TextAssets (or rely on StreamingAssets).
3. Drag the Quad's `MeshRenderer` into **Target Renderer** (an Unlit/Texture material looks best), and/or a uGUI
   `RawImage` into **Target Image**. `Flip Vertically` stays on: Unity textures are bottom-up.
4. Press Play. The BASIC screen appears after about a second.

The component exposes `Texture` (the RGBA32 texture, point filtered) if you want to bind it to something else,
`Bridge` (the `Tedd.Commodore.Unity.C64Bridge` facade) and `Bridge.Session` / `Bridge.Machine` for everything the
hosting layer offers (memory, chips, drive, screen text, key bindings).

### Audio

Audio is produced in `OnAudioFilterRead` at `AudioSettings.outputSampleRate`; the `AudioSource` acts only as an
output slot (its volume and mixer group apply). About 250 ms of audio is buffered; if `Bridge.AudioUnderruns`
grows the emulator is not keeping up (check `Time.deltaTime`), if `AudioOverruns` grows the audio thread is not
draining (the AudioSource is muted or not playing).

### Keyboard

With `Capture Keyboard` on, the component polls the legacy Input manager (`Input.GetKeyDown/Up`) for every
`KeyCode` that has a keyboard equivalent, translates it with `UnityKeyCodes.ToWebCode(keyCode.ToString())` to
a W3C `KeyboardEvent.code` ("KeyA", "Digit1", "Enter", "Numpad8"...) and feeds it to the bridge, where the
`KeyBindings` table decides what it does on the C64. Defaults: letters/digits/punctuation where a PC keyboard
has them, Escape = RUN/STOP, Tab = C=, Backspace = INST/DEL, cursor keys, F1-F8, numeric keypad = joystick 2
(8/2/4/6, 0 or 5 = fire), Page Up = RESTORE, F11 = reset, Pause = pause. Edit `Bridge.Bindings` or load a
JSON file with `KeyBindings.Load(path)`.

Projects that use only the new Input System (`Active Input Handling = Input System Package (New)`) do not get
keyboard polling (the code is compiled out); call `Bridge.KeyDown/KeyUp` with W3C codes, `Bridge.PressKey`
with `C64Key` values or `Bridge.SetJoystick` from your own input code instead.

### Media

`AttachMedia(byte[] data, string fileName, bool autostart)` (or `AttachMediaFile(path)`) accepts `.d64` disks
(needs the 1541 ROM), `.t64`/`.prg` programs and `.crt`/raw cartridges; the file name's extension selects the
type. With autostart the first program is loaded and run once BASIC is ready. `Startup Media` on the component
does this on start (rename the image to `.bytes` to import it as a TextAsset and put the real file name in
`Startup Media File Name`). `EjectDisk()`, `DetachCartridge()`, `Reset(hard)`, `TypeText("LIST\n")`,
`Paused` and `Warp` are also available.

## Using the bridge without the MonoBehaviour

`Tedd.Commodore.Unity.C64Bridge` has no Unity dependency and is what the component is built on:

```csharp
var bridge = C64Bridge.Create(basic, kernal, chargen, driveRom /* or null */, AudioSettings.outputSampleRate);
// per frame
if (bridge.Update(Time.deltaTime)) { bridge.CopyFrameRgba(rgba, flipVertically: true); texture.LoadRawTextureData(rgba); texture.Apply(); }
// audio thread
void OnAudioFilterRead(float[] data, int channels) => bridge.ReadAudio(data, channels);
```

`Update` runs as many 50 Hz PAL frames as the elapsed time requires (at most `MaxFramesPerUpdate`, default 3,
then the backlog is dropped) and returns true when the picture changed.

## Limitations

* Keyboard input relies on the legacy Input manager; the new Input System is not wired up.
* Key mapping is positional for a US layout (Unity reports physical keys as US-layout `KeyCode`s).
* StreamingAssets ROM loading uses `System.IO`, so it does not work on Android/WebGL (use TextAssets).
* The picture is uploaded with `LoadRawTextureData` every frame (about 400 KB); fine on any platform, but on
  mobile you may want to skip uploads when the emulator is paused.
* The `.meta` files Unity generates inside the package are ignored by the repository's `.gitignore`
  (`*.meta`), so asset GUIDs are regenerated per checkout.
