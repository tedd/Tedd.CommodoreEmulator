# Tedd.Commodore.Web

Blazor WebAssembly front-end for the Tedd.Commodore C64 emulator. The complete machine (6510, VIC-II, two
CIAs, SID and optionally the 1541) runs client-side in the browser; there is no server component. The
project doubles as the project web site (hero, features, downloads, the interactive demo and the
keyboard help), published to GitHub Pages under `/Tedd.Commodore/`.

The site's animation lives in `wwwroot/js/effects.js` on top of a vendored GSAP; it is layered on from the
outside (it waits for Blazor to render, then animates), so the Razor components stay free of it.

## Layout

| Path | Purpose |
|---|---|
| `Components/Emulator.razor(.cs)` | The interactive emulator component (canvas, toolbar, status, touch controls, type-text box, media and ROM panels). |
| `Interop/C64Js.cs` | `[JSImport]` bindings for `wwwroot/js/c64.js`. |
| `Interop/BrowserVideoSink.cs` | `IVideoSink` -> canvas (`putImageData` of the 384x272 RGBA picture, last frame of each animation tick only). |
| `Interop/BrowserAudioSink.cs` | `IAudioSink` -> AudioWorklet (16-bit PCM chunks per frame, ring buffer with silence on underrun). |
| `Interop/BrowserInput.cs` | document `keydown`/`keyup` -> `EmulatorSession.KeyDown/KeyUp` (W3C `KeyboardEvent.code`), release-all on blur. |
| `Interop/BrowserLoop.cs` | `[JSExport] Tick` called from `requestAnimationFrame`; runs 1/50.125 s of emulation per elapsed wall-clock time, max 3 frames per tick. |
| `Services/RomStore.cs` | ROM discovery: localStorage (base64) -> `roms/*.bin` on the site -> uploads; size validation, split/combined images. |
| `Demo/DemoProgram.cs` | Generates the tech demo's 6502 source (raster bars, scroller, sprites, SID tune) and assembles it at runtime with `Tedd.Commodore.Emulator.Tools.Assembler`. |
| `wwwroot/js/c64.js` | Framework-free ES module: canvas, AudioContext/worklet, key listeners, rAF loop, fullscreen, PNG download, drag & drop, localStorage. |
| `wwwroot/js/c64-audio-worklet.js` | The `AudioWorkletProcessor` that plays the SID output. |
| `wwwroot/js/effects.js` | Page animation (hero intro, scroll reveals, raster/starfield backdrop, card tilt, eased anchors). Presentation only - it never touches the emulator, and the page works unchanged if it does not load or the visitor prefers reduced motion. |
| `wwwroot/js/vendor/gsap/` | GSAP 3.15.0 (core + ScrollTrigger, ScrollToPlugin, TextPlugin, SplitText), vendored so nothing is fetched from a CDN. See the README there. |
| `wwwroot/roms/` | ROM images (git-ignored); see below. |
| `wwwroot/index.html`, `404.html`, `.nojekyll` | Static shell, GitHub Pages SPA fallback, disable Jekyll (so `_framework` is served). |

`c64.js` and the `Interop/*` classes are deliberately independent of Blazor so another web host (a plain
.NET-on-WASM app, a different UI framework) can reuse them: import the module, create a
`BrowserVideoSink`/`BrowserAudioSink`/`BrowserInput`, give them to an `EmulatorSession`, and call
`BrowserLoop.Attach` + `BrowserLoop.StartAsync`.

## Build and run

Requirements: .NET SDK 11 (preview) with the `wasm-tools` workload for AOT (`dotnet workload install wasm-tools`).

```sh
# development (interpreter, slow but quick to build)
dotnet run --project src/Tedd.Commodore.Web

# release: AOT compiled WebAssembly (takes a few minutes)
dotnet publish src/Tedd.Commodore.Web -c Release -o out/web
cd out/web/wwwroot && python3 -m http.server 8080      # any static file server works
```

The Release configuration sets `RunAOTCompilation=true`, `WasmEnableSIMD`, `InvariantGlobalization` and
disables the pre-compressed `.br/.gz` output (GitHub Pages does not use them). Without AOT the Mono
interpreter is far too slow for a cycle-exact C64; Debug builds are only useful for UI work.

Static hosting notes:

* `wwwroot/.nojekyll` must be published (GitHub Pages otherwise hides `_framework/`).
* `index.html` and `404.html` contain `<base href="/" />`. When hosting under a sub-path, rewrite both:
  `sed -i 's|<base href="/" />|<base href="/Tedd.Commodore/" />|' index.html 404.html`.
* `404.html` redirects unknown paths to the base with `?p=<path>`; `index.html` restores the path with
  `history.replaceState`, so deep links work without server-side rewrites.
* The `.wasm` files should be served as `application/wasm` (Python's `http.server` and GitHub Pages do).

## ROMs

The original Commodore ROMs are copyrighted and never committed. The app looks for images in this order:

1. localStorage (`tedd.commodore.rom.*`, base64) - whatever the user uploaded through "Load your own ROMs".
2. `roms/basic.bin` (8192), `roms/kernal.bin` (8192), `roms/chargen.bin` (4096), `roms/1541.bin` (16384,
   optional), fetched relative to the site root at startup.
3. Uploads (per-slot file inputs, or ROM files dropped on the emulator). BASIC/KERNAL/character sizes are
   validated; the 1541 slot accepts one 16K image or the two 8K halves (`...-c000...` + `...-e000...`, order
   detected from the reset vectors); the combined 16K `64c.251913-01.bin` fills BASIC and KERNAL.

For a free default, `tools/fetch-openroms.ps1` (PowerShell) or `tools/fetch-openroms.sh` download
[OpenROMs](https://github.com/MEGA65/open-roms) (LGPL-3.0) into `wwwroot/roms/`:

```
https://raw.githubusercontent.com/MEGA65/open-roms/master/bin/basic_generic.rom    -> basic.bin
https://raw.githubusercontent.com/MEGA65/open-roms/master/bin/kernal_generic.rom   -> kernal.bin
https://raw.githubusercontent.com/MEGA65/open-roms/master/bin/chargen_openroms.rom -> chargen.bin
LICENSE, COPYING.LESSER
```

(The open-roms repository has no GitHub releases; the pre-built images live in its `bin/` directory.)
OpenROMs boots to `READY.` in a few frames, runs BASIC and machine code, and is enough for the built-in
demo, PRG/T64 injection and cartridges. It is not byte-compatible with Commodore's ROMs (programs that
call into ROM routines or depend on their timing may misbehave) and there is no free 1541 DOS, so disk
images need a real drive ROM.

## Deploying to GitHub Pages

A deploy job needs to: install the `wasm-tools` workload, run `tools/fetch-openroms.sh`, `dotnet publish
-c Release`, rewrite the `<base href>` in `wwwroot/index.html` and `wwwroot/404.html` to `/Tedd.Commodore/`,
and upload `wwwroot` (including `.nojekyll`) as the Pages artifact.
