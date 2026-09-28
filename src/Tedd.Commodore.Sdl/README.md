# Tedd.Commodore.Sdl — SDL2 front-end

A cross-platform (Windows, Linux, macOS) desktop front-end for the Tedd.CommodoreEmulator cycle-exact C64 emulator, built on
[SDL2](https://libsdl.org) through the [ppy.SDL2-CS](https://www.nuget.org/packages/ppy.SDL2-CS) bindings, which
bundle the native SDL2 library for win-x64/x86/arm64, linux-x64/x86 and osx-x64/arm64. It uses the host-agnostic
`Tedd.Commodore.Hosting` layer (session, runner, key bindings) and only adds the SDL surfaces.

## Usage

```
Tedd.Commodore.Sdl [--roms <dir>] [--disk file.d64] [--tape file.t64|file.prg] [--cart file.crt|file.bin]
                 [--autostart] [--warp] [--no-drive] [--scale N] [--joy-port 1|2] [--sample-rate N] [file ...]
```

| Option | Meaning |
|---|---|
| `--roms <dir>` | Directory with the ROM images. Without it the ROMs are searched in `C64_ROMS`, the executable's directory, the current directory and a `roms` / `src/Tedd.Commodore.GUI` sub-directory of their ancestors. |
| `--disk file.d64` | Insert a disk into drive 8. |
| `--tape file.t64` / `file.prg` | Load the (first) program of a tape image or a PRG file into memory as soon as BASIC is ready. |
| `--cart file.crt` / `file.bin` | Plug in a cartridge (CRT container or raw 8K/16K image) and reset. |
| `--autostart` | Type `LOAD"*",8,1` + `RUN` for the disk / `RUN` (or `SYS`) for the tape program. |
| `--warp` | Start as fast as possible (no sound). |
| `--no-drive` | Do not attach a 1541. |
| `--scale N` | Initial window scale, default 3 (1152 x 816 for the 384 x 272 PAL picture). |
| `--joy-port 1\|2` | Which C64 joystick port the game controllers drive, default 2. |
| `--sample-rate N` | Requested audio rate, default 44100. The device may pick another (typically 48000); the emulator then resamples to that rate. |

Bare file arguments are attached by extension/content exactly like a drag-and-drop (`--autostart` applies to them).

The ROM images are not part of the repository. Recognised names follow VICE: `basic.901226-01.bin` (or `basic.bin`),
`kernal.901227-03.bin` (`kernal.bin`), `characters.901225-01.bin` (`chargen.bin`) and for the drive
`1541-II.251968-03.bin` / `dos1541` or the `1541-c000.325302-01.bin` + `1541-e000.901229-05.bin` pair.

Once running, the console prints a status line every second: emulated fps, display fps, warp/paused, attached media,
drive track and LED, audio queue length and connected controllers. Messages (attach results, screenshots, memory
dumps) are printed above it.

## Window

* The picture is scaled by integers only (largest multiple of 384 x 272 that fits) and centred; resize freely.
* **Left Alt + Enter** or **Left Alt + F** toggles fullscreen (desktop resolution, integer scaled, cursor hidden).
* Closing the window (or Ctrl+C in the console) quits. **Escape does not quit** — it is the C64 RUN/STOP key.
* Drop a `.d64`, `.t64`, `.prg`, `.crt` or raw cartridge `.bin` onto the window to attach it; disks and programs are
  autostarted.
* When the window loses focus every key and joystick input is released, so nothing sticks.
* Rendering is paced by the emulator (PAL 50.12 Hz): the emulator thread produces frames, the SDL main thread
  uploads the newest one into a streaming ARGB8888 texture and presents it, capped at 60 presentations per second in
  warp mode. Vsync is off so the emulator's timing, not the monitor's, defines the speed.

## Keys

Keys are physical positions (W3C `KeyboardEvent.code` names, e.g. `KeyA`, `Digit1`, `Numpad8`), so the mapping is the
same on every keyboard layout. Bindings are read from the per-user file

```
%APPDATA%\Tedd.Commodore\keybindings.json                 (Windows)
~/.config/Tedd.Commodore/keybindings.json                 (Linux, macOS)
```

which the WPF front-end's key binding editor writes; when it does not exist the defaults below apply. The file is a
flat JSON object `{ "code": "action" }` where the action is `key:<C64Key>[+shift]`, `joy1:<up|down|left|right|fire>`,
`joy2:...` or `sys:<restore|reset|hardreset|pause|warp|screenshot|memoryviewer>`, e.g.

```json
{ "KeyA": "key:A", "Digit1": "key:D1", "F2": "key:F1+shift", "Numpad8": "joy2:up", "PageUp": "sys:restore", "F12": "sys:screenshot" }
```

Default layout:

| PC key | C64 |
|---|---|
| Letters, digits, Space, Enter | the same keys |
| `-` `=` | `-` `+` |
| `[` `]` `\` | `@` `*` `£` |
| `;` `'` | `;` `:` |
| `` ` `` (Backquote) | `←` (left arrow) |
| `,` `.` `/` | `,` `.` `/` |
| Backspace / Delete, Insert | INST/DEL, SHIFT+INST/DEL |
| Escape | RUN/STOP |
| Tab | C= (Commodore) |
| Ctrl (either) | CTRL |
| Shift left / right | SHIFT left / right |
| Home, End | CLR/HOME, SHIFT+CLR/HOME |
| Cursor keys | CRSR (up/left = shifted) |
| F1-F8 | F1-F8 (even numbers = shifted) |
| Page Up | RESTORE |
| Numpad 8/2/4/6, 0 or 5, Right Alt | Joystick 2 up/down/left/right, fire |
| Numpad `+` `-` `*` `/` `.` | `+` `-` `*` `/` `.` |
| F11 | Reset |
| F12 | Screenshot (PNG next to the executable) |
| Pause | Pause / resume |

Host commands can also be bound to keys with `sys:warp` (toggle warp), `sys:hardreset` and `sys:memoryviewer` (prints
the 6510/1541 registers and a hex dump of the zero page to the console).

## Game controllers

Every controller SDL recognises as a game controller (Xbox, PlayStation, Switch Pro, most USB pads) is opened
automatically, also when plugged in while the emulator runs. D-pad and the left stick (dead zone 25 %) are the joystick
directions, **A** and **B** are fire. All controllers act on the port chosen with `--joy-port` (default 2, the port most
games use); the numeric keypad remains available as a keyboard joystick.

## Building and running

Requires the .NET SDK the solution targets (`net11.0`).

```
dotnet run --project src/Tedd.Commodore.Sdl -- --roms /path/to/roms --disk game.d64 --autostart
```

Self-contained builds for other platforms (the native SDL2 library is included by the NuGet package):

```
dotnet publish src/Tedd.Commodore.Sdl -c Release -r win-x64   --self-contained -o out/win-x64
dotnet publish src/Tedd.Commodore.Sdl -c Release -r linux-x64 --self-contained -o out/linux-x64
dotnet publish src/Tedd.Commodore.Sdl -c Release -r osx-x64   --self-contained -o out/osx-x64
dotnet publish src/Tedd.Commodore.Sdl -c Release -r osx-arm64 --self-contained -o out/osx-arm64
```

On Linux the bundled `libSDL2.so` needs the usual desktop libraries (X11 or Wayland, libGL, ALSA/PulseAudio/PipeWire);
on Debian/Ubuntu `apt install libx11-6 libxext6 libgl1 libasound2` covers a minimal box, or install the distribution's
`libsdl2-2.0-0` package, which pulls in everything and can be used instead of the bundled copy. On macOS the app is
started from a terminal; Gatekeeper may ask to allow the unsigned `libSDL2.dylib` on first start.

## Reusable pieces

`SdlVideoSink`, `SdlAudioSink`, `SdlGameControllers` and `SdlKeyCodes` (the full SDL scancode to W3C code table, with a
reverse lookup) are public and self-contained, so another SDL-based host (an SDL_gpu/OpenGL front-end, an ImGui
debugger, a libretro-like shell) can reuse them; `SdlHost` and `Program` are the thin application on top.

## Limitations

* No menu or on-screen UI: everything is done with the command line, key bindings, drag-and-drop and the console.
* The screenshot goes next to the executable (`Tedd.CommodoreEmulator-<timestamp>.png`); the "memory viewer" command prints
  to the console instead of opening a window.
* Only the first entry of a `.t64` is loaded (no chooser). Disk writes are kept in memory and not saved back to the
  `.d64` on exit.
* Key bindings are read, not edited: create or change the JSON file by hand or with the WPF front-end.
