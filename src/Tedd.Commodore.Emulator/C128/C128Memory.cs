using System;
using System.Runtime.CompilerServices;
using Tedd.Commodore.Emulator.Audio;
using Tedd.Commodore.Emulator.Bus;
using Tedd.Commodore.Emulator.C64;
using Tedd.Commodore.Emulator.Cpu;
using Tedd.Commodore.Emulator.IO;
using Tedd.Commodore.Emulator.Video;

namespace Tedd.Commodore.Emulator.C128;

/// <summary>
/// The Commodore 128 address space: 128K of RAM in two banks, the C128 and C64 ROMs, the I/O chips, and the
/// <see cref="Mmu8722"/> that decides what the 8502 or the Z80 sees where. The decoding is kept as two 256-entry
/// page tables (read source / write target per 256-byte page) that are rebuilt whenever the MMU, the 8502 port
/// or the cartridge lines change, so a memory access is one table lookup.
/// <para>
/// C128 mode (C128 Programmer's Reference Guide, chapter 13): RAM bank from CR bits 6-7 with the common area
/// (RCR) always taken from bank 0; page 0 and page 1 relocated through the P0/P1 pointers (the target page and
/// the original page exchange places); BASIC low ROM at $4000 (CR bit 1), BASIC high / function ROM / RAM at
/// $8000 (bits 2-3), editor + KERNAL / function ROM / RAM at $C000-$FFFF (bits 4-5), I/O or the character ROM at
/// $D000 (bit 0); the MMU registers at $FF00-$FF04 are visible in every configuration and the 8502 port always
/// at $0000/$0001. Colour RAM has two 1K banks, chosen for the CPU by port bit 0 and for the VIC by bit 1.
/// </para>
/// <para>
/// C64 mode (MCR bit 6): the C64 PLA from the 6510 port lines and the cartridge, using the C64 ROMs and the C64
/// half of the character generator; the MMU registers disappear. The VDC and the VIC-IIe registers stay reachable.
/// </para>
/// <para>
/// Z80 view (VICE's model of the 8722): the Z80 reads its BIOS (the $D000 quarter of the KERNAL ROM) at
/// $0000-$0FFF as long as RAM bank 0 is selected (in bank 1 it sees RAM there), everything else is the 8502 map
/// without the processor port. IN/OUT with an address of $0000-$0FFF reach the RAM under the I/O area ($D000-$DFFF, bank 0),
/// with $D000-$DFFF they reach the I/O chips, other ports read/write memory.
/// </para>
/// </summary>
public sealed class C128Memory : IBus, IVicMemory
{
    private enum Kind : byte { Ram, Rom, Io, Port, Mmu, Open }

    /// <summary>128K RAM: bank 0 at 0..$FFFF, bank 1 at $10000..$1FFFF.</summary>
    public readonly byte[] Ram = new byte[0x20000];
    /// <summary>Two 1K colour RAM banks (nibbles).</summary>
    public readonly byte[] ColorRam = new byte[0x800];

    private readonly byte[] _basicLo, _basicHi, _kernal, _char, _c64Basic, _c64Kernal;

    /// <summary>The MMU. Its registers are reached through the I/O area and the $FF00 window.</summary>
    public Mmu8722 Mmu { get; } = new();

    // Chips (attached by the machine after construction)
    public VicII? Vic;
    public Sid6581? Sid;
    public Cia6526? Cia1;
    public Cia6526? Cia2;
    public Vdc8563? Vdc;


    /// <summary>Last value seen on the data bus; returned for open/unmapped reads.</summary>
    public byte LastBusValue;
    /// <summary>Last value written to any SID register (write-only SID registers read this back).</summary>
    public byte LastSidWrite;
    /// <summary>VIC bank (0..3) selected through CIA2 port A (inverted bits 0-1).</summary>
    public int VicBank;
    /// <summary>Cassette sense input (port bit 4): true = no key pressed on the datasette.</summary>
    public bool CassetteSense = true;
    /// <summary>The CAPS LOCK key (port bit 6 reads 0 while it is down).</summary>
    public bool CapsLock;

    private ExpansionDevice? _expansion;
    private readonly Action _expansionChanged;
    // The expansion device's ROM windows, taken when the map is rebuilt
    private byte[]? _romL, _romH;
    private int _romLOffset, _romHOffset;

    // 8502 on-chip port ($0000 = DDR, $0001 = data)
    private byte _portDdr, _portData, _portFloating;

    // Page tables: read source (null = special, see _kind) and write target (null = special)
    private readonly byte[][] _rd = new byte[256][];
    private readonly int[] _rdOff = new int[256];
    private readonly byte[][] _wr = new byte[256][];
    private readonly int[] _wrOff = new int[256];
    private readonly Kind[] _kind = new Kind[256];
    // RAM behind the special pages 0 and $FF
    private byte[] _ram0 = null!, _ramFF = null!;
    private int _ram0Off, _ramFFOff;
    private byte[]? _romFF;
    private int _romFFOff;
    private bool _z80Bios;
    private bool _ultimax;
    private int _colorCpu, _colorVic;   // 0 or $400
    private int _charOffset;            // 0 = C64 set, $1000 = C128 set

    public C128Memory(C128RomSet roms)
    {
        _basicLo = roms.BasicLow;
        _basicHi = roms.BasicHigh;
        _kernal = roms.Kernal;
        _char = roms.Char;
        _c64Basic = roms.C64Basic;
        _c64Kernal = roms.C64Kernal;
        Z80View = new Z80Bus(this);
        _expansionChanged = OnExpansionChanged;
        Mmu.Changed += UpdateMap;
        Reset(hard: true);
    }

    /// <summary>The Z80's view of this memory (BIOS at $0000, port I/O).</summary>
    public Z80Bus Z80View { get; }

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
            OnExpansionChanged();
        }
    }

    /// <summary>The MMU's MCR reads the EXROM and GAME lines, and the C64 mode PLA maps from them.</summary>
    private void OnExpansionChanged()
    {
        Mmu.ExromAsserted = !(_expansion?.Exrom ?? true);
        Mmu.GameAsserted = !(_expansion?.Game ?? true);
        UpdateMap();
    }

    /// <summary>8502 port data direction register ($0000).</summary>
    public byte PortDdr => _portDdr;
    /// <summary>8502 port data register as written ($0001).</summary>
    public byte PortData => _portData;
    /// <summary>Datasette motor control (bit 5 of $01, active low): true = motor on.</summary>
    public bool CassetteMotor => (PortLevels() & 0x20) == 0;
    /// <summary>True while the Z80 sees its BIOS ROM at $0000-$0FFF.</summary>
    public bool Z80BiosVisible => _z80Bios;

    /// <summary>
    /// Resets the memory subsystem (and the MMU). A hard reset also re-initialises RAM with the power-on pattern
    /// (64 bytes $00 / 64 bytes $FF alternating).
    /// </summary>
    public void Reset(bool hard)
    {
        if (hard)
        {
            for (int i = 0; i < Ram.Length; i++)
                Ram[i] = ((i >> 6) & 1) != 0 ? (byte)0xFF : (byte)0x00;
            for (int i = 0; i < ColorRam.Length; i++)
                ColorRam[i] = (byte)(((i >> 6) & 1) != 0 ? 0x0F : 0x00);
        }
        _portDdr = 0x2F;
        _portData = 0x37;
        _portFloating = 0;
        UpdateFloating();
        VicBank = 0;
        LastBusValue = 0;
        Mmu.Reset();   // raises Changed -> UpdateMap
    }

    #region 8502 port

    private byte PortLevels()
    {
        // Bits 0-2 have pull-ups, bit 4 is the cassette sense, bit 5 (motor) reads 0 as input, bit 6 is the CAPS
        // LOCK key (low while pressed), bits 3 and 7 keep the level last driven on the pin.
        int inputs = 0x07 | (CassetteSense ? 0x10 : 0) | (CapsLock ? 0 : 0x40) | (_portFloating & 0x88);
        return (byte)((_portData & _portDdr) | (inputs & ~_portDdr));
    }

    private void UpdateFloating()
    {
        _portFloating = (byte)(((_portFloating & ~_portDdr) | (_portData & _portDdr)) & 0x88);
    }

    #endregion

    #region Map

    /// <summary>Rebuilds the page tables from the MMU, the 8502 port and the cartridge lines.</summary>
    private void UpdateMap()
    {
        _romL = _expansion?.RomL;
        _romLOffset = _expansion?.RomLOffset ?? 0;
        _romH = _expansion?.RomH;
        _romHOffset = _expansion?.RomHOffset ?? 0;
        bool c64 = Mmu.C64Mode;
        int bank = Mmu.RamBank;
        byte port = PortLevels();
        _colorCpu = (port & 1) != 0 ? 0 : 0x400;
        _colorVic = (port & 2) != 0 ? 0 : 0x400;
        _charOffset = c64 ? 0 : C128RomSet.C128CharOffset;

        // RAM everywhere first, honouring the common area (always bank 0).
        for (int p = 0; p < 256; p++)
        {
            int a = p << 8;
            int b = bank == 1 && !Mmu.IsCommon(a) ? 1 : 0;
            SetRam(p, (b << 16) | a);
        }
        // Page 0 / page 1 relocation: the CPU's page 0 goes to the target page and the target page comes back at
        // page 0 (the two exchange places); likewise for page 1.
        Relocate(0, Mmu.Page0, Mmu.Page0Bank);
        Relocate(1, Mmu.Page1, Mmu.Page1Bank);

        if (c64) BuildC64Overlay(port);
        else BuildC128Overlay();

        // The processor port is always at $0000/$0001 (page 0 becomes special, its RAM is kept aside).
        _ram0 = _rd[0]!;
        _ram0Off = _rdOff[0];
        _rd[0] = null;
        _wr[0] = null;
        _kind[0] = Kind.Port;

        // The MMU window at $FF00-$FF04 is always visible in C128 mode.
        if (!c64)
        {
            _romFF = _kind[0xFF] == Kind.Rom ? _rd[0xFF] : null;
            _romFFOff = _rdOff[0xFF];
            _ramFF = _wr[0xFF]!;
            _ramFFOff = _wrOff[0xFF];
            _rd[0xFF] = null;
            _wr[0xFF] = null;
            _kind[0xFF] = Kind.Mmu;
        }

        // The Z80 BIOS (ROM at $D000) is mapped to $0000-$0FFF while RAM bank 0 is selected, whatever the other
        // configuration bits say (the boot BIOS switches to an all-RAM configuration in its second instruction and
        // keeps executing from ROM); with bank 1 selected the Z80 sees bank 1 RAM there, which is where CP/M
        // keeps its page zero and TPA.
        _z80Bios = !c64 && Mmu.RamBank == 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SetRam(int page, int ramOffset)
    {
        _rd[page] = Ram;
        _rdOff[page] = ramOffset;
        _wr[page] = Ram;
        _wrOff[page] = ramOffset;
        _kind[page] = Kind.Ram;
    }

    private void Relocate(int cpuPage, int targetPage, int targetBank)
    {
        if (targetPage == cpuPage) return;
        int original = _rdOff[cpuPage];                 // where the CPU page lived
        SetRam(cpuPage, (targetBank << 16) | (targetPage << 8));
        SetRam(targetPage, original);
    }

    private void SetRom(int firstPage, int pageCount, byte[] rom, int romOffset)
    {
        for (int i = 0; i < pageCount; i++)
        {
            _rd[firstPage + i] = rom;
            _rdOff[firstPage + i] = romOffset + (i << 8);
            _kind[firstPage + i] = Kind.Rom;
        }
    }

    /// <summary>An 8K expansion port ROM window, or open bus (writes reach the RAM underneath) when the device has none.</summary>
    private void SetWindow(int firstPage, byte[]? rom, int romOffset)
    {
        if (rom is null) SetSpecial(firstPage, 0x20, Kind.Open, true);
        else SetRom(firstPage, 0x20, rom, romOffset);
    }

    private void SetSpecial(int firstPage, int pageCount, Kind kind, bool writable)
    {
        for (int i = 0; i < pageCount; i++)
        {
            _rd[firstPage + i] = null;
            _kind[firstPage + i] = kind;
            if (!writable) _wr[firstPage + i] = null;
        }
    }

    private void BuildC128Overlay()
    {
        _ultimax = false;
        if (Mmu.BasicLowRom)
            SetRom(0x40, 0x40, _basicLo, 0);
        switch (Mmu.MidRomSelect)
        {
            case 0: SetRom(0x80, 0x40, _basicHi, 0); break;
            case 1: SetSpecial(0x80, 0x40, Kind.Open, true); break;                          // internal function ROM: none fitted
            case 2:   // external function ROM: ROML, then ROMH (ROML again without one)
                SetWindow(0x80, _romL, _romLOffset);
                if (_romH is null) SetWindow(0xA0, _romL, _romLOffset);
                else SetWindow(0xA0, _romH, _romHOffset);
                break;
        }
        switch (Mmu.HighRomSelect)
        {
            case 0:
                SetRom(0xC0, 0x10, _kernal, 0x0000);                                          // editor
                SetRom(0xD0, 0x10, _char, C128RomSet.C128CharOffset);                         // character ROM under I/O
                SetRom(0xE0, 0x20, _kernal, 0x2000);                                          // KERNAL
                break;
            case 1:
                SetSpecial(0xC0, 0x40, Kind.Open, true);
                break;
            case 2:
                SetWindow(0xC0, _romH, _romHOffset);
                SetWindow(0xE0, _romH, _romHOffset);
                break;
        }
        if (Mmu.IoEnabled)
            SetSpecial(0xD0, 0x10, Kind.Io, false);
    }

    private void BuildC64Overlay(byte port)
    {
        bool loram = (port & 1) != 0, hiram = (port & 2) != 0, charen = (port & 4) != 0;
        bool exrom = _expansion?.Exrom ?? true;
        bool game = _expansion?.Game ?? true;
        _ultimax = exrom && !game;

        if (_ultimax)
        {
            SetSpecial(0x10, 0x70, Kind.Open, false);
            SetWindow(0x80, _romL, _romLOffset);
            SetSpecial(0xA0, 0x30, Kind.Open, false);
            SetSpecial(0xD0, 0x10, Kind.Io, false);
            if (_romH is not null) SetRom(0xE0, 0x20, _romH, _romHOffset);
            else SetSpecial(0xE0, 0x20, Kind.Open, false);
            return;
        }

        if (!exrom && loram && hiram)
            SetWindow(0x80, _romL, _romLOffset);
        if (!game && !exrom && hiram)
            SetWindow(0xA0, _romH, _romHOffset);
        else if (loram && hiram && game)
        {
            SetRom(0xA0, 0x20, _c64Basic, 0);
        }
        if (loram || hiram)
        {
            if (charen) SetSpecial(0xD0, 0x10, Kind.Io, false);
            else SetRom(0xD0, 0x10, _char, 0);
        }
        if (hiram)
            SetRom(0xE0, 0x20, _c64Kernal, 0);
    }

    #endregion

    #region CPU bus (8502)

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
        int page = address >> 8;
        var arr = _rd[page];
        if (arr is not null)
            return arr[_rdOff[page] + (address & 0xFF)];
        return ReadSpecial(address, peek);
    }

    private byte ReadSpecial(ushort address, bool peek)
    {
        switch (_kind[address >> 8])
        {
            case Kind.Port:
                if (address == 0) return _portDdr;
                if (address == 1) return PortLevels();
                return _ram0[_ram0Off + address];
            case Kind.Io:
                return ReadIo(address, peek);
            case Kind.Mmu:
                if ((address & 0xFF) < 5) return Mmu.ReadWindow(address & 0xFF);
                return _romFF is { } rom ? rom[_romFFOff + (address & 0xFF)] : _ramFF[_ramFFOff + (address & 0xFF)];
            default:
                return LastBusValue;
        }
    }

    private byte ReadIo(ushort address, bool peek)
    {
        switch (address >> 8)
        {
            case 0xD0: case 0xD1: case 0xD2: case 0xD3:
                return Vic is null ? LastBusValue : (peek ? Vic.Peek(address & 0x3F) : Vic.Read(address & 0x3F));
            case 0xD4:
            {
                int reg = address & 0x1F;
                if (Sid is null) return LastBusValue;
                if (reg < 0x19) return LastSidWrite;
                return peek ? Sid.Peek(reg) : Sid.Read(reg);
            }
            case 0xD5:
                return Mmu.C64Mode ? LastBusValue : Mmu.Peek(address & 0x0F);
            case 0xD6:
                return Vdc is null ? LastBusValue : (peek ? Vdc.Peek(address & 1) : Vdc.Read(address & 1));
            case 0xD8: case 0xD9: case 0xDA: case 0xDB:
                return (byte)((LastBusValue & 0xF0) | (ColorRam[_colorCpu + (address & 0x3FF)] & 0x0F));
            case 0xDC:
                return Cia1 is null ? LastBusValue : (peek ? Cia1.Peek(address & 0x0F) : Cia1.Read(address & 0x0F));
            case 0xDD:
                return Cia2 is null ? LastBusValue : (peek ? Cia2.Peek(address & 0x0F) : Cia2.Read(address & 0x0F));
            case 0xDE: case 0xDF:   // I/O 1 and I/O 2 on the expansion port
                if (_expansion is { } x)
                    return (peek ? x.PeekIo(address) : x.ReadIo(address)) ?? LastBusValue;
                return LastBusValue;
            default: // $D700-$D7FF is not decoded
                return LastBusValue;
        }
    }

    public void Write(ushort address, byte value)
    {
        LastBusValue = value;
        int page = address >> 8;
        var arr = _wr[page];
        if (arr is not null)
        {
            arr[_wrOff[page] + (address & 0xFF)] = value;
            return;
        }
        WriteSpecial(address, value);
    }

    private void WriteSpecial(ushort address, byte value)
    {
        switch (_kind[address >> 8])
        {
            case Kind.Port:
                if (address == 0)
                {
                    _portDdr = value;
                    UpdateFloating();
                    UpdateMap();
                }
                else if (address == 1)
                {
                    _portData = value;
                    UpdateFloating();
                    UpdateMap();
                }
                else
                {
                    _ram0[_ram0Off + address] = value;
                }
                return;
            case Kind.Io:
                WriteIo(address, value);
                return;
            case Kind.Mmu:
                if ((address & 0xFF) < 5) Mmu.WriteWindow(address & 0xFF, value);
                else _ramFF[_ramFFOff + (address & 0xFF)] = value;
                return;
            default:
                return;   // open (Ultimax): nothing listens
        }
    }

    private void WriteIo(ushort address, byte value)
    {
        switch (address >> 8)
        {
            case 0xD0: case 0xD1: case 0xD2: case 0xD3:
                Vic?.Write(address & 0x3F, value);
                return;
            case 0xD4:
                LastSidWrite = value;
                Sid?.Write(address & 0x1F, value);
                return;
            case 0xD5:
                if (!Mmu.C64Mode) Mmu.Write(address & 0x0F, value);
                return;
            case 0xD6:
                Vdc?.Write(address & 1, value);
                return;
            case 0xD8: case 0xD9: case 0xDA: case 0xDB:
                ColorRam[_colorCpu + (address & 0x3FF)] = (byte)(value & 0x0F);
                return;
            case 0xDC:
                Cia1?.Write(address & 0x0F, value);
                return;
            case 0xDD:
                Cia2?.Write(address & 0x0F, value);
                return;
            case 0xDE: case 0xDF:   // I/O 1 and I/O 2 on the expansion port
                _expansion?.WriteIo(address, value);
                return;
        }
    }

    /// <summary>Writes bank 0 RAM directly, bypassing the MMU (for program injection and the memory editor).</summary>
    public void PokeRam(int address, byte value) => Ram[address & 0x1FFFF] = value;

    #endregion

    #region Z80 bus

    /// <summary>The Z80's memory read: BIOS at $0000-$0FFF when selected, otherwise the 8502 map without the port.</summary>
    public byte ReadZ80(ushort address)
    {
        byte v;
        if (address < 0x1000)
        {
            if (_z80Bios) v = _kernal[C128RomSet.Z80BiosOffset + address];
            else if (address < 0x100) v = _ram0[_ram0Off + address];
            else v = ReadInternal(address, peek: false);
        }
        else
        {
            v = ReadInternal(address, peek: false);
        }
        LastBusValue = v;
        return v;
    }

    /// <summary>The Z80's memory write: like the 8502's, except that $0000/$0001 are plain RAM.</summary>
    public void WriteZ80(ushort address, byte value)
    {
        if (address < 2)
        {
            LastBusValue = value;
            _ram0[_ram0Off + address] = value;
            return;
        }
        Write(address, value);
    }

    /// <summary>Z80 IN: ports $0000-$0FFF read the RAM under the I/O area (bank 0), $D000-$DFFF the I/O chips, others memory.</summary>
    public byte InZ80(ushort port)
    {
        byte v;
        if (port < 0x1000)
            v = Mmu.RamBank == 0 ? Ram[0xD000 | (port & 0x0FFF)] : ReadZ80(port);
        else if ((port & 0xF000) == 0xD000)
            v = ReadIo(port, peek: false);
        else
            v = ReadZ80(port);
        LastBusValue = v;
        return v;
    }

    /// <summary>Z80 OUT, the counterpart of <see cref="InZ80"/>.</summary>
    public void OutZ80(ushort port, byte value)
    {
        if (port < 0x1000)
        {
            LastBusValue = value;
            if (Mmu.RamBank == 0) Ram[0xD000 | (port & 0x0FFF)] = value;
            else WriteZ80(port, value);
        }
        else if ((port & 0xF000) == 0xD000)
        {
            LastBusValue = value;
            WriteIo(port, value);
        }
        else
        {
            WriteZ80(port, value);
        }
    }

    /// <summary>Adapter that presents <see cref="C128Memory"/> to the <see cref="Z80"/>.</summary>
    public sealed class Z80Bus : IZ80Bus
    {
        private readonly C128Memory _memory;
        internal Z80Bus(C128Memory memory) => _memory = memory;
        public byte Read(ushort address) => _memory.ReadZ80(address);
        public void Write(ushort address, byte value) => _memory.WriteZ80(address, value);
        public byte In(ushort port) => _memory.InZ80(port);
        public void Out(ushort port, byte value) => _memory.OutZ80(port, value);
    }

    #endregion

    #region VIC bus

    /// <summary>
    /// VIC read of a 14-bit address in the current bank of the 64K block selected by RCR bits 6-7. The character
    /// ROM (C128 or C64 half) is visible at $1000-$1FFF of VIC banks 0 and 2; in Ultimax mode (C64 mode with such
    /// a cartridge) ROMH appears at $3000-$3FFF of every bank.
    /// </summary>
    public byte ReadVic(int address14)
    {
        byte v = PeekVic(address14);
        LastBusValue = v;
        return v;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte PeekVic(int address14)
    {
        int a = address14 & 0x3FFF;
        if (_ultimax && a >= 0x3000 && _romH is { } h)
            return h[_romHOffset + ((a & 0x0FFF) | 0x1000)];
        if ((a & 0x3000) == 0x1000 && (VicBank & 1) == 0 && !_ultimax)
            return _char[_charOffset + (a & 0x0FFF)];
        return Ram[(Mmu.VicRamBank << 16) | (VicBank << 14) | a];
    }

    public byte ReadColor(int address10) => (byte)(ColorRam[_colorVic + (address10 & 0x3FF)] & 0x0F);

    /// <summary>Offset (0 or $400) of the colour RAM bank the CPU currently sees.</summary>
    public int CpuColorBank => _colorCpu;
    /// <summary>Offset (0 or $400) of the colour RAM bank the VIC currently sees.</summary>
    public int VicColorBank => _colorVic;

    #endregion
}
