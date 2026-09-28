# Tedd.Commodore

A cycle-exact Commodore 64 and Commodore 128 emulator written in C# (.NET 11), with a real 1541 disk drive
emulation and several front-ends: two Windows desktop apps (WPF and .NET MAUI), a browser build (Blazor WebAssembly),
an SDL2 app and a Unity package. The C128 boots BASIC 7 on both its screens and CP/M 3.0 from the included system disks.
For your own programs (a Vulkan game, an Avalonia app) it comes as NuGet packages that hand out raw frames and PCM.

Live demo and downloads: https://tedd.no/Tedd.Commodore/ (GitHub Pages, also reachable as https://tedd.github.io/Tedd.Commodore/; published from the `deploy` branch, see below). Binaries: https://github.com/tedd/Tedd.Commodore/releases/latest

## What is emulated

| Part | Model |
|---|---|
| CPU | Cycle-stepped NMOS 6502/6510: every clock is one bus cycle including dummy reads/writes, all 256 opcodes (documented and undocumented), NMOS decimal mode, RDY stalling, interrupt polling quirks (SEI/CLI/branch delays, NMI hijack), SO pin. Verified against all 2.56 million SingleStepTests/65x02 cycle-trace vectors and Klaus Dormann's functional test. |
| VIC-II | PAL 6569, per cycle: bad lines, all graphics modes, sprites with DMA timing and collisions, border unit, raster/collision/light-pen interrupts, register read-back rules. |
| CIA | Two 6526 with the Lorenz delay-pipeline timer model, TOD, serial register, old/new IRQ timing. |
| SID | 6581 following reSID: oscillators, ADSR with the rate-counter bugs, two-integrator filter, resampled output. |
| Memory | PLA with every LORAM/HIRAM/CHAREN/EXROM/GAME configuration incl. Ultimax, 6510 port, color RAM, open-bus reads. |
| Peripherals | Keyboard matrix (both scan directions), two joysticks, RESTORE, 8K/16K/Ultimax cartridges (raw and .CRT type 0), T64/PRG injection. |
| 1541 | Real drive: 6502 at 1 MHz, DOS ROM, two 6522 VIAs, IEC bus with the hardware ATN acknowledge, GCR bit stream at 300 RPM with the four density zones, writes go back into the D64. |

Commodore 128 (`src/Tedd.Commodore.Emulator/C128`):

| Part | Model |
|---|---|
| CPUs | The 8502 is the same cycle-stepped core with the C128's port (CAPS LOCK, colour RAM banks), one or two bus cycles per system cycle (2 MHz mode). The Z80A is instruction stepped with the documented T-state counts, all opcodes including the undocumented ones, X/Y flags, WZ/MEMPTR and the Q latch; verified against all 1604 SingleStepTests/z80 vector files. It gets two T-states per system cycle (VICE's model) and only one CPU runs at a time: MMU register $D505 hands the bus over at the next instruction boundary. |
| MMU 8722 | CR/PCR A-D/MCR/RCR/P0/P1/version, the $FF00-$FF04 window, two RAM banks with the common area, page 0/1 relocation (the target page swaps places), CPU select, C64 mode with the cartridge lines. |
| Memory | 128K RAM, BASIC 7 low/high, editor + Z80 BIOS + KERNAL, 8K character ROM (C64 set / C128 set), the C64 mode ROMs, two 1K colour RAM banks, I/O layout with VDC at $D600, the Z80's view (BIOS at $0000 in bank 0, ports $0xxx to the RAM under I/O, $Dxxx to the chips), external function ROM from the cartridge port. |
| VIC-IIe | The C64 VIC plus $D02F (K0-K2 keyboard lines for the 24 extra keys) and $D030 (2 MHz mode: the VIC keeps its timing and interrupts but fetches nothing and never asserts BA). |
| VDC 8563 | 16K (or 64K) video RAM, 37 registers, status with the ready/blank bits and VICE's busy timing, block copy/fill, text with attributes (colour, reverse, blink, underline, alternate set), 640 x 200 bitmap, hardware cursor, 16 MHz raster model; renders a 768 x 272 picture once per VDC frame. Smooth scrolling and interlace are not modelled. |
| Keyboard | The 11 x 8 matrix (three rows driven by the VIC-IIe), the 40/80 DISPLAY and CAPS LOCK toggles. |

## Repository layout

```
src/Tedd.Commodore.Emulator   the emulator core (no UI, no dependencies)
src/Tedd.Commodore.Hosting    host-agnostic session, key bindings, video/audio sink contracts
src/Tedd.Commodore.Embedding  the emulator for programs that draw and play it themselves (Vulkan, Avalonia...): raw frames, PCM
src/Tedd.Commodore.GUI        Windows desktop front-end (WPF): memory viewer/editor, sprite viewer, character set viewer, key binding editor, audio visualizer
src/Tedd.Commodore.Maui       the same desktop front-end ported to .NET MAUI (Windows head)
src/Tedd.Commodore.Web        Blazor WebAssembly front-end and the project web site
src/Tedd.Commodore.Sdl        SDL2 front-end (Windows/Linux/macOS)
src/Tedd.Commodore.Unity      game-engine facade (netstandard2.1) used by the Unity package in unity/
src/Tedd.Commodore.Tests      NUnit test-suite (~1500 tests)
docs/ARCHITECTURE.md          component contracts and timing model
disks/c128                    the CP/M 3.0 system and utility disks Commodore shipped with the C128 (D64)
```

## ROM images

The Commodore ROMs are copyrighted and are not part of the repository. Put them in a `roms` folder next to the
executable, in `src/Tedd.Commodore.GUI/` when working from source, or point the `C64_ROMS` environment variable
at them:

* `basic.901226-01.bin` (8 KiB) and `kernal.901227-03.bin` (8 KiB), or the combined `64c.251913-01.bin` (16 KiB)
* `characters.901225-01.bin` (4 KiB) or another `characters*.bin`
* optional 1541 ROM: `1541-II.251968-03.bin` (16 KiB) or the pair `1541-c000.325302-01.bin` + `1541-e000.901229-05.bin`

A Commodore 128 additionally needs (same folder; `C128_ROMS` is checked before `C64_ROMS`; all of them are at
https://www.zimmers.net/anonftp/pub/cbm/firmware/computers/c128/):

* `basic-4000.318018-04.bin` + `basic-8000.318019-04.bin` (16 KiB each), or the combined `basic.318022-02.bin` (32 KiB)
* `kernal.318020-05.bin` (16 KiB: screen editor, Z80 BIOS, KERNAL), or `complete.318023-02.bin` (32 KiB, which also
  contains the C64 mode BASIC and KERNAL)
* `characters.390059-01.bin` (8 KiB: the C64 character set in the lower half, the C128 set in the upper half)
* the C64 BASIC and KERNAL listed above (used in C64 mode) unless the `complete` image is present

There is no free replacement for the C128 ROMs, so the web build only offers the C128 once you have loaded them.

The web build can use [OpenROMs](https://github.com/MEGA65/open-roms) (a free LGPL re-implementation of
BASIC/KERNAL/character set, fetched by `tools/fetch-openroms.sh`; no 1541 DOS) and also lets you upload your
own ROMs, which stay in your browser's local storage.

## Building and testing

Requires the .NET 11 SDK.

```bash
dotnet build src/Tedd.Commodore.sln
dotnet test src/Tedd.Commodore.Tests/Tedd.Commodore.Tests.csproj
```

Slow and data-dependent suites:

* `Category=Slow` includes Klaus Dormann's functional test and the 1541 loading tests.
* `HARTE_6502_TESTS=<dir>` enables the 2.56 million SingleStepTests vectors (download `6502/v1/*.json` from
  https://github.com/SingleStepTests/65x02).
* `HARTE_Z80_TESTS=<dir>` enables the 1.6 million SingleStepTests/z80 vectors (the `v1/*.json` files of
  https://github.com/SingleStepTests/z80, about 1.4 GB); all 1604 files pass.
* The C128 system tests need the C128 ROMs; `Category=Slow` includes booting CP/M 3.0 from `disks/c128` to the `A>` prompt.
* `LORENZ_TESTS=<dir>` enables Wolfgang Lorenz's C64 test-suite (the `*.prg` files from VICE's
  `testprogs/general/Lorenz-2.15/src`), run with `dotnet test -c Release --filter Category=Lorenz`
  (`LORENZ_ONLY=cia1ta,irq` narrows it down). Status: all 221 CPU opcode programs, `trap1`-`trap16`,
  `branchwrap`, `mmufetch`, `mmu`, `cpuport`, `cputiming`, `irq` and every CIA timer program (`cia1ta/tb`,
  `cia2ta/tb`, `*pb6/7`, `*tb123`, `cia1tab`, `icr01`, `imr`, `flipos`, `oneshot`, `cntdef`, `cnto2`, `loadth`)
  pass; `nmi` (NMI arriving during BRK) and `trap17` are still open.

Rendering tests write PNG frames to `TestResults/` next to the test assembly.

## Front-ends

* **WPF** (`src/Tedd.Commodore.GUI`): Machine menu to switch between the Commodore 64 and 128 (remembered), the C128's
  40/80 DISPLAY and CAPS LOCK keys and which screen to show (the window resizes for the 80 column picture), attach D64/T64/PRG/CRT, autostart, screenshots, freeze + memory viewer/editor
  with cycle/instruction/frame stepping, sprite viewer (all eight sprites zoomed, with their registers, DMA state
  and position on screen), character set viewer (the ROM sets or the 2 KiB the VIC is reading live, per character
  screen/PETSCII codes and addresses, and loading a different character set over the running machine), key binding
  editor (Tools menu) where you pick the keys that make up the joysticks, audio visualizer window.
* **MAUI** (`src/Tedd.Commodore.Maui`): the WPF front-end above, feature for feature, on .NET MAUI - the same
  windows, the same key bindings file, and the tool windows redrawn with `Microsoft.Maui.Graphics` so they are
  not tied to Windows. Every picture made of C64 pixels - the screen, the character set grid, a blown up
  character, the sprites and their thumbnails - is written into a `Tedd.WriteableBitmap.Maui` bitmap at one
  bitmap pixel per C64 pixel and magnified by the GPU with nearest neighbour sampling, so a repaint costs what
  the machine has rather than what the screen shows, and stays sharp at any zoom; grids, boxes and labels are
  drawn in screen pixels on a transparent canvas over it. Only the Windows head is built
  (`net10.0-windows10.0.19041.0`, because the .NET 11 MAUI workload is not published yet); the emulator picture,
  sound, the save dialog and the physical key reader are the four pieces behind a platform boundary, so adding a
  macOS, Android or iOS head means installing that workload, adding the TFM and implementing those.
* **Web** (`src/Tedd.Commodore.Web`): runs entirely in the browser (video on a canvas, audio through an AudioWorklet),
  machine selector, C128 ROM slots, a menu that boots the shipped CP/M disks, the 40/80 key, drag-and-drop media,
  touch joystick, built-in tech demo.
* **SDL2** (`src/Tedd.Commodore.Sdl`): `Tedd.Commodore.Sdl --roms <dir> --disk game.d64 --autostart`, `--machine c128 --80`
  for a C128 on its 80 column screen, game controller support.
* **Unity** (`unity/com.tedd.commodore`): UPM package with a `C64Emulator` MonoBehaviour that renders to a texture and
  plays audio through `OnAudioFilterRead`; build the DLLs with `tools/build-unity-package.ps1`.
* **Your own program** (`src/Tedd.Commodore.Embedding`, NuGet package `Tedd.Commodore.Embedding`): `EmbeddedEmulator` runs a
  C64 or C128 from your game loop (`Update(deltaSeconds)`) or on its own thread, and hands the picture out as raw
  BGRA, RGBA or palette-index bytes written straight into memory you own (a mapped Vulkan staging buffer, a locked
  Avalonia `WriteableBitmap`) with your row pitch, and the sound as float or 16-bit PCM for your audio callback.
  No graphics or UI references; key-code helpers for Avalonia and GLFW (Silk.NET, OpenTK, Raylib). See
  [its README](src/Tedd.Commodore.Embedding/README.md).

All front-ends share `Tedd.Commodore.Hosting`: implement `IVideoSink`/`IAudioSink`, translate your key events to
W3C `KeyboardEvent.code` names and call `EmulatorSession.RunFrame()` from your loop (or use `EmulatorRunner`). A
session is created from a `RomSet` (C64) or a `C128RomSet` (C128); `VideoFrame` carries its own geometry because a
C128 switches between the 384 x 272 VIC-II picture and the 768 x 272 VDC picture (`EmulatorSession.Display`).

Booting CP/M: attach `disks/c128/cpm.system.622-580745.d64` to a C128 with the drive enabled and reset (the session
does that for you when autostart is on and the disk has a boot sector). The 1541 needs about two minutes of emulated
time to load `CPM+.SYS`, so turn on warp.

## Deployment

Pushing the `deploy` branch runs `.github/workflows/deploy.yml`: it tests the solution, publishes the Windows
builds (WPF and MAUI), the SDL2 builds and the NuGet packages as a GitHub Release, and publishes the web site + browser emulator to GitHub Pages
(enable *Settings > Pages > Source: GitHub Actions* once).

The libraries (`Tedd.Commodore.Emulator`, `Tedd.Commodore.Hosting`, `Tedd.Commodore.Embedding`) are built, tested and packed by
`.github/workflows/library.yml` on every push to `master` and every pull request; the packages are the run's
`nuget-packages` artifact. Their version is `0.1.<number of commits>` (major.minor in `src/Package.props`). Locally:
`dotnet pack src/Tedd.Commodore.Embedding -c Release -o artifacts/packages` (and the same for Emulator and Hosting).

## License

MIT (see LICENSE). Test data under `src/Tedd.Commodore.Tests/TestData` has its own licenses (see the README there).

Commodore, Commodore 64, Commodore 128, VIC-II, SID and 1541 are trademarks of their respective owners.
Tedd.Commodore is an independent project and is not affiliated with or endorsed by them.
