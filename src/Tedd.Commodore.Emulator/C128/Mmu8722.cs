using System;

namespace Tedd.Commodore.Emulator.C128;

/// <summary>
/// The MOS 8722 memory management unit of the Commodore 128: eleven registers at $D500-$D50B (I/O area) plus
/// the always visible $FF00-$FF04 window. The MMU decides which RAM bank, which ROMs and whether I/O appear in
/// the 8502's (or Z80's) address space, relocates page 0 and page 1, defines the common RAM shared between the
/// banks, selects the active CPU and switches the machine into C64 mode. This class holds the registers and
/// the decoded configuration; <see cref="C128Memory"/> turns it into the actual address decoding.
/// (C128 Programmer's Reference Guide, chapter 13 "The MMU".)
/// </summary>
public sealed class Mmu8722
{
    /// <summary>$D500 configuration register.</summary>
    public const int RegCr = 0;
    /// <summary>$D501-$D504 preconfiguration registers A-D.</summary>
    public const int RegPcrA = 1;
    /// <summary>$D505 mode configuration register.</summary>
    public const int RegMcr = 5;
    /// <summary>$D506 RAM configuration register.</summary>
    public const int RegRcr = 6;
    /// <summary>$D507/$D508 page 0 pointer low (page) / high (bank).</summary>
    public const int RegP0L = 7, RegP0H = 8;
    /// <summary>$D509/$D50A page 1 pointer low / high.</summary>
    public const int RegP1L = 9, RegP1H = 10;
    /// <summary>$D50B version register: bits 0-3 MMU version, bits 4-7 number of 64K banks.</summary>
    public const int RegVersion = 11;

    private readonly byte[] _regs = new byte[12];
    private byte _p0HighLatch, _p1HighLatch;

    /// <summary>Raised after any write that can change the memory configuration.</summary>
    public event Action? Changed;

    /// <summary>Raised when MCR bit 0 changes, i.e. the other CPU takes over (argument: true = Z80 becomes active).</summary>
    public event Action<bool>? CpuSwitched;

    /// <summary>Level of the /GAME line of the expansion port (true = asserted / low; a C64 cartridge pulls it).</summary>
    public bool GameAsserted;
    /// <summary>Level of the /EXROM line (true = asserted / low).</summary>
    public bool ExromAsserted;
    /// <summary>The 40/80 DISPLAY key: true = key up = 40 columns (MCR bit 7 reads 1).</summary>
    public bool Display40Key = true;

    /// <summary>Configuration register $D500 as written.</summary>
    public byte Cr => _regs[RegCr];
    /// <summary>Mode configuration register $D505 (bits 0-3 as written).</summary>
    public byte Mcr => _regs[RegMcr];
    /// <summary>RAM configuration register $D506.</summary>
    public byte Rcr => _regs[RegRcr];

    /// <summary>CR bit 0: I/O ($D000-$DFFF) visible.</summary>
    public bool IoEnabled => (_regs[RegCr] & 0x01) == 0;
    /// <summary>CR bit 1: BASIC low ROM at $4000-$7FFF (0) or RAM (1).</summary>
    public bool BasicLowRom => (_regs[RegCr] & 0x02) == 0;
    /// <summary>CR bits 2-3: 0 = BASIC high ROM, 1 = internal function ROM, 2 = external function ROM, 3 = RAM at $8000-$BFFF.</summary>
    public int MidRomSelect => (_regs[RegCr] >> 2) & 3;
    /// <summary>CR bits 4-5: 0 = KERNAL (and character) ROM, 1 = internal, 2 = external function ROM, 3 = RAM at $C000-$FFFF.</summary>
    public int HighRomSelect => (_regs[RegCr] >> 4) & 3;
    /// <summary>CR bits 6-7: RAM bank for the CPU (the C128 has two, bit 7 is ignored).</summary>
    public int RamBank => (_regs[RegCr] >> 6) & 1;

    /// <summary>MCR bit 0 = 0: the Z80 owns the bus; 1: the 8502.</summary>
    public bool Z80Active => (_regs[RegMcr] & 0x01) == 0;
    /// <summary>MCR bit 1: fast serial bus direction (1 = output).</summary>
    public bool FastSerialOutput => (_regs[RegMcr] & 0x02) != 0;
    /// <summary>
    /// C64 mode (MCR bit 6 written as 1). Latched until reset: the PLA then behaves like a C64's, the C64 ROMs
    /// appear and the MMU registers disappear from the address space.
    /// </summary>
    public bool C64Mode { get; private set; }

    /// <summary>RCR bits 0-1: size of the common (shared) RAM area: 1K, 4K, 8K or 16K.</summary>
    public int CommonSize => (_regs[RegRcr] & 3) switch { 0 => 1024, 1 => 4096, 2 => 8192, _ => 16384 };
    /// <summary>RCR bit 2: common RAM at the bottom ($0000 up).</summary>
    public bool CommonBottom => (_regs[RegRcr] & 0x04) != 0;
    /// <summary>RCR bit 3: common RAM at the top ($FFFF down).</summary>
    public bool CommonTop => (_regs[RegRcr] & 0x08) != 0;
    /// <summary>RCR bits 6-7: the 64K block the VIC-II reads from (only bit 6 matters on a 128K machine).</summary>
    public int VicRamBank => (_regs[RegRcr] >> 6) & 1;

    /// <summary>Page (256 byte block) that CPU page 0 is redirected to.</summary>
    public int Page0 => _regs[RegP0L];
    /// <summary>RAM bank of the redirected page 0 (forced to 0 when the page lies in common RAM).</summary>
    public int Page0Bank => IsCommon(_regs[RegP0L] << 8) ? 0 : _regs[RegP0H] & 1;
    /// <summary>Page that CPU page 1 (the stack) is redirected to.</summary>
    public int Page1 => _regs[RegP1L];
    /// <summary>RAM bank of the redirected page 1.</summary>
    public int Page1Bank => IsCommon(_regs[RegP1L] << 8) ? 0 : _regs[RegP1H] & 1;

    /// <summary>True when <paramref name="address"/> lies in the common RAM area (which always maps to bank 0).</summary>
    public bool IsCommon(int address)
    {
        int size = CommonSize;
        if (CommonBottom && address < size) return true;
        if (CommonTop && address >= 0x10000 - size) return true;
        return false;
    }

    public void Reset()
    {
        Array.Clear(_regs, 0, _regs.Length);
        _regs[RegP1L] = 1;
        _p0HighLatch = _p1HighLatch = 0;
        C64Mode = false;
        Changed?.Invoke();
    }

    /// <summary>Reads register <paramref name="reg"/> (0..11, $D500 + reg).</summary>
    public byte Read(int reg) => Peek(reg);

    /// <summary>Reads register <paramref name="reg"/> (0..11) without side effects (there are none).</summary>
    public byte Peek(int reg)
    {
        switch (reg & 0x0F)
        {
            case RegCr: case RegPcrA: case RegPcrA + 1: case RegPcrA + 2: case RegPcrA + 3:
                return _regs[reg];
            case RegMcr:
                // Bits 0-3 as written, bit 4 = /GAME, bit 5 = /EXROM (line levels), bit 6 reads 0, bit 7 = 40/80 key.
                return (byte)((_regs[RegMcr] & 0x0F) | (GameAsserted ? 0 : 0x10) | (ExromAsserted ? 0 : 0x20) | (Display40Key ? 0x80 : 0));
            case RegRcr:
                return _regs[RegRcr];
            case RegP0L: case RegP1L:
                return _regs[reg];
            case RegP0H: case RegP1H:
                return (byte)(_regs[reg] | 0xF0);                  // bits 4-7 unused, read 1
            case RegVersion:
                return 0x20;                                       // version 0, two 64K banks
            default:
                return 0xFF;
        }
    }

    /// <summary>Writes register <paramref name="reg"/> (0..11).</summary>
    public void Write(int reg, byte value)
    {
        switch (reg & 0x0F)
        {
            case RegCr:
                _regs[RegCr] = value;
                Changed?.Invoke();
                break;
            case RegPcrA: case RegPcrA + 1: case RegPcrA + 2: case RegPcrA + 3:
                _regs[reg] = value;
                break;
            case RegMcr:
            {
                bool wasZ80 = Z80Active;
                _regs[RegMcr] = (byte)(value & 0x0F);
                if ((value & 0x40) != 0 && !C64Mode)
                {
                    C64Mode = true;
                    Changed?.Invoke();
                }
                if (wasZ80 != Z80Active)
                    CpuSwitched?.Invoke(Z80Active);
                break;
            }
            case RegRcr:
                _regs[RegRcr] = value;
                Changed?.Invoke();
                break;
            case RegP0L:
                // Writing the low byte commits the pointer together with the latched high byte.
                _regs[RegP0L] = value;
                _regs[RegP0H] = (byte)(_p0HighLatch & 0x0F);
                Changed?.Invoke();
                break;
            case RegP0H:
                _p0HighLatch = value;
                break;
            case RegP1L:
                _regs[RegP1L] = value;
                _regs[RegP1H] = (byte)(_p1HighLatch & 0x0F);
                Changed?.Invoke();
                break;
            case RegP1H:
                _p1HighLatch = value;
                break;
            default:
                break;                                             // version register is read-only
        }
    }

    /// <summary>Reads the $FF00-$FF04 window: $FF00 = CR, $FF01-$FF04 = PCR A-D.</summary>
    public byte ReadWindow(int offset) => offset == 0 ? _regs[RegCr] : _regs[RegPcrA + offset - 1];

    /// <summary>
    /// Writes the $FF00-$FF04 window: $FF00 writes CR, writing anything to $FF01-$FF04 loads CR from PCR A-D
    /// (the "load configuration registers").
    /// </summary>
    public void WriteWindow(int offset, byte value)
    {
        _regs[RegCr] = offset == 0 ? value : _regs[RegPcrA + offset - 1];
        Changed?.Invoke();
    }

    /// <summary>The raw register file (for debuggers).</summary>
    public ReadOnlySpan<byte> Registers => _regs;
}
