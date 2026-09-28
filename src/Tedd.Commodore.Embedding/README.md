# Tedd.Commodore.Embedding

The cycle-exact Commodore 64 / 128 emulator [Tedd.CommodoreEmulator](https://github.com/tedd/Tedd.CommodoreEmulator) as a library for
programs that draw and play it themselves: a Vulkan, Direct3D or OpenGL game, an Avalonia application, a video
encoder. It has no graphics, audio or UI dependencies. Pictures come out as raw pixels written straight into memory
you own (a mapped staging buffer, a locked bitmap), sound comes out as PCM for your audio callback, and the machine
runs either from your game loop or on a thread of its own.

Targets `net11.0` and `netstandard2.1` (.NET 8, 9 and 10 applications use the latter automatically).

## Installing

The packages are built by the repository's *Library* workflow: download the `nuget-packages` artifact of a run (or
the `.nupkg` files attached to a release), put the three packages (`Tedd.Commodore.Emulator`, `Tedd.Commodore.Hosting`,
`Tedd.Commodore.Embedding`) in a folder and use it as a package source:

```bash
dotnet nuget add source /path/to/packages --name Tedd.Commodore
dotnet add package Tedd.Commodore.Embedding
```

Or reference `src/Tedd.Commodore.Embedding/Tedd.Commodore.Embedding.csproj` from a clone.

## Quick start

```csharp
using Tedd.Commodore.Embedding;
using Tedd.Commodore.Emulator.C64;

// The Commodore ROMs are not included, see "ROMs" below.
using var c64 = new EmbeddedEmulator(RomSet.Load("roms"), new EmbeddedEmulatorOptions { SampleRate = 48000 });
c64.AttachMedia(File.ReadAllBytes("game.d64"), "game.d64");   // loads and runs the first program

// Every tick of the game loop:
c64.Update(deltaSeconds);                     // runs the PAL frames that are due (50.125 per second)
if (c64.Video.TryAcquire(out RawFrame frame)) // the newest picture, if there is a new one
    frame.CopyTo(mappedPointer, mappedSize, rowPitch, FramePixelFormat.Bgra8888);

// On the audio thread:
c64.Audio.Fill(interleavedFloats, channels: 2);
```

A Commodore 128: `new EmbeddedEmulator(C128RomSet.Load("roms"), new EmbeddedEmulatorOptions { Columns80 = true })`.

## Running it

| | |
|---|---|
| `Update(deltaSeconds)` | From your loop. Runs as many whole frames as the elapsed time asks for, at most `MaxFramesPerUpdate` (3); after a longer hitch the backlog is dropped instead of caught up. Returns the number of frames run. |
| `RunFrame()` | Exactly one frame, whatever the time: offline rendering, tests, frame stepping. |
| `Start()` / `Stop()` | On a thread of its own, paced to real time. Your render loop just picks up frames. |
| `Paused`, `Warp` | Freeze (and flush the audio); run flat out with the sound muted. |

A PAL frame of a C64 with its 1541 takes about 3 ms on a current desktop CPU, so the emulator needs about a sixth of
one core either way.

## The picture

`Video` is a triple buffer: the emulator publishes every finished frame, your renderer takes the newest one with
`TryAcquire` whenever it is about to draw. Neither side waits for the other. The emulator makes 50.125 frames a
second; on a 60 Hz display `TryAcquire` returns false on about one tick in six and you draw the previous picture
again (`Video.Current`). A frame you took stays untouched until your next `TryAcquire`. For push-style hosts
`Video.FramePresented` fires on the emulation thread after each frame.

A `RawFrame` is the visible area: 384 x 272 from the VIC-II (the 320 x 200 screen with its border), 768 x 272 from
the C128's VDC. A C128 can switch between the two at any time (the 40/80 key, `Display`), so size your texture from
`frame.Width` / `frame.Height`. `CopyTo` writes it in the format you ask for, honouring your row pitch:

| `FramePixelFormat` | Bytes | Vulkan | Also |
|---|---|---|---|
| `Bgra8888` | B, G, R, A | `VK_FORMAT_B8G8R8A8_UNORM` / `_SRGB` | Direct3D `B8G8R8A8_UNORM`, Avalonia `Bgra8888`, SDL `ARGB8888`. A plain copy (fastest). |
| `Rgba8888` | R, G, B, A | `VK_FORMAT_R8G8B8A8_UNORM` / `_SRGB` | OpenGL `GL_RGBA`, WebGPU `rgba8unorm`, Avalonia `Rgba8888` |
| `Indexed8` | colour number 0-15 | `VK_FORMAT_R8_UINT` | Look the colour up in a shader (`frame.Palette`): palette swaps, CRT/PAL filters on the real colour numbers |

Alpha is always 255. The colours are sRGB values: use the `_SRGB` format when you sample into an sRGB swapchain,
`_UNORM` when the swapchain is `_UNORM`. Sample with nearest filtering (`VK_FILTER_NEAREST`) to keep the pixels sharp.
`flipVertically: true` writes the bottom row first, for OpenGL-style bottom-up textures.

### Vulkan

Keep a host-visible staging buffer of at least 768 x 272 x 4 bytes mapped and copy each new frame into it:

```csharp
if (c64.Video.TryAcquire(out var frame))
{
    if (frame.Width != imageWidth || frame.Height != imageHeight)
        RecreateImage(frame.Width, frame.Height);              // 384 x 272, or 768 x 272 for the VDC

    int pitch = frame.GetRowBytes(FramePixelFormat.Bgra8888);  // tightly packed rows
    frame.CopyTo((IntPtr)stagingMapped, stagingSize, pitch, FramePixelFormat.Bgra8888);

    // Then record: image layout UNDEFINED -> TRANSFER_DST_OPTIMAL, vkCmdCopyBufferToImage (bufferRowLength = 0),
    // TRANSFER_DST_OPTIMAL -> SHADER_READ_ONLY_OPTIMAL, and draw the image with a nearest-filtering sampler.
}
```

With a linear-tiling image that you map directly, pass `VkSubresourceLayout.rowPitch` as the pitch instead.

Indexed output with the palette in a uniform buffer (the 16 entries of `frame.Palette` as `vec4`s):

```glsl
layout(set = 0, binding = 0) uniform usampler2D screen;           // VK_FORMAT_R8_UINT, nearest
layout(set = 0, binding = 1) uniform Palette { vec4 colors[16]; };
layout(location = 0) in vec2 uv;
layout(location = 0) out vec4 color;
void main() { color = colors[texture(screen, uv).r]; }
```

### Avalonia

No Avalonia reference is needed: a locked `WriteableBitmap` is just an address, a row pitch and a format.

```csharp
var bitmap = new WriteableBitmap(new PixelSize(384, 272), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
image.Source = bitmap;
RenderOptions.SetBitmapInterpolationMode(image, BitmapInterpolationMode.None);   // sharp pixels

c64.Video.FramePresented += () => Dispatcher.UIThread.Post(() =>
{
    if (!c64.Video.TryAcquire(out var frame)) return;
    if (bitmap.PixelSize.Width != frame.Width || bitmap.PixelSize.Height != frame.Height)
    {
        bitmap = new WriteableBitmap(new PixelSize(frame.Width, frame.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        image.Source = bitmap;
    }
    using (var fb = bitmap.Lock())
        frame.CopyTo(fb.Address, fb.RowBytes * fb.Size.Height, fb.RowBytes,
            fb.Format == PixelFormat.Rgba8888 ? FramePixelFormat.Rgba8888 : FramePixelFormat.Bgra8888);
    image.InvalidateVisual();
});
c64.Start();

// Input: Avalonia's PhysicalKey names are the W3C codes the key bindings use.
window.KeyDown += (_, e) => c64.KeyDown(AvaloniaKeyCodes.ToWebCode(e.PhysicalKey.ToString()));
window.KeyUp += (_, e) => c64.KeyUp(AvaloniaKeyCodes.ToWebCode(e.PhysicalKey.ToString()));
window.Deactivated += (_, _) => c64.ReleaseAllInput();
```

## The sound

The SID is resampled to `EmbeddedEmulatorOptions.SampleRate` (default 48000 Hz; use your device's rate) and comes out
of `Audio` as mono, copied to as many interleaved channels as you ask for, as `float` (-1..1) or `short`:

* `Audio.Fill(buffer, channels)` for callbacks that must return a full buffer (miniaudio, SDL, PortAudio, NAudio):
  what is missing becomes silence and is counted in `Audio.Underruns`.
* `Audio.Read(buffer, channels)` for queues you push to (OpenAL, XAudio2, `SDL_QueueAudio`): only what has been
  produced, returns the number of sample frames.

Latency stays bounded: audio that arrives when more than `AudioMaxLatencySeconds` (0.15 s) is queued is dropped
(`Audio.DroppedSamples`), and audio that finds the queue empty gets `AudioPrimeSeconds` (0.06 s) of silence in front
so frame-to-frame jitter does not cause clicks. `Audio.BufferedSeconds` is the current latency.

## Input

`KeyDown(code)` / `KeyUp(code)` take W3C `KeyboardEvent.code` names ("KeyA", "Digit1", "Enter", "ArrowUp",
"Numpad8") and go through `Bindings`: letters, digits and punctuation where a PC keyboard has them, Escape =
RUN/STOP, Tab = C=, the cursor keys, F1-F8, the numeric keypad = joystick 2 (8/2/4/6, 0 or 5 = fire), Page Up =
RESTORE. On a C128, Escape, Tab, the keypad and the cursor keys are its own extra keys instead and End is RUN/STOP.
Translations for common windowing layers, none of which is referenced:

* `AvaloniaKeyCodes.ToWebCode(e.PhysicalKey.ToString())`
* `GlfwKeyCodes.ToWebCode((int)key)` for GLFW key numbers, which are also Silk.NET's `Key`, OpenTK's `Keys` and
  Raylib's `KeyboardKey`: `keyboard.KeyDown += (_, key, _) => c64.KeyDown(GlfwKeyCodes.ToWebCode((int)key));`

`SetJoystick(port, input, pressed)` drives a joystick from a game pad, `SetKey(C64Key, pressed)` presses a C64 key
directly (on-screen keyboards), `TypeText("LOAD\"*\",8,1\n")` types through the KERNAL. Call `ReleaseAllInput()`
when your window loses focus. Keys bound to host commands (F11 = reset, Pause, ...) raise `Command`; what they do is
up to you.

## Talking to programs on the machine

.NET code joins the machine the way hardware does: through the expansion port. Derive from `ExpansionDevice`, plug it
in with `AttachExpansion`, and 6502 programs (or BASIC's `PEEK` and `POKE`) talk to it like any cartridge:

* **Registers** in I/O 1 ($DE00-$DEFF) and I/O 2 ($DF00-$DFFF): `ReadIo` and `WriteIo` are called inside the CPU's bus
  cycle, with the full address. Return `null` from `ReadIo` for addresses you do not use (open bus).
* **ROM windows**: `MapRomL` / `MapRomH` put 8K of a byte array at $8000 (ROML) and $A000 (ROMH, or $E000 in Ultimax
  mode), wherever `SetLines` tells the PLA to show them. The CPU reads your array directly, so changes show at once.
* **Interrupts**: set `Irq` or `Nmi` to interrupt the program.
* **Memory**: `Machine.Ram` is the very array the CPU and the VIC read (64K; 128K on a C128, bank 1 from $10000), so a
  device can put its results straight into the machine's memory.

A device with a command register that answers at once, and a command that takes time and raises an IRQ when done:

```csharp
using System.Text;
using Tedd.Commodore.Embedding;
using Tedd.Commodore.Emulator.C64;

/// <summary>
/// $DE00 write: command. 1 = write the host's time ("12:34:56") at the buffer, at once.
///                       2 = fetch something slow; bit 7 of the status is set until it is there, then an IRQ.
/// $DE01/$DE02 write: buffer address, low/high byte.
/// $DE03 read: status (bit 7 = busy). Reading it acknowledges the IRQ.
/// </summary>
sealed class HostLink : ExpansionDevice
{
    private readonly EmbeddedEmulator _emulator;
    private readonly byte[] _ram;      // the machine's own RAM
    private ushort _buffer;
    private byte _status;

    public HostLink(EmbeddedEmulator emulator) : base("host link")
    {
        _emulator = emulator;
        _ram = emulator.Invoke(s => s.Machine.Ram);
    }

    public override byte? PeekIo(ushort address) => address == 0xDE03 ? _status : null;

    public override byte? ReadIo(ushort address)
    {
        if (address == 0xDE03) Irq = false;
        return PeekIo(address);
    }

    public override void WriteIo(ushort address, byte value)
    {
        switch (address)
        {
            case 0xDE01: _buffer = (ushort)((_buffer & 0xFF00) | value); break;
            case 0xDE02: _buffer = (ushort)((_buffer & 0x00FF) | (value << 8)); break;
            case 0xDE00 when value == 1:
                Store(Encoding.ASCII.GetBytes(DateTime.Now.ToString("HH:mm:ss")));
                break;
            case 0xDE00 when value == 2 && _status == 0:
                _status = 0x80;
                ushort buffer = _buffer;
                Task.Run(async () =>
                {
                    byte[] answer = await FetchAsync();          // never block the emulation thread
                    _emulator.Invoke(_ =>                        // back in the machine, between two frames
                    {
                        Store(answer, buffer);
                        _status = 0;
                        Irq = true;
                    });
                });
                break;
        }
    }

    public override void Reset(bool hard)
    {
        _status = 0;
        Irq = false;
    }

    private void Store(byte[] data, int? at = null)
    {
        int address = at ?? _buffer;
        for (int i = 0; i < data.Length; i++)
            _ram[(address + i) & 0xFFFF] = data[i];
    }

    private static async Task<byte[]> FetchAsync()
    {
        await Task.Delay(500);
        return Encoding.ASCII.GetBytes("HELLO FROM .NET");
    }
}
```

```csharp
c64.AttachExpansion(new HostLink(c64));   // registers only: it can go in while a program runs
```

From BASIC the time lands on the screen, whose memory starts at 1024 (digits and the colon are the same codes in
ASCII, PETSCII and screen codes):

```basic
POKE 56833,0:POKE 56834,4:POKE 56832,1
```

From assembly, including the slow command. Its IRQ arrives on the KERNAL's IRQ vector like the timer interrupt, so
the handler acknowledges the device and carries on into the KERNAL's own handler:

```asm
        lda #<buffer
        sta $DE01
        lda #>buffer
        sta $DE02
        lda #1
        sta $DE00       ; the time is in the buffer when this instruction ends

        sei
        lda #<irq       ; hook the KERNAL's IRQ vector
        sta $0314
        lda #>irq
        sta $0315
        cli
        lda #2
        sta $DE00       ; start the fetch; bit 7 of $DE03 is set until the answer is in the buffer
        ...
irq:    lda $DE03       ; acknowledge the host link (and see whether the answer is in)
        jmp $EA31       ; the KERNAL's IRQ handler (keyboard, clock, the timer's acknowledge)
```

A ROM window: 64K of .NET memory the 6502 sees at $8000-$9FFF, 8K at a time.

```csharp
sealed class BankedRom : ExpansionDevice
{
    public readonly byte[] Data = new byte[8 * WindowSize];   // write to it at any time; the CPU sees it at once

    public BankedRom() : base("banked ROM")
    {
        MapRomL(Data);
        SetLines(exrom: false, game: true);    // the 8K configuration: ROML at $8000, BASIC and KERNAL stay
    }

    public override void WriteIo(ushort address, byte value)
    {
        if (address == 0xDE00) MapRomL(Data, (value & 7) * WindowSize);   // bank select
    }

    public override void Reset(bool hard) => MapRomL(Data);
}
```

```csharp
c64.AttachExpansion(new BankedRom(), reset: true);   // then from BASIC: POKE 56832,5:PRINT PEEK(32768)
```

Plug a device that maps ROM in with `reset: true`: at boot the KERNAL looks for a cartridge start signature at $8004
and sizes the RAM BASIC may use (30719 bytes free with ROML in place), and a program that was already running could
have data where ROML now shows. A device with registers only needs no reset.

The rules: the device is called on the emulation thread with the emulator's lock held, so a handler must return
quickly, must never block or wait for another thread, and must not call the `EmbeddedEmulator` (the lock is already
held). Slow work goes to a task whose result comes back through `Invoke`, as above. Change the device's lines,
windows and `Irq`/`Nmi` only from its handlers, its `Reset`, or inside `Invoke`. `DetachCartridge()` or
`AttachExpansion(null)` unplugs it. docs/ARCHITECTURE.md ("Expansion port") has the details of the timing and the
memory map.

## Threading

Everything on `EmbeddedEmulator` may be called from any thread: frames run under one lock that input, media and
`Invoke` take as well, so a call waits at most for the frame in progress. The two consumers are lock free and
never wait: `Video.TryAcquire` belongs to one rendering thread, `Audio.Fill`/`Read` to one audio thread.
`Invoke(s => ...)` gives exclusive access to the whole `EmulatorSession` (memory, chips, drive) for anything this
class does not wrap: `c64.Invoke(s => s.Machine.GetScreenText())`. Expansion port devices run inside the frames,
under the same lock (see above).

## ROMs

The Commodore ROMs are copyrighted and not part of the package. `RomSet.Load(directory)` / `C128RomSet.Load(directory)`
find them by their usual file names (`RomSet.TryLoadDefault()` also looks at the `C64_ROMS` environment variable),
`EmbeddedEmulator.CreateC64(basic, kernal, chargen, drive1541)` takes them as byte arrays (from your asset system,
say). Without the 1541 ROM there is no disk drive; programs (`.prg`, `.t64`) and cartridges still work. The
repository's README lists the files and sizes.

## Without the facade

`VideoOutput` and `AudioOutput` are ordinary `IVideoSink`/`IAudioSink`s for an `EmulatorSession` you drive yourself,
and `new RawFrame(videoFrame)` converts the emulator's own raster inside your own `IVideoSink` without a copy (valid
until the sink returns).
