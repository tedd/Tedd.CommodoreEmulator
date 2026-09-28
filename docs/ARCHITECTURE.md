# Tedd.CommodoreEmulator architecture

Cycle-exact Commodore 64 emulator written in C# (.NET 8). Everything is clocked one system cycle at a
time; there are no "execute N cycles" shortcuts anywhere in the core. The same 6502 core drives the C64's
6510 and the 1541 drive's 6502.

```
src/Tedd.Commodore.Emulator   class library, no UI dependencies, no NuGet dependencies
src/Tedd.Commodore.Tests      NUnit tests
src/Tedd.Commodore.GUI        WPF front-end (Windows only)
```

ROM images, disk (.D64) and tape (.T64) images live in `src/Tedd.Commodore.GUI/` and are git-ignored.

## Conventions

* Namespaces: `Tedd.Commodore.Emulator.<Area>`; folders mirror namespaces.
* Every chip is a plain class with `Read(int reg)`, `Peek(int reg)` (no side effects, for debuggers),
  `Write(int reg, byte value)`, `Clock()` (exactly one cycle), `Reset()`.
* Register indexes passed to chips are already masked to the chip's register count; mirrors are handled by
  the memory map, not the chip.
* Signal lines are `bool` properties/fields where `true` means *asserted* (electrically low for the
  active-low lines IRQ, NMI, BA, ATN, CLK, DATA). Exception: port pin levels are raw voltages (`1` = high).
* No allocations in per-cycle paths. No LINQ in hot paths. `unsafe` is allowed but not required.
* Comments cite the source document for timing claims (64doc.txt, vic-ii.txt, CIA/VIA data sheets,
  "A Software Model of the CIA6526" by Wolfgang Lorenz, reSID).
* Public API is documented with XML comments. Tests use NUnit 4 (`Assert.That`).

## Bus / clock primitives — `Tedd.Commodore.Emulator.Bus`

```csharp
public interface IBus { byte Read(ushort address); void Write(ushort address, byte value); }
public interface IClockable { void Clock(); }
```

## CPU — `Tedd.Commodore.Emulator.Cpu.Cpu6502` (done, verified)

```csharp
public sealed class Cpu6502
{
    public Cpu6502(IBus bus);
    public byte A, X, Y, S; public ushort PC; public byte P;         // P: bit5 always 1, bit4 always 0 after PLP/RTI
    public bool FlagCarry, FlagZero, FlagInterrupt, FlagDecimal, FlagOverflow, FlagNegative;
    public bool Irq;              // level input, true = asserted
    public bool Nmi;              // edge input (false -> true triggers)
    public bool Rdy = true;       // false = halt on next read cycle (writes still run)
    public void SetOverflow();    // SO pin
    public void Clock();          // exactly one bus cycle
    public int  Step();           // run to next instruction boundary
    public void Reset();          // immediate: PC from $FFFC, S=$FD, I=1
    public bool AtInstructionBoundary { get; }
    public bool Jammed { get; }
    public long Cycles, StallCycles;
    public static OpcodeInfo GetOpcodeInfo(byte opcode);
}
```

Interrupt polling: lines are sampled at the end of every cycle; the poll at the end of an instruction uses
the sample from the previous cycle (two cycles back for a taken branch without page crossing). This gives
the documented SEI/CLI/PLP one-instruction delay and the branch delay.

## Machine timing model

One C64 system cycle (PAL: 985 248 Hz, 63 cycles per raster line, 312 lines per frame):

```
vic.Clock();                 // φ1 half: VIC memory access, BA/AEC, IRQ; renders 8 pixels
cpu.Rdy = !vic.Ba;
cpu.Irq = vic.Irq || cia1.IrqLine || expansion.Irq;
cpu.Nmi = cia2.IrqLine || restoreKeyPressed || expansion.Nmi;
cpu.Clock();                 // φ2 half: CPU bus access (stalls when Rdy is false)
cia1.Clock(); cia2.Clock(); sid.Clock();
drive.ClockRatio();          // 1541 runs at 1 000 000 Hz; fractional accumulator keeps the ratio exact
```

## C64 memory map — `Tedd.Commodore.Emulator.C64.C64Memory : IBus, IVicMemory`

Implements the PLA. Inputs: 6510 port bits LORAM/HIRAM/CHAREN (with pull-ups when configured as inputs),
cartridge lines EXROM/GAME. Address decoding per the standard table (c64-wiki "Bank Switching"):

| LORAM HIRAM CHAREN | $8000 | $A000 | $D000 | $E000 |  (GAME=1 EXROM=1, no cartridge)
|---|---|---|---|---|
| 0 0 x | RAM | RAM | RAM | RAM |
| 0 1 0 | RAM | RAM | CHAR | KERNAL |
| 0 1 1 | RAM | RAM | I/O | KERNAL |
| 1 0 0 | RAM | RAM | RAM | RAM |
| 1 0 1 | RAM | RAM | I/O | RAM |
| 1 1 0 | RAM | BASIC | CHAR | KERNAL |
| 1 1 1 | RAM | BASIC | I/O | KERNAL |

8K cartridge (EXROM=0, GAME=1): ROML at $8000 when LORAM=HIRAM=1. 16K (EXROM=0, GAME=0): ROML at $8000
and ROMH at $A000 when HIRAM=1 (ROML additionally needs LORAM=1). Ultimax (EXROM=1, GAME=0): $1000-$7FFF
and $A000-$CFFF open (reads return the last value seen on the bus), ROML at $8000, I/O at $D000, ROMH at
$E000, regardless of the 6510 port.

Writes always go to RAM (write-through under ROM) except the I/O area when I/O is mapped, and open areas
in Ultimax mode.

I/O area ($D000-$DFFF when mapped):

| Range | Device |
|---|---|
| $D000-$D3FF | VIC-II, 64 registers mirrored every $40 |
| $D400-$D7FF | SID, 32 registers mirrored every $20 |
| $D800-$DBFF | Color RAM (1K x 4 bit; upper nibble reads as the last bus value) |
| $DC00-$DCFF | CIA 1, 16 registers mirrored |
| $DD00-$DDFF | CIA 2, 16 registers mirrored |
| $DE00-$DEFF | I/O 1 (the expansion port device; open bus without one) |
| $DF00-$DFFF | I/O 2 (the expansion port device; open bus without one) |

Unmapped I/O reads return the last value seen on the data bus (VICE "vicii_read_phi1" approximation:
the byte the VIC fetched in the φ1 half of the same cycle). `C64Memory` keeps `LastBusValue`.

VIC memory view (`IVicMemory`): 14-bit address + 2 bank bits from CIA2 port A (inverted: PA0-1 = 11 → bank 0
= $0000). Character ROM appears at $1000-$1FFF of banks 0 and 2. In Ultimax mode ROMH appears at
$3000-$3FFF of every bank.

```csharp
public interface IVicMemory
{
    byte ReadVic(int address14);   // 0..$3FFF inside the currently selected VIC bank
    byte ReadColor(int address10); // 0..$3FF, returns 0..15
    byte PeekVic(int address14);   // the same read without leaving the value on the bus (debuggers)
}
```

## Expansion port — `Tedd.Commodore.Emulator.C64.ExpansionDevice`

Whatever sits in the cartridge port of the C64 or the C128 derives from `ExpansionDevice`; the ROM cartridge
(`Cartridge`, raw and .CRT images) is one of them. It is also how .NET code talks to programs running on the machine:
a device answers the CPU's accesses to I/O 1 and I/O 2 from .NET, maps .NET byte arrays in as ROML/ROMH, and drives the
interrupt lines. Real hardware works the same way (REUs, network and command-interface cartridges), so the 6502 side
is ordinary register programming.

```csharp
public abstract class ExpansionDevice
{
    protected ExpansionDevice(string name);  string Name;  const int WindowSize = 8192;
    bool Exrom, Game, IsUltimax;                             // line levels: true = high, the level with nothing plugged in
    byte[]? RomL; int RomLOffset; byte[]? RomH; int RomHOffset;   // the 8K windows; null = nothing drives the bus there
    bool Irq { get; protected set; }  bool Nmi { get; protected set; }   // true = asserted
    protected void SetLines(bool exrom, bool game);          // the memory map follows at once
    protected void MapRomL(byte[]? data, int offset = 0);    // point a window into an array (bank switching)
    protected void MapRomH(byte[]? data, int offset = 0);
    virtual byte? ReadIo(ushort address);                    // $DE00-$DFFF, full address; null = open bus
    virtual byte? PeekIo(ushort address);                    // the same without side effects (debuggers); default open bus
    virtual void WriteIo(ushort address, byte value);
    virtual void Reset(bool hard);                           // the reset line (hard = power cycle)
}
public sealed class Cartridge : ExpansionDevice;             // fixed ROML/ROMH and lines from a raw or .CRT image

CommodoreMachine: ExpansionDevice? Expansion; void AttachExpansion(ExpansionDevice? device); void AttachCartridge(Cartridge?);
C64Memory, C128Memory: ExpansionDevice? Expansion { get; set; }
```

* **Memory map.** The PLA takes EXROM/GAME and the two windows from the device whenever the map is computed (the
  table in "C64 memory map"; on the C128 also the MCR bits 4-5 and the external function ROM, see "Memory" under the
  C128). A device may call `SetLines`, `MapRomL` and `MapRomH` at any time, typically from a bank register in
  `WriteIo`; the memory it is plugged into rebuilds its map at once, so the next bus cycle sees the new
  configuration (on the C128 that rebuilds the page tables, a few microseconds of host time).
* **ROM windows** are read straight from the device's array: no copy and no call per access, and a device changes
  what the CPU reads by writing into its own array. Where the PLA selects a window that has no array, the CPU reads
  open bus. CPU writes to ROML/ROMH addresses go to the RAM underneath, as before. The VIC sees ROMH only in Ultimax
  mode ($3000-$3FFF of every bank, from the upper 4K of the window).
* **I/O 1 / I/O 2** ($DE00-$DEFF / $DF00-$DFFF) reach the device only while the I/O area is mapped, with the full
  address. `null` from `ReadIo` reads as the last value on the bus, as with an empty port. `Peek` (debuggers, memory
  viewers) calls `PeekIo`, never `ReadIo`. On the C128 the Z80's IN/OUT to $DExx/$DFxx reach the device too.
* **Timing.** A handler runs inside the bus cycle that makes the access and takes no emulated time. The machine ORs
  the device's `Irq`/`Nmi` into the CPU's lines at the start of every cycle's CPU half, so a line the device changes
  during a CPU access is on the CPU's pin from the next cycle; from there the CPU's own interrupt timing applies (IRQ
  is a level, NMI an edge of the combined line).
* **Reset.** `Reset(hard)` of the machine calls the device's `Reset(hard)` first and then rebuilds the map, so lines
  the device releases on reset are honoured by the boot. Plugging a device in does not reset anything: a device with a
  ROM that should start (or that takes memory the KERNAL sizes at boot) needs a reset as well, one with only I/O
  registers can go in while a program runs.
* **Threading.** Everything happens on the emulation thread; handlers must return quickly and never block. Slow work
  (network, files) goes to another thread, and its result comes back through the host's `Invoke` (between frames):
  store the result, clear a busy bit, assert `Irq`. For "DMA" a device writes the machine's RAM directly
  (`CommodoreMachine.Ram`, the array the CPU and VIC read).
* A device with state belongs in one machine. A stateless one (`Cartridge`) can be plugged into several at once: the
  memories subscribe to the device's changes through a multicast delegate.

Not modelled: DMA with BA (a device cannot stall the CPU), the dot clock and φ2 on the port, and per-cycle clocking of
a device (it only runs when it is accessed or reset).

## VIC-II — `Tedd.Commodore.Emulator.Video.VicII` (PAL 6569)

Cycle-exact per Christian Bauer's "The MOS 6567/6569 video controller (VIC-II) and its application in
the Commodore 64" (vic-ii.txt). 63 cycles per line, 312 lines.

```csharp
public sealed class VicII : IClockable
{
    public VicII(IVicMemory memory);
    public byte Read(int reg); public byte Peek(int reg); public void Write(int reg, byte value); // reg 0..63
    public void Clock();                     // one system cycle (φ1 VIC access + 8 pixels)
    public void Reset();
    public bool Ba { get; }                  // true = BA asserted (CPU must stop on next read)
    public bool Aec { get; }                 // true = VIC owns the bus (3 cycles after Ba)
    public bool Irq { get; }                 // true = IRQ asserted
    public int RasterLine { get; }           // 0..311
    public int RasterCycle { get; }          // 1..63
    public long FrameCount { get; }
    public uint[] Frame { get; }             // ARGB, FrameWidth * FrameHeight, one full raster line = 504 px
    public const int FrameWidth = 504, FrameHeight = 312;
    public const int CyclesPerLine = 63, LinesPerFrame = 312;
    public static readonly uint[] Palette;   // 16 ARGB entries (Pepto PAL)
    public event Action? FrameCompleted;     // raised at the end of the last cycle of line 311
    public void SetLightPen(bool asserted);  // optional
}
```

Frame buffer pixel `x = (cycle - 1) * 8 + pixel`, `y = raster line`. The VIC renders the *entire* raster
line including blanking; the GUI crops to the visible PAL area. Sprites, all 5 graphics modes + 3 invalid
modes, border unit (38/40 columns, 24/25 rows), badlines (with DEN latch in line $30), idle state,
raster IRQ (line 0 fires one cycle later), sprite/sprite and sprite/background collisions, sprite DMA
timing (cycle 55/56 enable check, cycle 58 display check, MCBASE update in cycle 15/16), sprite
priorities, X/Y expansion, multicolor sprites, XSCROLL/YSCROLL, register read-back rules ($D011/$D012
return the current raster, unused bits read 1, $D01E/$D01F cleared on read, $D019 acknowledge by writing 1).

Per-line VIC φ1 access schedule (6569):

| Cycle | Access |
|---|---|
| 1 | sprite 3 pointer (p), then data (s) at 1φ2, 2φ1, 2φ2 |
| 3,5,7,9 | sprite 4,5,6,7 pointer + data |
| 11-15 | DRAM refresh |
| 15-54 (φ2) | c-accesses (video matrix + color RAM) on bad lines |
| 16-55 (φ1) | g-accesses (character generator / bitmap) |
| 55-56 | sprite DMA enable check (Y compare); MC/MCBASE handling |
| 58 | sprite 0 pointer + data; display state check (RC=7 → idle) |
| 60,62 | sprite 1, 2 pointer + data |

BA goes low three cycles before the first access the VIC needs the bus for (cycle 12 on a badline,
three cycles before a sprite's pointer fetch when that sprite's DMA is on).

## CIA 6526 — `Tedd.Commodore.Emulator.IO.Cia6526`

```csharp
public sealed class Cia6526 : IClockable
{
    public Cia6526(string name);
    public byte Read(int reg); public byte Peek(int reg); public void Write(int reg, byte value); // reg 0..15
    public void Clock(); public void TodTick(); public void Reset();
    public Func<byte>? PortAInput;   // external pin levels, 1 = high; default 0xFF
    public Func<byte>? PortBInput;
    public byte PortAOutput { get; } // PRA | ~DDRA   (what the chip drives; inputs float high)
    public byte PortBOutput { get; } // PRB | ~DDRB, with timer PB6/PB7 output overlaid when enabled
    public event Action? PortAChanged; // after any write that can change PortAOutput
    public event Action? PortBChanged;
    public bool Flag { set; }        // FLAG pin; a true->false transition sets ICR bit 4
    public bool IrqLine { get; }     // true = /IRQ asserted
    public bool Model6526A { get; set; } // false = old 6526 (IRQ one cycle after ICR bit), true = 6526A
}
```

Reading PRA/PRB returns `PortXOutput & PortXInput()`. Timer model follows Lorenz's software model:
the first decrement happens two cycles after the start bit is written; underflow sets the ICR bit in the
same cycle, reloads from the latch (new value visible the next cycle), stops the timer in one-shot mode,
and the IRQ line follows one cycle later on the old 6526. Timer B can count timer A underflows (CRB bits
5-6). TOD: BCD tenths/seconds/minutes/hours with AM/PM, halt on hours write until tenths write, latch on
hours read, alarm (CRB bit 7 selects alarm write), 50/60 Hz divider selected by CRA bit 7. Serial shift
register: output mode shifts on timer A underflow (8 bits then ICR bit 3); input mode minimal.
ICR: reading returns pending bits with bit 7 set if any enabled bit is pending, then clears them; writing
with bit 7 set sets mask bits, with bit 7 clear clears them.

## VIA 6522 — `Tedd.Commodore.Emulator.IO.Via6522`

```csharp
public sealed class Via6522 : IClockable
{
    public Via6522(string name);
    public byte Read(int reg); public byte Peek(int reg); public void Write(int reg, byte value); // reg 0..15
    public void Clock(); public void Reset();
    public Func<byte>? PortAInput; public Func<byte>? PortBInput;   // pin levels, default 0xFF
    public byte PortAOutput { get; } public byte PortBOutput { get; } // ORx | ~DDRx (T1 PB7 overlaid when enabled)
    public event Action? PortAChanged; public event Action? PortBChanged;
    public bool Ca1 { get; set; } public bool Ca2 { get; set; } public bool Cb1 { get; set; } public bool Cb2 { get; set; } // input levels (raw)
    public bool Ca2Output { get; } public bool Cb2Output { get; }   // when PCR configures them as outputs
    public event Action? Ca2Changed; public event Action? Cb2Changed;
    public bool IrqLine { get; }
}
```

Registers: 0 ORB/IRB, 1 ORA/IRA (with handshake), 2 DDRB, 3 DDRA, 4 T1C-L, 5 T1C-H, 6 T1L-L, 7 T1L-H,
8 T2C-L, 9 T2C-H, 10 SR, 11 ACR, 12 PCR, 13 IFR, 14 IER, 15 ORA (no handshake). Timer 1: one-shot /
free-running with reload from latch, optional PB7 output; interval is N+2 cycles in free-running mode.
Timer 2: one-shot or PB6 pulse counting. CA1/CB1 edge interrupts with polarity from PCR, CA2/CB2 input
edge / independent modes, handshake and pulse output modes, manual output. Reading/writing ORA/ORB
clears the corresponding CA/CB interrupt flags as documented. IFR bit 7 = any enabled flag set.

## SID 6581 — `Tedd.Commodore.Emulator.Audio.Sid6581`

```csharp
public sealed class Sid6581 : IClockable
{
    public Sid6581();
    public byte Read(int reg); public byte Peek(int reg); public void Write(int reg, byte value); // reg 0..31
    public void Clock(); public void Reset();
    public float Output { get; }         // mixed, filtered, volume-scaled output after the last Clock(), range about -1..1
    public byte PotX { get; set; } public byte PotY { get; set; } // paddle inputs (default 0xFF)
}
public sealed class SidResampler
{
    public SidResampler(Sid6581 sid, int sampleRate, double clockFrequency = 985248.0);
    public void Clock();                                  // call once per system cycle; runs sid.Clock()
    public int Read(Span<short> destination);             // drain resampled 16-bit mono PCM
    public int Available { get; }
}
```

Voices per reSID: 24-bit phase accumulator, triangle/saw/pulse/noise (23-bit LFSR clocked on accumulator
bit 19 rising edge, output bits 20,18,14,11,9,5,2,0), combined waveforms approximated with AND,
oscillator sync and ring modulation, test bit. Envelope: ADSR rate table (period values 9, 32, 63, 95, 149,
220, 267, 313, 392, 977, 1954, 3126, 3907, 11720, 19532, 31251), exponential counter for decay/release
(1/2/4/8/16/30 steps at levels $FF/$5D/$36/$1A/$0E/$06), gate handling. Filter: two-integrator-loop
state-variable filter, LP/BP/HP mixing, per-voice routing, "3 OFF", cutoff from the 11-bit value using the
6581 curve, resonance to Q mapping; master volume. Reads: $19/$1A POTX/POTY, $1B OSC3 (upper 8 bits of
voice 3 waveform), $1C ENV3; write-only registers read as the last written bus value (return 0).

## IEC serial bus — `Tedd.Commodore.Emulator.IO.IecBus`

Open-collector lines ATN, CLK, DATA: a line is *low* (asserted) if any participant pulls it low.

```csharp
public sealed class IecBus
{
    public bool AtnLow { get; } public bool ClkLow { get; } public bool DataLow { get; }
    public IecPort Attach(string name);          // one port per participant
    public event Action? Changed;                // any line changed
}
public sealed class IecPort
{
    public bool PullAtn { get; set; } public bool PullClk { get; set; } public bool PullData { get; set; }
    public IecBus Bus { get; }
}
```

C64 side (CIA2 PA): PA3=1 pulls ATN, PA4=1 pulls CLK, PA5=1 pulls DATA; PA6 reads 1 when CLK is *high*
(released), PA7 reads 1 when DATA is high. 1541 side (VIA1 PB): PB0 reads 1 when DATA is *low*, PB2 reads
1 when CLK is low, PB7 reads 1 when ATN is low; PB1=1 pulls DATA, PB3=1 pulls CLK, PB4 = ATNA. Hardware
auto-acknowledge: DATA is also pulled low whenever `AtnLow != ATNA`. ATN in also drives VIA1 CA1.

## 1541 drive — `Tedd.Commodore.Emulator.Drive`

* `Drive1541`: `Cpu6502` + `DriveMemory` (2K RAM at $0000-$07FF mirrored through $1FFF; VIA1 at $1800,
  VIA2 at $1C00 (each mirrored in its $400 window); 16K ROM at $C000-$FFFF) + two `Via6522` + `DiskUnit`.
  `Clock()` = one 1 MHz drive cycle. `Attach(GcrDisk)`, `Detach()`, `DeviceNumber` (8-11, VIA1 PB5/PB6).
* `DiskUnit` (mechanics): 300 RPM spindle, 84 half-tracks, stepper (VIA2 PB0-1 phase pattern), motor
  (PB2), LED (PB3), write protect (PB4, 1 = write enabled), density (PB5-6 select the bit rate: 4 zones
  with 16/15/14/13 cycles per bit i.e. 32/30/28/26 cycles per byte... see below), SYNC (PB7 = 0 while
  10+ consecutive 1 bits are under the head), byte-ready → VIA2 CA1 negative edge and CPU SO (when SOE via
  CA2 is enabled), read data → VIA2 PA input, write via PA output when CB2 (R/W mode) = 0.
  Bit rates: zone 0 (tracks 31-35) 13 cycles/bit, zone 1 (25-30) 14, zone 2 (18-24) 15, zone 3 (1-17) 16
  — selected by PB5-6 (the ROM sets them from the track number, the mechanics only use PB5-6).
* `GcrDisk`: 84 half-tracks of raw bit streams. `FromD64(D64Image)` produces standard formatted tracks
  (sync, header, gap, sync, data, gap; GCR 4→5 encoding; track lengths 7692/7142/6666/6250 bytes for the
  four zones); `ToD64()` decodes back. Odd half-tracks are empty (unformatted).
* `D64Image`: sector access, directory listing, file read (chain following), BAM, disk name/ID; 35-track
  (174848 bytes, optionally + 683 error bytes) and 40-track variants.

## Tape / program files — `Tedd.Commodore.Emulator.Media`

* `T64Image`: parses the "C64 tape image file"/"C64S tape file" container; entries with name, C64 file
  type, start/end address, data. Works around the common wrong-end-address bug (use file size).
* `PrgFile`: 2-byte little-endian load address + data.
* Loading into the C64 is done by injection: after the KERNAL is ready, data is copied to RAM at the load
  address and BASIC pointers ($2B-$2E, $AE/$AF) are set, then `RUN` is typed via the keyboard buffer.

## Assembler — `Tedd.Commodore.Emulator.Tools.Assembler`

Two-pass 6502 assembler used by the tests to inject compiled programs.

```csharp
public sealed class AssemblyResult { public ushort Origin; public byte[] Bytes; public IReadOnlyDictionary<string, ushort> Labels; }
public static class Assembler { public static AssemblyResult Assemble(string source, ushort defaultOrigin = 0x1000); }
public sealed class AssemblerException : Exception { public int Line { get; } }
```

Syntax: `* = $C000` or `.org $C000`; labels `name:` (also `name` at line start followed by an instruction);
`sym = expr`; comments `;`; `.byte`/`.db`, `.word`/`.dw`, `.text "..."`, `.res n[, fill]`; operands with
`#imm`, `<expr` / `>expr` low/high byte, `*` current address, expressions with `+ - * / & | ^ << >> ( )`,
numbers `$hex %bin decimal 'c'`; all addressing modes incl. `(zp,X)`, `(zp),Y`, `(abs)`; zero page is
chosen automatically when the operand is known and < 256 and the mode exists; forward references assemble
as absolute; undocumented mnemonics (SLO RLA SRE RRA SAX LAX DCP ISC ANC ALR ARR ANE/XAA SBX/AXS LAS
SHA/AHX SHX SHY TAS/SHS JAM/KIL NOP-with-operand) supported.

## Sprite snapshot — `Tedd.Commodore.Emulator.Tools.SpriteSnapshot`

Decodes the eight VIC-II sprites for debuggers and viewers without disturbing the machine: registers through
`VicII.Peek` (so `$D01E`/`$D01F` are *not* cleared) and memory through `IVicMemory.PeekVic` (so nothing is left
on the bus). `Update` refills the same instance, so a viewer that refreshes several times per second allocates
nothing.

```csharp
public sealed class SpriteSnapshot
{
    public const int Width = 24, Height = 21, DataSize = 63;
    public const byte Transparent = 0xFF;
    public static SpriteSnapshot Capture(VicII vic, int vicBank = 0);
    public void Update(VicII vic, int vicBank = 0);
    public IReadOnlyList<SpriteInfo> Sprites { get; }   // also this[n]
    // $D015, $D017, $D01B-$D01F, $D021, $D025/$D026, video matrix, bank base, active DMA count
}

public sealed class SpriteInfo
{
    // MxX/MxY (also relative to the display window), color, multicolor/expansion/priority flags, collisions,
    // pointer and data address (inside the bank and as the CPU sees it), the 63 data bytes, and the sequencer
    // state: DMA on, display state, MC/MCBASE, Y expansion flip-flop, shift register.
    public void Render(Span<byte> pixels);   // 24 x 21 color indices, Transparent where nothing is drawn
    public int SolidPixelCount();
}
```

The sequencer state comes from `VicII.SpriteX/SpriteDma/SpriteDisplayed/SpriteExpansionFlipFlop/SpriteMc/`
`SpriteMcBase/SpritePointer/SpriteShiftRegister(int n)`, all side effect free. The WPF sprite viewer
(`SpriteViewerWindow`, `SpriteViews.cs`) is a thin layer on top: thumbnails, a zoomed pixel grid, where each
sprite sits relative to the display window, and the register/DMA text.

## Character set — `Tedd.Commodore.Emulator.Tools.CharacterSet`

An 8 x 8 character generator — a ROM image, a file the user picked, or the 2 KiB the VIC-II is reading right
now — together with the C64 facts about it. `FromVic` goes through `VicII.Peek` and `IVicMemory.PeekVic`, so
capturing never touches the bus; the instance owns a copy of the bytes, so it stays stable while the machine
runs on.

```csharp
public sealed class CharacterSet
{
    public const int CharacterWidth = 8, CharacterHeight = 8, BytesPerCharacter = 8;
    public const int CharactersPerSet = 256, SetSize = 2048, RomSize = 4096, LowercaseSetOffset = 2048;
    public const int CpuBase = 0xD000, VicShadowBase = 0x1000;

    public static CharacterSet FromRom(byte[] characterRom, string name);
    public static CharacterSet FromVic(VicII vic, int vicBank = 0);   // sets VicAddress, BankBase, IsRomShadow
    public static int CharacterBaseOf(byte d018);                     // $D018 bits 3-1, in 2 KiB steps
    public static byte D018For(int characterBase, byte d018);

    public ReadOnlySpan<byte> Glyph(int index);
    public void Render(int index, Span<byte> pixels, byte ink = 1, byte paper = 0);   // 8 x 8 color indices
    public int SolidPixelCount(int index);   public bool IsBlank(int index);
    public int InvertedMismatchCount(int setOffset);   // how much of $80-$FF is not the reverse of $00-$7F
    public uint Crc32 { get; }

    public static int ScreenCodeOf(int petscii);              // -1 for the two control code blocks
    public static ReadOnlySpan<int> PetsciiCodesFor(int screenCode);
    public static string Describe(int screenCode, bool lowercaseSet);
}
```

The PETSCII mapping is the KERNAL's: `$20-$3F` stay put, `$40-$5F` become `$00-$1F`, `$60-$7F` and `$C0-$DF`
both become `$40-$5F`, `$A0-$BF` and `$E0-$FE` both become `$60-$7F`/`$60-$7E`, `$FF` (pi) shares the glyph at
`$5E`, and `$00-$1F`/`$80-$9F` are control codes with no glyph. Screen codes `$80-$FF` are the reverse video
half and no PETSCII code prints them directly.

The WPF character set viewer (`CharsetViewerWindow`, `CharsetViews.cs`) is a thin layer on top: the glyph grid,
the codes and addresses of the selected character, and loading a character set from a file.
`RomSet.ReplaceChar(ReadOnlySpan<byte>, int offset)` writes it over the running machine's character ROM in
place — the array is the one `C64Memory` reads through, so the new glyphs appear from the next character fetch.

## PNG output — `Tedd.Commodore.Emulator.Tools.PngWriter`

`static byte[] Encode(int width, int height, ReadOnlySpan<uint> argb)` and
`static void Save(string path, int width, int height, ReadOnlySpan<uint> argb)`; zlib via
`System.IO.Compression.ZLibStream`, no other dependencies. Used by tests and the GUI screenshot feature.

## Hosting layer — `src/Tedd.Commodore.Hosting`

Everything a front-end needs, independent of UI framework. Front-ends (WPF, Blazor/WASM, SDL2, Unity) only
implement the two sink interfaces and translate their native key events to W3C `KeyboardEvent.code` names.

```csharp
public interface IVideoSink { void PresentFrame(in VideoFrame frame); }   // frame.CopyVisible/Rgba/Bgra helpers, 384x272
public interface IAudioSink { int SampleRate { get; } void Write(ReadOnlySpan<short> samples); void Clear(); }
public sealed class AudioTap : IAudioSink          // decorator keeping the latest samples for visualizers (CopyLatest)

public sealed class EmulatorSession                 // single-threaded; call RunFrame() from the host's loop
{
    EmulatorSession(RomSet roms, int sampleRate = 44100, bool attachDrive = true, KeyBindings? bindings = null);
    C64 Machine; KeyBindings Bindings; IVideoSink Video; IAudioSink Audio; bool Paused; bool Warp;
    void RunFrame(); void StepCycle(); void StepInstruction(); void Reset(bool hard);
    void KeyDown(string code); void KeyUp(string code); void SetJoystick(int port, JoystickInput input, bool pressed);
    void SetKey(C64Key key, bool pressed); void ReleaseAllInput(); void TypeText(string text);
    event Action<SystemCommand> Command;            // bound host commands (Reset, HardReset, Pause, Warp, Screenshot, MemoryViewer)
    void AttachDisk(byte[] d64, string name, bool autostart, bool writeProtected = false); void EjectDisk(); D64Image? SaveDisk();
    void AttachProgram(byte[] t64OrPrg, string name, int entryIndex = 0, bool run = true);
    void AttachCartridge(byte[] rawOrCrt, string name); void DetachCartridge();
    ExpansionDevice? Expansion; void AttachExpansion(ExpansionDevice? device, bool reset = false);   // see "Expansion port"
    void AttachAuto(byte[] data, string fileName, bool autostart = true);   // by extension / content
    string MediaDescription;
}
public sealed class EmulatorRunner : IDisposable    // paced thread for hosts without their own loop
{
    EmulatorRunner(EmulatorSession session); void Start(); bool Paused; bool Warp; double MeasuredFps;
    EmulatorRunner(EmulatorSession session, object syncRoot);   // frames and Invoke lock syncRoot (shared with the host)
    void Invoke(Action a); T Invoke<T>(Func<T> f);  // exclusive access to the machine (between frames / while frozen)
}
public sealed class KeyBindings                     // code -> InputAction, JSON { "KeyA": "key:A", "Numpad8": "joy2:up", "PageUp": "sys:restore" }
{
    static KeyBindings CreateDefault(); static KeyBindings FromJson(string); string ToJson(); Save/Load/LoadOrDefault(path);
    InputAction? Get(string code); bool TryGet(...); void Set(string code, InputAction a); bool Remove(string code); IEnumerable<string> CodesFor(InputAction a);
    event Action Changed; IReadOnlyDictionary<string, InputAction> All;
}
public readonly struct InputAction                  // ForKey(C64Key, shift) | ForJoystick(port 1|2, JoystickInput) | ForSystem(SystemCommand); Parse/ToString/Describe
public static class KeyCodes                        // All (W3C code names), Display(code)
```

Key naming: W3C `KeyboardEvent.code` ("KeyA", "Digit1", "Enter", "ShiftLeft", "ArrowUp", "Numpad8", "F1"...).
Browsers deliver these directly; WPF, SDL2 and Unity hosts keep a table from their native key enums.

## Tests — `src/Tedd.Commodore.Tests`

* `Support/RecordingBus` — 64K RAM that logs every bus access (`BusAccess(Address, Value, IsWrite)`).
* `Cpu/HarteSingleStepTests` — SingleStepTests vectors (needs `HARTE_6502_TESTS`).
* `Cpu/Opcode*Tests` — hand-written per-opcode tests: exact cycle traces per addressing mode (from
  64doc.txt), flag behaviour, page crossing, RMW double write, stack, interrupts.
* `Cpu/AssemblyProgramTests` — programs written in assembly, assembled and run.
* `Cpu/FunctionalTests` — Klaus Dormann's 6502 functional test (`TestData/6502_functional_test.bin`).
* Chip tests next to each chip; VIC tests write PNGs to `TestResults/` for inspection and compare against
  reference images in `TestData/Reference/`.
* System tests boot the real ROMs (located via `RomSet.Locate()`), so they are skipped when ROMs are
  missing.

## Machine base class — `Tedd.Commodore.Emulator.Machines.CommodoreMachine`

What the C64 and the C128 share, and what the hosting layer and the tools program against: the 6502-family CPU, VIC-II,
both CIAs, SID + resampler, keyboard, joysticks, IEC bus and the optional 1541; `Clock()` (abstract, one system cycle),
`RunFrame()`, `RunCycles()`, `StepInstruction()`, `Reset(hard)`, `AttachDrive()/DetachDrive()`, `Expansion`,
`AttachExpansion()`, `AttachCartridge()` (see "Expansion port"); memory access for tools (`PeekMemory`, `WriteMemory`,
`Ram`, `ColorRam`, `VicMemory`, `VicBank`, `ScreenAddress`); `GetScreenText()`, `IsBasicReady()`,
`WaitForBasicReady()`; typing through the KERNAL keyboard buffer (`TypeText`; buffer/count/size addresses are abstract:
C64 $0277/$C6/$0289, C128 $034A/$D0/$0A20 or the C64 ones in C64 mode), the disk autostart helper and `InjectProgram`
(BASIC pointers per machine: C64 $2D..$32/$AE, C128 TEXT_TOP $1210). `MachineModel { C64, C128 }`.
`C64 : CommodoreMachine` keeps its previous public API.

## Z80 — `Tedd.Commodore.Emulator.Cpu.Z80`

```csharp
public interface IZ80Bus : IBus { byte In(ushort port); void Out(ushort port, byte value); }
public sealed partial class Z80
{
    public Z80(IZ80Bus bus);
    public byte A, F, B, C, D, E, H, L, A2, F2, B2, C2, D2, E2, H2, L2, I, R; public ushort IX, IY, SP, PC, WZ; public byte Q;
    public bool Iff1, Iff2; public int InterruptMode; public bool Halted;
    public bool Irq;                 // /INT level
    public void SetNmi(bool level);  // /NMI, edge latched
    public int Step();               // one instruction (or interrupt acknowledge / HALT cycle); returns T-states
    public void Reset();             // PC = 0, I = R = 0, IFF off, IM 0, AF = SP = $FFFF
    public long Cycles, Instructions;
}
```

Instruction stepped (not T-state stepped): a machine keeps a T-state budget and calls `Step()` while it is
positive. Cycle counts are the documented ones incl. the conditional forms (JR/DJNZ/RET cc/CALL cc, block repeats
21/16). Complete instruction set incl. the undocumented IXH/IXL/IYH/IYL forms, SLL, IN F,(C), OUT (C),0, duplicate
NEG/RETN/IM opcodes and the DDCB/FDCB result-to-register forms; X/Y flag bits everywhere (CP from the operand, BIT
n,(HL) from WZ, block instructions from B / PC when repeating), WZ (MEMPTR) rules, the Q latch (F after a
flag-writing instruction, else 0; a DD/FD prefix clears it) for SCF/CCF, LD A,I/R with IFF2 in P, the block I/O
repeat flag rules, EI delay, IM 2 vector from I:$FF. Verified against every SingleStepTests/z80 file
(`HARTE_Z80_TESTS`, `Cpu/HarteZ80Tests`) and `Cpu/Z80Tests`. No allocations; `switch` decoders in `Z80.Execute.cs`
(unprefixed) and `Z80.Prefixed.cs` (CB, ED, DDCB).

## Commodore 128 — `Tedd.Commodore.Emulator.C128`

```csharp
public sealed class C128 : CommodoreMachine
{
    public C128(C128RomSet roms, int audioSampleRate = 44100);
    public C128RomSet Roms; public C128Memory Memory; public Mmu8722 Mmu; public Z80 Z80; public Vdc8563 Vdc;
    public bool Display40Columns;    // the 40/80 DISPLAY key (true = up = 40 columns), read by the KERNAL at reset / ESC X
    public bool CapsLock;            // the CAPS LOCK key (8502 port bit 6)
    public bool Z80Active, C64Mode;
    public string GetVdcScreenText(); public string GetVdcScreenAscii();   // the 80 column screen
}
```

Clocking (one system cycle, 985 248 Hz PAL like the C64):

```
vic.Clock(); cia1.Clock(); cia2.Clock();
irq = vic.Irq || cia1.IrqLine || expansion.Irq; nmi = cia2.IrqLine || restore || expansion.Nmi;
if (z80 owns the bus)  { z80.Irq = irq; z80.SetNmi(nmi); if (!vic.Ba) { budget += 2; while (budget > 0) budget -= z80.Step(); } }
else                   { cpu.Rdy = !vic.Ba; cpu.Irq = irq; cpu.Nmi = nmi; cpu.Clock(); if (vic.FastMode) cpu.Clock(); }
audio.Clock(); vdc.Clock(); TOD ticks; 1541 at 1 MHz
```

The Z80 gets two T-states per system cycle (VICE: each memory or I/O transaction operates at 1 MHz, T-states are
halved), does not run while BA is asserted, and the bus changes hands at instruction boundaries when MCR bit 0 is
written. Reset starts the Z80 (MCR = 0): the boot BIOS at Z80 $0000 (the $D000 quarter of the KERNAL ROM) checks the
cartridge lines and the C= key, programs the MMU, copies its switch code to $FFD0 and turns the 8502 on. In 2 MHz
mode the 8502 gets two cycles per VIC cycle (no I/O clock stretching is modelled); the Z80 is unaffected.

### MMU 8722 — `Mmu8722`

Registers $D500-$D50B (I/O) and the always visible window $FF00-$FF04 (CR, and LCR A-D: writing loads CR from
PCR A-D). CR: bit 0 I/O (0 = on), bit 1 BASIC low ROM (0 = ROM), bits 2-3 $8000-$BFFF (0 BASIC high, 1 internal
function ROM, 2 external function ROM, 3 RAM), bits 4-5 $C000-$FFFF (0 editor/character ROM/KERNAL, 1/2 function ROM,
3 RAM), bits 6-7 RAM bank (only bit 6 on a 128K machine). MCR: bit 0 CPU (0 = Z80, 1 = 8502), bit 1 fast serial
direction, bit 6 written 1 = C64 mode (latched until reset); read back: bits 0-3 as written, bit 4 /GAME level,
bit 5 /EXROM level, bit 6 = 0, bit 7 = 40/80 key (1 = 40 columns). RCR: bits 0-1 common RAM size 1K/4K/8K/16K,
bit 2 common at the bottom, bit 3 at the top, bits 6-7 the VIC's 64K block. P0L/P0H, P1L/P1H: page (and bank) that
CPU page 0 / page 1 are redirected to; the high byte is latched and committed by the low byte write; a target page
in common RAM is forced to bank 0; the high bytes read with bits 4-7 set. Version register reads $20 (2 banks).
`Changed` fires on anything that alters the map, `CpuSwitched` when bit 0 of the MCR flips.

### Memory — `C128Memory : IBus, IVicMemory`

256-entry page tables (read source array + offset, write target array + offset, kind) rebuilt from the MMU, the 8502
port and the cartridge lines; a memory access is one table lookup. Rules: RAM from the CR bank with the common area
always in bank 0; page 0 / page 1 relocated to P0 / P1 and the target pages appear in their place (exchange); the 8502
port is always $0000/$0001; $FF00-$FF04 are the MMU in C128 mode; I/O when CR bit 0 = 0: VIC $D000-$D3FF, SID
$D400-$D4FF, MMU $D500-$D50B (C128 mode only), VDC $D600/$D601, $D700 open, colour RAM $D800-$DBFF (1K bank chosen by
port bit 0 for the CPU and bit 1 for the VIC; 1 = bank 0), CIA1 $DC00, CIA2 $DD00, the expansion port's I/O 1 / I/O 2
$DE00-$DFFF; with I/O off and the KERNAL selected the C128 character set sits at $D000. External function ROM = the
expansion device's ROML at $8000 and ROMH (ROML again without one) at $A000 (bits 2-3 = 2), or ROMH at $C000 and $E000
(bits 4-5 = 2); a window the device does not fill, like the empty internal function ROM socket, is open bus.
C64 mode: the C64 PLA from the port lines LORAM/HIRAM/CHAREN and the cartridge (incl. Ultimax), C64 BASIC/KERNAL, the
C64 half of the character ROM, no MMU registers; the RAM bank and common area still apply.

Z80 view (`Z80View : IZ80Bus`): $0000-$0FFF = the Z80 BIOS while RAM bank 0 is selected (whatever the other CR bits
say; the boot BIOS switches to all-RAM in its second instruction and keeps running from ROM), bank 1 RAM otherwise;
no processor port at $0000/$0001; everything else like the 8502. IN/OUT with a port address of $0000-$0FFF read/write
the RAM under the I/O area ($D000-$DFFF of bank 0; with bank 1 selected they go to Z80 memory at the port address),
$D000-$DFFF the I/O chips regardless of CR bit 0, other ports memory. (VICE's z80mem.c model.)

VIC view: RCR bits 6-7 pick the 64K block, CIA2 the 16K bank, character ROM (C128 or C64 half) at $1000-$1FFF of
banks 0 and 2, ROMH at $3000-$3FFF in Ultimax C64 mode.

### VIC-IIe

`new VicII(memory, c128: true)`: $D02F bits 0-2 = keyboard lines K0-K2 (bits 3-7 read 1), $D030 bit 0 = 2 MHz mode,
bit 1 = test (bits 2-7 read 1). In 2 MHz mode the VIC keeps its raster timing and interrupts but performs no memory
accesses (fetches read $FF) and never asserts BA. The C128 copies `KeyboardLines` into `Keyboard.ExtendedRowLevels`
before each CIA1 port B read; the matrix has 11 rows (`C64Key` values 64-87: HELP, keypad, TAB, ESC, LINE FEED,
ENTER, ALT, cursor keys, NO SCROLL). A C64 leaves the extra rows unselected.

### VDC 8563 — `Tedd.Commodore.Emulator.Video.Vdc8563`

```csharp
public sealed class Vdc8563 : IClockable
{
    public Vdc8563(int ramSize = 16384);          // or 65536
    public byte Read(int port); public byte Peek(int port); public void Write(int port, byte value);  // port 0 = $D600, 1 = $D601
    public void Clock();                          // one system cycle; renders + FrameCompleted at the end of each VDC frame
    public byte[] Ram; public uint[] Frame;       // 768 x 272 ARGB, the 640 x 200 display at (64, 36)
    public const int FrameWidth = 768, FrameHeight = 272, DisplayX = 64, DisplayY = 36;
    public static readonly uint[] Palette;        // 16 RGBI colours
    public bool Ready, VerticalBlank; public int RasterLine; public byte Register(int index);
    public string GetScreenText(Func<byte, char> convert);
}
```

$D600 write selects a register (0-36), $D600 read = status: bit 7 ready, bit 6 light pen (never), bit 5 vertical
blank (the raster is outside the displayed rows), bits 0-2 revision (default 1). $D601 reads/writes the selected
register; R31 accesses RAM at R18/R19 and increments it (ready drops for 43 cycles inside the display, 4 outside);
writing R30 runs a block fill (R31 value) or copy (R24 bit 7, source R32/R33) of R30 bytes (0 = 256) and updates the
addresses (busy 0.66 / 1.2 cycles per byte, VICE's numbers). Unused register bits read as 1 (`ReadMask`), R16/R17 are
read-only. Timing: a 16.16 fixed point accumulator adds 16 MHz / 985 248 Hz dots per cycle; a line is (R0 + 1) * 8
dots, a frame (R4 + 1) * char height + R5 lines; the picture is rendered when the frame wraps. Rendering: text from
R12/R13 (+ R27 per row) with attributes from R20/R21 when R25 bit 6 is set (bits 0-3 colour, 4 blink, 5 underline at
R29, 6 reverse, 7 alternate set = +256), character data at R28 bits 5-7 x $2000 with 16 (or 32 for tall characters)
bytes per glyph, R22 total/displayed width with semi-graphics (R25 bit 5) repeating the last pixel, double width
pixels (R25 bit 4), the cursor R14/R15 with modes/lines from R10/R11, blink rates R24 bit 5, reverse screen R24 bit 6,
bitmap mode (R25 bit 7, fg/bg from the attribute nibbles or R26). Not modelled: smooth scrolling, interlace, beam
position dependent register effects.

### ROM set — `C128RomSet`

BASIC low/high (16K each, or one 32K image), KERNAL (16K: editor $C000, Z80 BIOS $D000, KERNAL $E000; or the 32K
"complete" image of the C128DCR whose lower half is the C64 BASIC + KERNAL), 8K character ROM (C64 set first, C128
set at $1000), the C64 BASIC/KERNAL, optional 1541 DOS. `Locate()` checks `C128_ROMS`, `C64_ROMS`, the base and
current directory and the `roms` / `src/Tedd.Commodore.GUI` sub-directories of their ancestors. `ToC64RomSet()`.

## Hosting additions for the C128

`EmulatorSession(C128RomSet, ...)` creates a C128 (`columns80` presses the 40/80 key before boot);
`Machine` is the `CommodoreMachine`, `C64` / `C128` are typed views, `Model`, `Roms` (the C64 set, for a C128 its C64
mode ROMs), `C128Roms`, `HasDriveRom`, `RomDescription`. `Display` (`Auto` = follow the 40/80 key, `VicII`, `Vdc`),
`ShowingVdc`, `CurrentFrame`, `Display40Columns`, `CapsLock`. `VideoFrame` carries `Source`, full and visible
geometry (VIC-II 504 x 312 / 384 x 272, VDC 768 x 272); sinks size their targets from the frame. `SystemCommand`
gained `ToggleColumns` and `CapsLock`, applied by the session and then reported. `KeyBindings.CreateDefault(model)`:
the C64 layout gets the free keys for the C128 extras (Numpad 1/3/7/9, Page Down = HELP, Scroll Lock = NO SCROLL,
Left Alt = ALT, Caps Lock); the C128 layout also maps Escape = ESC, Tab = TAB, End = RUN/STOP, Right Ctrl = C=, the
whole keypad, the cursor keys and F9 = 40/80. `AttachDisk` with autostart resets a C128 when the disk has a boot
sector (track 1 sector 0 "CBM"), which makes the KERNAL boot it (CP/M).

## Embedding library — `src/Tedd.Commodore.Embedding`

For programs that draw and play the machine themselves (a Vulkan/Direct3D/OpenGL game, an Avalonia application): no
graphics, audio or UI references, `net11.0` + `netstandard2.1`, packed as a NuGet package together with the emulator
core and the hosting layer (`src/Package.props`, `.github/workflows/library.yml`).

```csharp
public sealed class EmbeddedEmulator : IDisposable   // one lock around frames, input, media and Invoke
{
    EmbeddedEmulator(RomSet roms, EmbeddedEmulatorOptions? options = null);      // or C128RomSet
    static EmbeddedEmulator CreateC64(byte[] basic, byte[] kernal, byte[] chargen, byte[]? drive1541 = null, ...);
    VideoOutput Video; AudioOutput Audio; EmulatorSession Session; int SampleRate; const double FrameRate;
    int Update(double deltaSeconds);  bool RunFrame();          // from the host's loop (accumulator, MaxFramesPerUpdate cap)
    void Start(); void Stop(); bool IsRunning;                   // or on its own thread (EmulatorRunner sharing the lock)
    bool Paused; bool Warp; DisplayOutput Display; KeyBindings Bindings; event Action<SystemCommand> Command;
    void KeyDown(string? code); void KeyUp(string? code); void SetJoystick(...); void SetKey(...); void ReleaseAllInput();
    void AttachMedia(byte[] data, string fileName, bool autostart = true); void Reset(bool hard = false);
    ExpansionDevice? Expansion; void AttachExpansion(ExpansionDevice? device, bool reset = false);   // .NET devices
    void Invoke(Action<EmulatorSession> action); T Invoke<T>(Func<EmulatorSession, T> func);
}
// SampleRate (48000), AttachDrive, Bindings, Columns80, AudioMaxLatencySeconds (0.15), AudioPrimeSeconds (0.06)
public sealed class EmbeddedEmulatorOptions;

public sealed class VideoOutput : IVideoSink         // lock free triple buffer: emulation thread -> one render thread
{
    bool TryAcquire(out RawFrame frame);   // newest frame since the last call; valid until the next call
    RawFrame Current; bool HasNewFrame; long FramesPresented, FramesAcquired, FramesSkipped; event Action FramePresented;
}
public readonly struct RawFrame                     // a view: visible area only (384 x 272 VIC-II, 768 x 272 VDC)
{
    RawFrame(in VideoFrame frame);         // zero-copy view of the emulator's raster inside an IVideoSink
    int Width, Height; long FrameNumber; VideoSource Source; ReadOnlySpan<uint> Palette; bool IsEmpty;
    ReadOnlySpan<uint> GetRow(int y); int GetRowBytes(FramePixelFormat f); int GetSizeInBytes(FramePixelFormat f);
    void CopyTo(Span<byte> destination, int rowPitch, FramePixelFormat format, bool flipVertically = false);
    void CopyTo(IntPtr destination, int destinationLength, int rowPitch, FramePixelFormat format, bool flipVertically = false);
}
public enum FramePixelFormat { Bgra8888, Rgba8888, Indexed8 }   // byte order in memory; Indexed8 = colour number 0-15

public sealed class AudioOutput : IAudioSink        // lock free SPSC ring: emulation thread -> one audio thread
{
    int Read(Span<float> destination, int channels = 2);   // what is there (push APIs); also Span<short>
    int Fill(Span<float> destination, int channels = 2);   // full buffer, silence for the shortfall (callbacks)
    int Available; double BufferedSeconds; long Underruns, DroppedSamples; void Clear();
}
public static class AvaloniaKeyCodes { static string? ToWebCode(string? physicalKeyName); }   // "A" -> "KeyA"
public static class GlfwKeyCodes { static string? ToWebCode(int glfwKey); }   // Silk.NET Key, OpenTK Keys, Raylib
```

Frames are converted on the render thread while copying into the host's memory: `Bgra8888` is the emulator's own
0xAARRGGBB word on a little-endian CPU (a block copy per row), `Rgba8888` swaps red and blue per pixel, `Indexed8`
maps each colour back to its palette index with a one-entry cache (pictures are long runs of one colour). The latency
policy of `AudioOutput` is the SDL sink's: prime with silence when a block finds the queue empty, drop incoming blocks
while more than the maximum latency is queued. `Clear` works from any thread by moving a "discard up to" mark the
consumer honours, so neither cursor ever has two writers.

The Unity bridge (`src/Tedd.Commodore.Unity`) runs on the same two outputs: `C64Bridge`'s copy methods take the newest
frame from a `VideoOutput` on the engine's main thread, which runs the frames as well, and `ReadAudio` is
`AudioOutput.Fill` with the default latency rule. The Unity package ships the netstandard2.1 build of the library.
