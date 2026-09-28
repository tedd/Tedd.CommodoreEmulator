using System;
using System.Runtime.CompilerServices;
using Tedd.Commodore.Emulator.Audio;
using Tedd.Commodore.Emulator.Bus;
using Tedd.Commodore.Emulator.IO;
using Tedd.Commodore.Emulator.Video;

namespace Tedd.Commodore.Emulator.C64;

/// <summary>
/// The C64 address space as seen by the CPU (PLA, 6510 I/O port, I/O chips, color RAM, expansion port) and by the
/// VIC-II (16K banks with the character ROM shadow). See docs/ARCHITECTURE.md, "C64 memory map" and "Expansion port".
/// </summary>
public sealed class C64Memory : IBus, IVicMemory
{
    // Region mapping (recomputed whenever the 6510 port or the cartridge lines change)
    private enum Map : byte { Ram, Open, RomL, RomH, Basic, Char, Io, Kernal }

    public readonly byte[] Ram = new byte[65536];
    public readonly byte[] ColorRam = new byte[1024];

    private readonly byte[] _basic;
    private readonly byte[] _kernal;
    private readonly byte[] _char;

    private ExpansionDevice? _expansion;
    private readonly Action _expansionChanged;
    // The expansion device's ROM windows, taken when the map is computed
    private byte[]? _romL, _romH;
    private int _romLOffset, _romHOffset;
    private Map _map8000 = Map.Ram, _mapA000 = Map.Basic, _mapD000 = Map.Io, _mapE000 = Map.Kernal;
    private bool _ultimax;

    // 6510 on-chip port ($0000 = DDR, $0001 = data)
    private byte _portDdr;
    private byte _portData;
    private byte _portFloating;    // bits 3, 6 and 7 float: as inputs they read the level last driven on the pin

    /// <summary>Cassette sense input (bit 4): true = no key pressed on the datasette (line high).</summary>
    public bool CassetteSense = true;

    /// <summary>Last value seen on the data bus; returned for open/unmapped reads.</summary>
    public byte LastBusValue;

    /// <summary>Last value written to any SID register (write-only SID registers read this back).</summary>
    public byte LastSidWrite;

    /// <summary>VIC bank (0..3) selected through CIA2 port A (inverted bits 0-1). Bank 0 = $0000-$3FFF.</summary>
    public int VicBank;

    // Chips (attached by the machine after construction)
    public VicII? Vic;
    public Sid6581? Sid;
    public Cia6526? Cia1;
    public Cia6526? Cia2;

    public C64Memory(RomSet roms)
    {
        _basic = roms.Basic;
        _kernal = roms.Kernal;
        _char = roms.Char;
        _expansionChanged = UpdateMap;
        Reset(hard: true);
    }

    /// <summary>
    /// The device in the expansion port (a <see cref="Cartridge"/> or any other <see cref="ExpansionDevice"/>),
    /// null = nothing. The map follows the device's lines and ROM windows from then on.
    /// </summary>
    public ExpansionDevice? Expansion
    {
        get => _expansion;
        set
        {
            if (!ReferenceEquals(value, _expansion))
            {
                _expansion?.Disconnect(_expansionChanged);
                _expansion = value;
                value?.Connect(_expansionChanged);
            }
            UpdateMap();
        }
    }

    /// <summary>6510 port data direction register ($0000).</summary>
    public byte PortDdr => _portDdr;
    /// <summary>6510 port data register as written ($0001).</summary>
    public byte PortData => _portData;
    /// <summary>Datasette motor control (bit 5 of $01, active low): true = motor on.</summary>
    public bool CassetteMotor => (PortLevels() & 0x20) == 0;

    /// <summary>
    /// Resets the memory subsystem. A hard reset also re-initialises RAM with the power-on pattern
    /// (64 bytes $00 / 64 bytes $FF alternating, as on real hardware).
    /// </summary>
    public void Reset(bool hard)
    {
        if (hard)
        {
            for (int i = 0; i < Ram.Length; i++)
                Ram[i] = ((i >> 6) & 1) != 0 ? (byte)0xFF : (byte)0x00;
            Array.Clear(ColorRam, 0, ColorRam.Length);
            for (int i = 0; i < ColorRam.Length; i++)
                ColorRam[i] = (byte)(((i >> 6) & 1) != 0 ? 0x0F : 0x00);
        }
        // Power-on state of the 6510 port: DDR = $2F (bits 0-3,5 output), data = $37 -> LORAM/HIRAM/CHAREN high.
        _portDdr = 0x2F;
        _portData = 0x37;
        _portFloating = 0;
        UpdateFloating();
        VicBank = 0;
        LastBusValue = 0;
        UpdateMap();
    }

    /// <summary>The three PLA input lines from the 6510 port (bit 0 LORAM, 1 HIRAM, 2 CHAREN) taking pull-ups into account.</summary>
    public int PlaLines => PortLevels() & 7;

    private byte PortLevels()
    {
        // Output bits show the data register; input bits show the external level: bits 0-2 have pull-ups
        // (read 1), bit 4 is the cassette sense line, bit 5 (motor control) reads 0, and bits 3, 6 and 7 are not
        // driven by anything else, so as inputs they keep the level that was last driven on the pin (no decay
        // modelled). Verified with the Lorenz "cpuport" test.
        int inputs = 0x07 | (CassetteSense ? 0x10 : 0) | (_portFloating & 0xC8);
        return (byte)((_portData & _portDdr) | (inputs & ~_portDdr));
    }

    /// <summary>Pins that are outputs take the data register level; that level stays on the floating pins when they become inputs.</summary>
    private void UpdateFloating()
    {
        _portFloating = (byte)(((_portFloating & ~_portDdr) | (_portData & _portDdr)) & 0xC8);
    }

    private void UpdateMap()
    {
        int lines = PlaLines;
        bool loram = (lines & 1) != 0, hiram = (lines & 2) != 0, charen = (lines & 4) != 0;
        var x = _expansion;
        bool exrom = x?.Exrom ?? true;
        bool game = x?.Game ?? true;
        _romL = x?.RomL;
        _romLOffset = x?.RomLOffset ?? 0;
        _romH = x?.RomH;
        _romHOffset = x?.RomHOffset ?? 0;
        // Where the PLA selects a ROM window the device does not fill, nothing drives the bus.
        var romL = _romL is null ? Map.Open : Map.RomL;
        var romH = _romH is null ? Map.Open : Map.RomH;

        _ultimax = exrom && !game;
        if (_ultimax)
        {
            _map8000 = romL;
            _mapA000 = Map.Open;
            _mapD000 = Map.Io;
            _mapE000 = romH;
            return;
        }

        // $8000: ROML when a cartridge asserts EXROM and LORAM=HIRAM=1
        _map8000 = (!exrom && loram && hiram) ? romL : Map.Ram;
        // $A000: ROMH for 16K cartridges (GAME low) when HIRAM=1; BASIC when LORAM=HIRAM=1 without cartridge
        if (!game && !exrom && hiram)
            _mapA000 = romH;
        else if (loram && hiram && game)
            _mapA000 = Map.Basic;
        else
            _mapA000 = Map.Ram;
        // $D000
        if (!loram && !hiram)
            _mapD000 = Map.Ram;
        else
            _mapD000 = charen ? Map.Io : Map.Char;
        // $E000
        _mapE000 = hiram ? Map.Kernal : Map.Ram;
    }

    #region CPU bus

    public byte Read(ushort address)
    {
        byte v = ReadInternal(address, peek: false);
        LastBusValue = v;
        return v;
    }

    /// <summary>Reads without side effects (chip registers use Peek). For debuggers.</summary>
    public byte Peek(ushort address) => ReadInternal(address, peek: true);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte ReadInternal(ushort address, bool peek)
    {
        switch (address >> 12)
        {
            case 0x0:
                if (address == 0) return _portDdr;
                if (address == 1) return PortLevels();
                return Ram[address];
            case 0x1: case 0x2: case 0x3: case 0x4: case 0x5: case 0x6: case 0x7:
                return _ultimax ? LastBusValue : Ram[address];
            case 0x8: case 0x9:
                return _map8000 switch
                {
                    Map.Ram => Ram[address],
                    Map.RomL => _romL![_romLOffset + (address & 0x1FFF)],
                    _ => LastBusValue,
                };
            case 0xA: case 0xB:
                return _mapA000 switch
                {
                    Map.Basic => _basic[address & 0x1FFF],
                    Map.RomH => _romH![_romHOffset + (address & 0x1FFF)],
                    Map.Open => LastBusValue,
                    _ => Ram[address],
                };
            case 0xC:
                return _ultimax ? LastBusValue : Ram[address];
            case 0xD:
                return _mapD000 switch
                {
                    Map.Io => ReadIo(address, peek),
                    Map.Char => _char[address & 0x0FFF],
                    _ => Ram[address],
                };
            default: // $E000-$FFFF
                return _mapE000 switch
                {
                    Map.Kernal => _kernal[address & 0x1FFF],
                    Map.RomH => _romH![_romHOffset + (address & 0x1FFF)],
                    Map.Open => LastBusValue,
                    _ => Ram[address],
                };
        }
    }

    private byte ReadIo(ushort address, bool peek)
    {
        switch (address >> 8)
        {
            case 0xD0: case 0xD1: case 0xD2: case 0xD3:
                return Vic is null ? LastBusValue : (peek ? Vic.Peek(address & 0x3F) : Vic.Read(address & 0x3F));
            case 0xD4: case 0xD5: case 0xD6: case 0xD7:
            {
                int reg = address & 0x1F;
                if (Sid is null) return LastBusValue;
                if (reg < 0x19) return LastSidWrite; // write-only registers return the SID's internal bus latch
                return peek ? Sid.Peek(reg) : Sid.Read(reg);
            }
            case 0xD8: case 0xD9: case 0xDA: case 0xDB:
                return (byte)((LastBusValue & 0xF0) | (ColorRam[address & 0x3FF] & 0x0F));
            case 0xDC:
                return Cia1 is null ? LastBusValue : (peek ? Cia1.Peek(address & 0x0F) : Cia1.Read(address & 0x0F));
            case 0xDD:
                return Cia2 is null ? LastBusValue : (peek ? Cia2.Peek(address & 0x0F) : Cia2.Read(address & 0x0F));
            default: // $DE00-$DFFF: I/O 1 and I/O 2 on the expansion port
                if (_expansion is { } x)
                    return (peek ? x.PeekIo(address) : x.ReadIo(address)) ?? LastBusValue;
                return LastBusValue;
        }
    }

    public void Write(ushort address, byte value)
    {
        LastBusValue = value;
        switch (address >> 12)
        {
            case 0x0:
                if (address == 0)
                {
                    _portDdr = value;
                    UpdateFloating();
                    UpdateMap();
                    return;
                }
                if (address == 1)
                {
                    _portData = value;
                    UpdateFloating();
                    UpdateMap();
                    return;
                }
                Ram[address] = value;
                return;
            case 0x1: case 0x2: case 0x3: case 0x4: case 0x5: case 0x6: case 0x7:
                if (!_ultimax) Ram[address] = value;
                return;
            case 0xA: case 0xB: case 0xC:
                if (!_ultimax) Ram[address] = value;
                return;
            case 0xD:
                if (_mapD000 == Map.Io)
                    WriteIo(address, value);
                else
                    Ram[address] = value;
                return;
            case 0xE: case 0xF:
                if (!_ultimax) Ram[address] = value;
                return;
            default: // $8000-$9FFF: RAM under ROML is still written
                Ram[address] = value;
                return;
        }
    }

    private void WriteIo(ushort address, byte value)
    {
        switch (address >> 8)
        {
            case 0xD0: case 0xD1: case 0xD2: case 0xD3:
                Vic?.Write(address & 0x3F, value);
                return;
            case 0xD4: case 0xD5: case 0xD6: case 0xD7:
                LastSidWrite = value;
                Sid?.Write(address & 0x1F, value);
                return;
            case 0xD8: case 0xD9: case 0xDA: case 0xDB:
                ColorRam[address & 0x3FF] = (byte)(value & 0x0F);
                return;
            case 0xDC:
                Cia1?.Write(address & 0x0F, value);
                return;
            case 0xDD:
                Cia2?.Write(address & 0x0F, value);
                return;
            default: // $DE00-$DFFF: I/O 1 and I/O 2 on the expansion port
                _expansion?.WriteIo(address, value);
                return;
        }
    }

    /// <summary>Writes RAM directly, bypassing the PLA (for program injection and the memory editor).</summary>
    public void PokeRam(int address, byte value) => Ram[address & 0xFFFF] = value;

    #endregion

    #region VIC bus

    /// <summary>
    /// VIC read of a 14-bit address in the current bank. The character ROM is visible at $1000-$1FFF in banks 0
    /// and 2; in Ultimax mode ROMH is visible at $3000-$3FFF of every bank (VICE: ultimax_romh_phi1).
    /// </summary>
    public byte ReadVic(int address14)
    {
        byte v = PeekVic(address14);
        LastBusValue = v;
        return v;
    }

    /// <summary>The same read without leaving the value on the bus, for debuggers and viewers.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte PeekVic(int address14)
    {
        int a = address14 & 0x3FFF;
        if (_ultimax && a >= 0x3000 && _romH is { } h)
            return h[_romHOffset + ((a & 0x0FFF) | 0x1000)];
        if ((a & 0x3000) == 0x1000 && (VicBank & 1) == 0 && !_ultimax)
            return _char[a & 0x0FFF];
        return Ram[(VicBank << 14) | a];
    }

    public byte ReadColor(int address10) => (byte)(ColorRam[address10 & 0x3FF] & 0x0F);

    #endregion
}
