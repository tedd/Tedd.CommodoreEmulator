using System;
using System.Runtime.CompilerServices;
using Tedd.Commodore.Emulator.Bus;

namespace Tedd.Commodore.Emulator.Cpu;

/// <summary>
/// Cycle-stepped NMOS 6502 core (also used for the 6510 in the C64 and the 6502 in the 1541 drive).
///
/// Every call to <see cref="Clock"/> performs exactly one bus cycle (one read or one write), including all
/// dummy reads/writes the real chip performs (page-crossing fix-ups, RMW double writes, stack dummy reads...).
/// The cycle sequences follow "64doc.txt" (John West / Marko Mäkelä) and were verified against the
/// SingleStepTests/65x02 cycle traces for all 256 opcodes.
///
/// Interrupt lines (<see cref="Irq"/>, <see cref="Nmi"/>) are sampled at the end of every cycle and polled
/// during the last cycle of every instruction using the value sampled in the cycle before, which reproduces
/// the well known SEI/CLI/PLP one-instruction delays and the taken-branch delay.
///
/// <see cref="Rdy"/> models the RDY pin (BA from the VIC-II): when low, the CPU halts on the next read cycle
/// but keeps executing write cycles.
/// <see cref="SetOverflow"/> models the SO pin (used by the 1541 for "byte ready").
/// </summary>
public sealed class Cpu6502
{
    public const byte FlagC = 0x01;
    public const byte FlagZ = 0x02;
    public const byte FlagI = 0x04;
    public const byte FlagD = 0x08;
    public const byte FlagB = 0x10;
    public const byte FlagU = 0x20;
    public const byte FlagV = 0x40;
    public const byte FlagN = 0x80;

    public const ushort NmiVector = 0xFFFA;
    public const ushort ResetVector = 0xFFFC;
    public const ushort IrqVector = 0xFFFE;

    private delegate void MicroOp(Cpu6502 cpu);

    private readonly record struct Cycle(MicroOp Run, bool IsWrite);

    private static readonly Cycle[][] Table = new Cycle[258][];
    private static readonly OpcodeInfo[] Infos = new OpcodeInfo[256];
    private const int IrqSequence = 256;
    private const int NmiSequence = 257;

    static Cpu6502() => BuildTable();

    /// <summary>Static opcode descriptions (mnemonic, addressing mode) for all 256 opcodes.</summary>
    public static OpcodeInfo GetOpcodeInfo(byte opcode) => Infos[opcode];

    private readonly IBus _bus;

    /// <summary>
    /// When set, every bus read and write the CPU performs is recorded here (the memory viewer's read/write
    /// highlighting). Leaving it null costs one predictable branch per bus cycle.
    /// </summary>
    public MemoryAccessTracker? AccessTracker;

    // Architectural registers
    public byte A;
    public byte X;
    public byte Y;
    public byte S;
    public ushort PC;
    private byte _p;

    /// <summary>
    /// Processor status register. Bits 4 (B) and 5 have no storage on the real chip; PHP/BRK push them as 1,
    /// PLP/RTI load bit 5 as 1 and bit 4 as 0. The value is stored verbatim here so that externally set
    /// values round-trip.
    /// </summary>
    public byte P
    {
        get => _p;
        set => _p = value;
    }

    public bool FlagCarry { get => (_p & FlagC) != 0; set => _p = value ? (byte)(_p | FlagC) : (byte)(_p & ~FlagC); }
    public bool FlagZero { get => (_p & FlagZ) != 0; set => _p = value ? (byte)(_p | FlagZ) : (byte)(_p & ~FlagZ); }
    public bool FlagInterrupt { get => (_p & FlagI) != 0; set => _p = value ? (byte)(_p | FlagI) : (byte)(_p & ~FlagI); }
    public bool FlagDecimal { get => (_p & FlagD) != 0; set => _p = value ? (byte)(_p | FlagD) : (byte)(_p & ~FlagD); }
    public bool FlagOverflow { get => (_p & FlagV) != 0; set => _p = value ? (byte)(_p | FlagV) : (byte)(_p & ~FlagV); }
    public bool FlagNegative { get => (_p & FlagN) != 0; set => _p = value ? (byte)(_p | FlagN) : (byte)(_p & ~FlagN); }

    // Input pins
    /// <summary>IRQ line (level sensitive). true = asserted.</summary>
    public bool Irq;
    /// <summary>NMI line (edge sensitive, triggers on false->true transition). true = asserted.</summary>
    public bool Nmi;
    /// <summary>RDY line. When false the CPU stalls on its next read cycle (writes still complete).</summary>
    public bool Rdy = true;

    // Statistics
    /// <summary>Number of executed bus cycles (stalled cycles are not counted).</summary>
    public long Cycles;
    /// <summary>Number of cycles the CPU was halted by RDY.</summary>
    public long StallCycles;
    /// <summary>Set when a JAM/KIL opcode was executed. Only <see cref="Reset"/> recovers.</summary>
    public bool Jammed;

    // Execution state
    private Cycle[] _current = Table[0xEA];
    private int _step;
    private bool _fetch = true;
    private bool _ended;
    private bool _pollTwoBack;

    // Operand/address latches
    private byte _opcode;
    private ushort _addr;      // effective address
    private ushort _wrongAddr; // effective address before page-cross fix-up (used for dummy accesses)
    private ushort _base;      // un-indexed base address (needed by SHA/SHX/SHY/TAS)
    private byte _ptr;         // zero page pointer for (zp,X)/(zp),Y
    private byte _tmp;         // data latch
    private bool _pageCross;

    // Interrupt sampling pipeline
    private bool _irqDet1, _irqDet2;   // IRQ detected at end of previous cycle / cycle before that
    private bool _nmiDet1, _nmiDet2;
    private bool _nmiLinePrev;
    private bool _nmiLatched;
    private bool _irqTake, _nmiTake;
    private bool _servicingNmi;

    public Cpu6502(IBus bus)
    {
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        _p = FlagU | FlagI;
        S = 0xFD;
    }

    /// <summary>The bus this CPU is connected to.</summary>
    public IBus Bus => _bus;

    /// <summary>Current opcode being executed (valid after the fetch cycle).</summary>
    public byte CurrentOpcode => _opcode;

    /// <summary>True when the next <see cref="Clock"/> will fetch a new opcode (instruction boundary).</summary>
    public bool AtInstructionBoundary => _fetch;

    /// <summary>True if an IRQ or NMI sequence will start at the next instruction boundary.</summary>
    public bool InterruptPending => _irqTake || _nmiTake;

    /// <summary>
    /// Performs a reset: loads PC from the reset vector, sets I, S = $FD. Takes effect immediately
    /// (the 7 cycle reset sequence of the real chip is not modelled cycle by cycle).
    /// </summary>
    public void Reset()
    {
        Jammed = false;
        _fetch = true;
        _step = 0;
        _ended = false;
        _pollTwoBack = false;
        _irqDet1 = _irqDet2 = _nmiDet1 = _nmiDet2 = false;
        _nmiLatched = false;
        _nmiLinePrev = Nmi;
        _irqTake = _nmiTake = false;
        _servicingNmi = false;
        S = 0xFD;
        _p |= FlagI;
        byte lo = Read(ResetVector);
        byte hi = Read((ushort)(ResetVector + 1));
        PC = (ushort)(lo | (hi << 8));
        Cycles += 7;
    }

    /// <summary>SO pin: sets the overflow flag immediately.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetOverflow() => _p |= FlagV;

    /// <summary>Executes cycles until the next instruction boundary. Returns the number of cycles executed (including stalls).</summary>
    public int Step()
    {
        int n = 0;
        do
        {
            Clock();
            n++;
        } while (!_fetch && n < 1_000_000);
        return n;
    }

    /// <summary>Executes exactly one bus cycle.</summary>
    public void Clock()
    {
        if (!Rdy)
        {
            bool nextIsWrite = !_fetch && _current[_step].IsWrite;
            if (!nextIsWrite)
            {
                StallCycles++;
                SampleInterrupts();
                return;
            }
        }

        Cycles++;

        if (_fetch)
        {
            _fetch = false;
            _step = 0;
            if (_nmiTake)
            {
                _nmiTake = false;
                _irqTake = false;
                _servicingNmi = true;
                Read(PC);
                _current = Table[NmiSequence];
            }
            else if (_irqTake)
            {
                _irqTake = false;
                _servicingNmi = false;
                Read(PC);
                _current = Table[IrqSequence];
            }
            else
            {
                _opcode = Read(PC++);
                _current = Table[_opcode];
            }
        }
        else
        {
            _current[_step].Run(this);
            _step++;
            if (_ended || _step >= _current.Length)
            {
                _ended = false;
                _fetch = true;
            }
        }

        if (_fetch)
        {
            // Instruction complete: poll interrupts using the line state sampled one cycle ago
            // (two cycles ago for a taken branch without page crossing).
            bool nmi = _pollTwoBack ? _nmiDet2 : _nmiDet1;
            bool irq = _pollTwoBack ? _irqDet2 : _irqDet1;
            _pollTwoBack = false;
            if (nmi)
            {
                _nmiTake = true;
                _nmiLatched = false;
                _nmiDet1 = _nmiDet2 = false;
            }
            else if (irq)
            {
                _irqTake = true;
            }
        }

        SampleInterrupts();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SampleInterrupts()
    {
        _irqDet2 = _irqDet1;
        _irqDet1 = Irq && (_p & FlagI) == 0;
        if (Nmi && !_nmiLinePrev)
            _nmiLatched = true;
        _nmiLinePrev = Nmi;
        _nmiDet2 = _nmiDet1;
        _nmiDet1 = _nmiLatched;
    }

    #region Helpers used by micro-ops

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte Read(ushort address)
    {
        AccessTracker?.Read(address);
        return _bus.Read(address);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Write(ushort address, byte value)
    {
        AccessTracker?.Write(address);
        _bus.Write(address, value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void End() => _ended = true;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SetNZ(byte v)
    {
        _p = (byte)((_p & ~(FlagN | FlagZ)) | (v & FlagN) | (v == 0 ? FlagZ : 0));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Push(byte v)
    {
        Write((ushort)(0x100 + S), v);
        S--;
    }

    private void IndexBy(byte index)
    {
        int sum = (_base & 0xFF) + index;
        _pageCross = sum > 0xFF;
        _addr = (ushort)(_base + index);
        _wrongAddr = (ushort)((_base & 0xFF00) | (sum & 0xFF));
    }

    #endregion

    #region Operations

    private void Adc(byte v)
    {
        if ((_p & FlagD) == 0)
        {
            int sum = A + v + (_p & FlagC);
            _p = (byte)((_p & ~(FlagC | FlagV)) | (sum > 0xFF ? FlagC : 0) | ((~(A ^ v) & (A ^ sum) & 0x80) != 0 ? FlagV : 0));
            A = (byte)sum;
            SetNZ(A);
        }
        else
        {
            // NMOS decimal mode, including its peculiar flag behaviour (from VICE / 64doc).
            int c = _p & FlagC;
            int tmp = (A & 0x0F) + (v & 0x0F) + c;
            if (tmp > 0x9) tmp += 0x6;
            if (tmp <= 0x0F)
                tmp = (tmp & 0x0F) + (A & 0xF0) + (v & 0xF0);
            else
                tmp = (tmp & 0x0F) + (A & 0xF0) + (v & 0xF0) + 0x10;
            int binary = (A + v + c) & 0xFF;
            _p = (byte)(_p & ~(FlagN | FlagV | FlagZ | FlagC));
            if (binary == 0) _p |= FlagZ;
            if ((tmp & 0x80) != 0) _p |= FlagN;
            if (((A ^ tmp) & 0x80) != 0 && ((A ^ v) & 0x80) == 0) _p |= FlagV;
            if ((tmp & 0x1F0) > 0x90) tmp += 0x60;
            if ((tmp & 0xFF0) > 0xF0) _p |= FlagC;
            A = (byte)tmp;
        }
    }

    private void Sbc(byte v)
    {
        if ((_p & FlagD) == 0)
        {
            Adc((byte)~v);
        }
        else
        {
            int borrow = (_p & FlagC) != 0 ? 0 : 1;
            int bin = A - v - borrow;
            int tmp = (A & 0x0F) - (v & 0x0F) - borrow;
            if ((tmp & 0x10) != 0)
                tmp = ((tmp - 6) & 0x0F) | ((A & 0xF0) - (v & 0xF0) - 0x10);
            else
                tmp = (tmp & 0x0F) | ((A & 0xF0) - (v & 0xF0));
            if ((tmp & 0x100) != 0) tmp -= 0x60;
            _p = (byte)(_p & ~(FlagN | FlagV | FlagZ | FlagC));
            if ((bin & 0x100) == 0) _p |= FlagC;
            if (((A ^ bin) & 0x80) != 0 && ((A ^ v) & 0x80) != 0) _p |= FlagV;
            SetNZ((byte)bin);
            A = (byte)tmp;
        }
    }

    private void Compare(byte reg, byte v)
    {
        int r = reg - v;
        _p = (byte)((_p & ~FlagC) | (r >= 0 ? FlagC : 0));
        SetNZ((byte)r);
    }

    private byte Asl(byte v)
    {
        _p = (byte)((_p & ~FlagC) | ((v & 0x80) != 0 ? FlagC : 0));
        v <<= 1;
        SetNZ(v);
        return v;
    }

    private byte Lsr(byte v)
    {
        _p = (byte)((_p & ~FlagC) | (v & FlagC));
        v >>= 1;
        SetNZ(v);
        return v;
    }

    private byte Rol(byte v)
    {
        int c = _p & FlagC;
        _p = (byte)((_p & ~FlagC) | ((v & 0x80) != 0 ? FlagC : 0));
        v = (byte)((v << 1) | c);
        SetNZ(v);
        return v;
    }

    private byte Ror(byte v)
    {
        int c = _p & FlagC;
        _p = (byte)((_p & ~FlagC) | (v & FlagC));
        v = (byte)((v >> 1) | (c << 7));
        SetNZ(v);
        return v;
    }

    private void Arr(byte v)
    {
        int tmp = A & v;
        if ((_p & FlagD) == 0)
        {
            tmp |= (_p & FlagC) << 8;
            tmp >>= 1;
            SetNZ((byte)tmp);
            _p = (byte)(_p & ~(FlagC | FlagV));
            if ((tmp & 0x40) != 0) _p |= FlagC;
            if (((tmp & 0x40) ^ ((tmp & 0x20) << 1)) != 0) _p |= FlagV;
            A = (byte)tmp;
        }
        else
        {
            int tmp2 = tmp | ((_p & FlagC) << 8);
            tmp2 >>= 1;
            _p = (byte)(_p & ~(FlagN | FlagZ | FlagV | FlagC));
            if ((tmp2 & 0x80) != 0) _p |= FlagN; // N = old carry
            if ((tmp2 & 0xFF) == 0) _p |= FlagZ;
            if (((tmp2 ^ tmp) & 0x40) != 0) _p |= FlagV;
            if (((tmp & 0x0F) + (tmp & 0x01)) > 0x5)
                tmp2 = (tmp2 & 0xF0) | ((tmp2 + 0x6) & 0x0F);
            if (((tmp & 0xF0) + (tmp & 0x10)) > 0x50)
            {
                tmp2 = (tmp2 & 0x0F) | ((tmp2 + 0x60) & 0xF0);
                _p |= FlagC;
            }
            A = (byte)tmp2;
        }
    }

    private void Bit(byte v)
    {
        _p = (byte)((_p & ~(FlagN | FlagV | FlagZ)) | (v & (FlagN | FlagV)) | ((v & A) == 0 ? FlagZ : 0));
    }

    private ushort PickVector()
    {
        // NMI hijacks BRK/IRQ if it became pending before the vector fetch.
        if (_servicingNmi || _nmiLatched)
        {
            _nmiLatched = false;
            _nmiDet1 = _nmiDet2 = false;
            _nmiTake = false;
            _servicingNmi = false;
            return NmiVector;
        }
        return IrqVector;
    }

    private byte HighAnd(byte reg)
    {
        byte v = (byte)(reg & ((_base >> 8) + 1));
        if (_pageCross)
            _addr = (ushort)((v << 8) | (_addr & 0xFF));
        return v;
    }

    #endregion

    #region Table construction

    private static void BuildTable()
    {
        // Fill everything with JAM first, so any gap is loud.
        for (int i = 0; i < 256; i++)
        {
            Table[i] = Jam();
            Infos[i] = new OpcodeInfo((byte)i, "JAM", AddressingMode.Implied, true);
        }

        // ---- Documented opcodes ----
        ReadGroup("ORA", 0x09, 0x05, 0x15, 0x0D, 0x1D, 0x19, 0x01, 0x11, static (c, v) => { c.A |= v; c.SetNZ(c.A); });
        ReadGroup("AND", 0x29, 0x25, 0x35, 0x2D, 0x3D, 0x39, 0x21, 0x31, static (c, v) => { c.A &= v; c.SetNZ(c.A); });
        ReadGroup("EOR", 0x49, 0x45, 0x55, 0x4D, 0x5D, 0x59, 0x41, 0x51, static (c, v) => { c.A ^= v; c.SetNZ(c.A); });
        ReadGroup("ADC", 0x69, 0x65, 0x75, 0x6D, 0x7D, 0x79, 0x61, 0x71, static (c, v) => c.Adc(v));
        ReadGroup("SBC", 0xE9, 0xE5, 0xF5, 0xED, 0xFD, 0xF9, 0xE1, 0xF1, static (c, v) => c.Sbc(v));
        ReadGroup("CMP", 0xC9, 0xC5, 0xD5, 0xCD, 0xDD, 0xD9, 0xC1, 0xD1, static (c, v) => c.Compare(c.A, v));
        ReadGroup("LDA", 0xA9, 0xA5, 0xB5, 0xAD, 0xBD, 0xB9, 0xA1, 0xB1, static (c, v) => { c.A = v; c.SetNZ(v); });

        ReadOperation ldx = static (c, v) => { c.X = v; c.SetNZ(v); };
        Read("LDX", 0xA2, AddressingMode.Immediate, ldx);
        Read("LDX", 0xA6, AddressingMode.ZeroPage, ldx);
        Read("LDX", 0xB6, AddressingMode.ZeroPageY, ldx);
        Read("LDX", 0xAE, AddressingMode.Absolute, ldx);
        Read("LDX", 0xBE, AddressingMode.AbsoluteY, ldx);

        ReadOperation ldy = static (c, v) => { c.Y = v; c.SetNZ(v); };
        Read("LDY", 0xA0, AddressingMode.Immediate, ldy);
        Read("LDY", 0xA4, AddressingMode.ZeroPage, ldy);
        Read("LDY", 0xB4, AddressingMode.ZeroPageX, ldy);
        Read("LDY", 0xAC, AddressingMode.Absolute, ldy);
        Read("LDY", 0xBC, AddressingMode.AbsoluteX, ldy);

        ReadOperation cpx = static (c, v) => c.Compare(c.X, v);
        Read("CPX", 0xE0, AddressingMode.Immediate, cpx);
        Read("CPX", 0xE4, AddressingMode.ZeroPage, cpx);
        Read("CPX", 0xEC, AddressingMode.Absolute, cpx);
        ReadOperation cpy = static (c, v) => c.Compare(c.Y, v);
        Read("CPY", 0xC0, AddressingMode.Immediate, cpy);
        Read("CPY", 0xC4, AddressingMode.ZeroPage, cpy);
        Read("CPY", 0xCC, AddressingMode.Absolute, cpy);

        Read("BIT", 0x24, AddressingMode.ZeroPage, static (c, v) => c.Bit(v));
        Read("BIT", 0x2C, AddressingMode.Absolute, static (c, v) => c.Bit(v));

        WriteOperation sta = static c => c.A;
        WriteOp("STA", 0x85, AddressingMode.ZeroPage, sta);
        WriteOp("STA", 0x95, AddressingMode.ZeroPageX, sta);
        WriteOp("STA", 0x8D, AddressingMode.Absolute, sta);
        WriteOp("STA", 0x9D, AddressingMode.AbsoluteX, sta);
        WriteOp("STA", 0x99, AddressingMode.AbsoluteY, sta);
        WriteOp("STA", 0x81, AddressingMode.IndirectX, sta);
        WriteOp("STA", 0x91, AddressingMode.IndirectY, sta);
        WriteOperation stx = static c => c.X;
        WriteOp("STX", 0x86, AddressingMode.ZeroPage, stx);
        WriteOp("STX", 0x96, AddressingMode.ZeroPageY, stx);
        WriteOp("STX", 0x8E, AddressingMode.Absolute, stx);
        WriteOperation sty = static c => c.Y;
        WriteOp("STY", 0x84, AddressingMode.ZeroPage, sty);
        WriteOp("STY", 0x94, AddressingMode.ZeroPageX, sty);
        WriteOp("STY", 0x8C, AddressingMode.Absolute, sty);

        RmwGroup("ASL", 0x0A, 0x06, 0x16, 0x0E, 0x1E, static (c, v) => c.Asl(v));
        RmwGroup("LSR", 0x4A, 0x46, 0x56, 0x4E, 0x5E, static (c, v) => c.Lsr(v));
        RmwGroup("ROL", 0x2A, 0x26, 0x36, 0x2E, 0x3E, static (c, v) => c.Rol(v));
        RmwGroup("ROR", 0x6A, 0x66, 0x76, 0x6E, 0x7E, static (c, v) => c.Ror(v));
        RmwGroup("INC", -1, 0xE6, 0xF6, 0xEE, 0xFE, static (c, v) => { v++; c.SetNZ(v); return v; });
        RmwGroup("DEC", -1, 0xC6, 0xD6, 0xCE, 0xDE, static (c, v) => { v--; c.SetNZ(v); return v; });

        Implied("CLC", 0x18, static c => c._p &= unchecked((byte)~FlagC));
        Implied("SEC", 0x38, static c => c._p |= FlagC);
        Implied("CLI", 0x58, static c => c._p &= unchecked((byte)~FlagI));
        Implied("SEI", 0x78, static c => c._p |= FlagI);
        Implied("CLV", 0xB8, static c => c._p &= unchecked((byte)~FlagV));
        Implied("CLD", 0xD8, static c => c._p &= unchecked((byte)~FlagD));
        Implied("SED", 0xF8, static c => c._p |= FlagD);
        Implied("DEX", 0xCA, static c => { c.X--; c.SetNZ(c.X); });
        Implied("DEY", 0x88, static c => { c.Y--; c.SetNZ(c.Y); });
        Implied("INX", 0xE8, static c => { c.X++; c.SetNZ(c.X); });
        Implied("INY", 0xC8, static c => { c.Y++; c.SetNZ(c.Y); });
        Implied("TAX", 0xAA, static c => { c.X = c.A; c.SetNZ(c.X); });
        Implied("TAY", 0xA8, static c => { c.Y = c.A; c.SetNZ(c.Y); });
        Implied("TXA", 0x8A, static c => { c.A = c.X; c.SetNZ(c.A); });
        Implied("TYA", 0x98, static c => { c.A = c.Y; c.SetNZ(c.A); });
        Implied("TSX", 0xBA, static c => { c.X = c.S; c.SetNZ(c.X); });
        Implied("TXS", 0x9A, static c => c.S = c.X);
        Implied("NOP", 0xEA, static c => { });

        Branch("BPL", 0x10, static c => (c._p & FlagN) == 0);
        Branch("BMI", 0x30, static c => (c._p & FlagN) != 0);
        Branch("BVC", 0x50, static c => (c._p & FlagV) == 0);
        Branch("BVS", 0x70, static c => (c._p & FlagV) != 0);
        Branch("BCC", 0x90, static c => (c._p & FlagC) == 0);
        Branch("BCS", 0xB0, static c => (c._p & FlagC) != 0);
        Branch("BNE", 0xD0, static c => (c._p & FlagZ) == 0);
        Branch("BEQ", 0xF0, static c => (c._p & FlagZ) != 0);

        // JMP abs
        Set(0x4C, "JMP", AddressingMode.Absolute, false,
            R(static c => c._addr = c.Read(c.PC++)),
            R(static c => c.PC = (ushort)((c.Read(c.PC) << 8) | c._addr)));
        // JMP (ind) with the page wrap bug
        Set(0x6C, "JMP", AddressingMode.Indirect, false,
            R(static c => c._addr = c.Read(c.PC++)),
            R(static c => c._addr |= (ushort)(c.Read(c.PC++) << 8)),
            R(static c => c._tmp = c.Read(c._addr)),
            R(static c => c.PC = (ushort)((c.Read((ushort)((c._addr & 0xFF00) | ((c._addr + 1) & 0xFF))) << 8) | c._tmp)));
        // JSR
        Set(0x20, "JSR", AddressingMode.Absolute, false,
            R(static c => c._addr = c.Read(c.PC++)),
            R(static c => c.Read((ushort)(0x100 + c.S))),
            W(static c => c.Push((byte)(c.PC >> 8))),
            W(static c => c.Push((byte)c.PC)),
            R(static c => c.PC = (ushort)((c.Read(c.PC) << 8) | c._addr)));
        // RTS
        Set(0x60, "RTS", AddressingMode.Implied, false,
            R(static c => c.Read(c.PC)),
            R(static c => { c.Read((ushort)(0x100 + c.S)); c.S++; }),
            R(static c => { c.PC = c.Read((ushort)(0x100 + c.S)); c.S++; }),
            R(static c => c.PC |= (ushort)(c.Read((ushort)(0x100 + c.S)) << 8)),
            R(static c => { c.Read(c.PC); c.PC++; }));
        // RTI
        Set(0x40, "RTI", AddressingMode.Implied, false,
            R(static c => c.Read(c.PC)),
            R(static c => { c.Read((ushort)(0x100 + c.S)); c.S++; }),
            R(static c => { c._p = (byte)((c.Read((ushort)(0x100 + c.S)) | FlagU) & ~FlagB); c.S++; }),
            R(static c => { c.PC = c.Read((ushort)(0x100 + c.S)); c.S++; }),
            R(static c => c.PC |= (ushort)(c.Read((ushort)(0x100 + c.S)) << 8)));
        // PHA / PHP / PLA / PLP
        Set(0x48, "PHA", AddressingMode.Implied, false,
            R(static c => c.Read(c.PC)),
            W(static c => c.Push(c.A)));
        Set(0x08, "PHP", AddressingMode.Implied, false,
            R(static c => c.Read(c.PC)),
            W(static c => c.Push((byte)(c._p | FlagB | FlagU))));
        Set(0x68, "PLA", AddressingMode.Implied, false,
            R(static c => c.Read(c.PC)),
            R(static c => { c.Read((ushort)(0x100 + c.S)); c.S++; }),
            R(static c => { c.A = c.Read((ushort)(0x100 + c.S)); c.SetNZ(c.A); }));
        Set(0x28, "PLP", AddressingMode.Implied, false,
            R(static c => c.Read(c.PC)),
            R(static c => { c.Read((ushort)(0x100 + c.S)); c.S++; }),
            R(static c => c._p = (byte)((c.Read((ushort)(0x100 + c.S)) | FlagU) & ~FlagB)));
        // BRK
        Set(0x00, "BRK", AddressingMode.Implied, false,
            R(static c => c.Read(c.PC++)),
            W(static c => c.Push((byte)(c.PC >> 8))),
            W(static c => c.Push((byte)c.PC)),
            W(static c => c.Push((byte)(c._p | FlagB | FlagU))),
            R(static c => { c._addr = c.PickVector(); c.PC = c.Read(c._addr); c._p |= FlagI; }),
            R(static c => c.PC |= (ushort)(c.Read((ushort)(c._addr + 1)) << 8)));

        // Hardware interrupt sequences (cycle 1 = dummy opcode read done by the fetch logic)
        Cycle[] irqSeq =
        {
            R(static c => c.Read(c.PC)),
            W(static c => c.Push((byte)(c.PC >> 8))),
            W(static c => c.Push((byte)c.PC)),
            W(static c => c.Push((byte)((c._p | FlagU) & ~FlagB))),
            R(static c => { c._addr = c.PickVector(); c.PC = c.Read(c._addr); c._p |= FlagI; }),
            R(static c => c.PC |= (ushort)(c.Read((ushort)(c._addr + 1)) << 8)),
        };
        Table[IrqSequence] = irqSeq;
        Table[NmiSequence] = irqSeq;

        // ---- Undocumented opcodes ----
        RmwGroupIllegal("SLO", 0x07, 0x17, 0x0F, 0x1F, 0x1B, 0x03, 0x13, static (c, v) => { v = c.Asl(v); c.A |= v; c.SetNZ(c.A); return v; });
        RmwGroupIllegal("RLA", 0x27, 0x37, 0x2F, 0x3F, 0x3B, 0x23, 0x33, static (c, v) => { v = c.Rol(v); c.A &= v; c.SetNZ(c.A); return v; });
        RmwGroupIllegal("SRE", 0x47, 0x57, 0x4F, 0x5F, 0x5B, 0x43, 0x53, static (c, v) => { v = c.Lsr(v); c.A ^= v; c.SetNZ(c.A); return v; });
        RmwGroupIllegal("RRA", 0x67, 0x77, 0x6F, 0x7F, 0x7B, 0x63, 0x73, static (c, v) => { v = c.Ror(v); c.Adc(v); return v; });
        RmwGroupIllegal("DCP", 0xC7, 0xD7, 0xCF, 0xDF, 0xDB, 0xC3, 0xD3, static (c, v) => { v--; c.Compare(c.A, v); return v; });
        RmwGroupIllegal("ISC", 0xE7, 0xF7, 0xEF, 0xFF, 0xFB, 0xE3, 0xF3, static (c, v) => { v++; c.Sbc(v); return v; });

        ReadOperation lax = static (c, v) => { c.A = c.X = v; c.SetNZ(v); };
        Read("LAX", 0xA7, AddressingMode.ZeroPage, lax, true);
        Read("LAX", 0xB7, AddressingMode.ZeroPageY, lax, true);
        Read("LAX", 0xAF, AddressingMode.Absolute, lax, true);
        Read("LAX", 0xBF, AddressingMode.AbsoluteY, lax, true);
        Read("LAX", 0xA3, AddressingMode.IndirectX, lax, true);
        Read("LAX", 0xB3, AddressingMode.IndirectY, lax, true);
        // LAX #imm (unstable, "magic" constant $EE matches the SingleStepTests reference)
        Read("LAX", 0xAB, AddressingMode.Immediate, static (c, v) => { c.A = c.X = (byte)((c.A | 0xEE) & v); c.SetNZ(c.A); }, true);
        // ANE/XAA #imm (unstable)
        Read("ANE", 0x8B, AddressingMode.Immediate, static (c, v) => { c.A = (byte)((c.A | 0xEE) & c.X & v); c.SetNZ(c.A); }, true);
        ReadOperation anc = static (c, v) => { c.A &= v; c.SetNZ(c.A); c.FlagCarry = (c.A & 0x80) != 0; };
        Read("ANC", 0x0B, AddressingMode.Immediate, anc, true);
        Read("ANC", 0x2B, AddressingMode.Immediate, anc, true);
        Read("ALR", 0x4B, AddressingMode.Immediate, static (c, v) => { c.A = c.Lsr((byte)(c.A & v)); }, true);
        Read("ARR", 0x6B, AddressingMode.Immediate, static (c, v) => c.Arr(v), true);
        Read("SBX", 0xCB, AddressingMode.Immediate, static (c, v) => { int r = (c.A & c.X) - v; c.FlagCarry = r >= 0; c.X = (byte)r; c.SetNZ(c.X); }, true);
        Read("SBC", 0xEB, AddressingMode.Immediate, static (c, v) => c.Sbc(v), true);
        Read("LAS", 0xBB, AddressingMode.AbsoluteY, static (c, v) => { c.A = c.X = c.S = (byte)(v & c.S); c.SetNZ(c.A); }, true);

        WriteOperation sax = static c => (byte)(c.A & c.X);
        WriteOp("SAX", 0x87, AddressingMode.ZeroPage, sax, true);
        WriteOp("SAX", 0x97, AddressingMode.ZeroPageY, sax, true);
        WriteOp("SAX", 0x8F, AddressingMode.Absolute, sax, true);
        WriteOp("SAX", 0x83, AddressingMode.IndirectX, sax, true);
        // SHA/SHX/SHY/TAS: value = reg & (high byte of base address + 1); on page cross the high byte of
        // the target address becomes the stored value.
        WriteOp("SHA", 0x9F, AddressingMode.AbsoluteY, static c => c.HighAnd((byte)(c.A & c.X)), true);
        WriteOp("SHA", 0x93, AddressingMode.IndirectY, static c => c.HighAnd((byte)(c.A & c.X)), true);
        WriteOp("SHX", 0x9E, AddressingMode.AbsoluteY, static c => c.HighAnd(c.X), true);
        WriteOp("SHY", 0x9C, AddressingMode.AbsoluteX, static c => c.HighAnd(c.Y), true);
        WriteOp("TAS", 0x9B, AddressingMode.AbsoluteY, static c => { c.S = (byte)(c.A & c.X); return c.HighAnd(c.S); }, true);

        // NOPs of various shapes
        foreach (var op in new[] { 0x1A, 0x3A, 0x5A, 0x7A, 0xDA, 0xFA })
            Implied("NOP", op, static c => { }, true);
        foreach (var op in new[] { 0x80, 0x82, 0x89, 0xC2, 0xE2 })
            Read("NOP", op, AddressingMode.Immediate, static (c, v) => { }, true);
        foreach (var op in new[] { 0x04, 0x44, 0x64 })
            Read("NOP", op, AddressingMode.ZeroPage, static (c, v) => { }, true);
        foreach (var op in new[] { 0x14, 0x34, 0x54, 0x74, 0xD4, 0xF4 })
            Read("NOP", op, AddressingMode.ZeroPageX, static (c, v) => { }, true);
        Read("NOP", 0x0C, AddressingMode.Absolute, static (c, v) => { }, true);
        foreach (var op in new[] { 0x1C, 0x3C, 0x5C, 0x7C, 0xDC, 0xFC })
            Read("NOP", op, AddressingMode.AbsoluteX, static (c, v) => { }, true);

        // JAM / KIL
        foreach (var op in new[] { 0x02, 0x12, 0x22, 0x32, 0x42, 0x52, 0x62, 0x72, 0x92, 0xB2, 0xD2, 0xF2 })
        {
            Table[op] = Jam();
            Infos[op] = new OpcodeInfo((byte)op, "JAM", AddressingMode.Implied, true);
        }
    }

    private static Cycle R(MicroOp op) => new(op, false);
    private static Cycle W(MicroOp op) => new(op, true);

    private static void Set(int opcode, string mnemonic, AddressingMode mode, bool illegal, params Cycle[] steps)
    {
        Table[opcode] = steps;
        Infos[opcode] = new OpcodeInfo((byte)opcode, mnemonic, mode, illegal);
    }

    private static Cycle[] Jam()
    {
        return new[]
        {
            R(static c => c.Read(c.PC)),
            R(static c => c.Read(0xFFFF)),
            R(static c => c.Read(0xFFFE)),
            R(static c => c.Read(0xFFFE)),
            R(static c => c.Read(0xFFFF)),
            R(static c => { c.Read(0xFFFF); c.Jammed = true; c._step--; }),
        };
    }

    // --- Addressing-mode templates -------------------------------------------------------------

    private static readonly MicroOp FetchZp = static c => c._addr = c.Read(c.PC++);
    private static readonly MicroOp FetchAdl = static c => c._addr = c.Read(c.PC++);
    private static readonly MicroOp FetchAdh = static c => c._addr |= (ushort)(c.Read(c.PC++) << 8);
    private static readonly MicroOp FetchAdhIndexX = static c => { c._base = (ushort)(c._addr | (c.Read(c.PC++) << 8)); c.IndexBy(c.X); };
    private static readonly MicroOp FetchAdhIndexY = static c => { c._base = (ushort)(c._addr | (c.Read(c.PC++) << 8)); c.IndexBy(c.Y); };
    private static readonly MicroOp ZpIndexX = static c => { c.Read(c._addr); c._addr = (byte)(c._addr + c.X); };
    private static readonly MicroOp ZpIndexY = static c => { c.Read(c._addr); c._addr = (byte)(c._addr + c.Y); };
    private static readonly MicroOp FetchPtr = static c => c._ptr = c.Read(c.PC++);
    private static readonly MicroOp PtrIndexX = static c => { c.Read(c._ptr); c._ptr += c.X; };
    private static readonly MicroOp PtrReadLo = static c => c._addr = c.Read(c._ptr);
    private static readonly MicroOp PtrReadHi = static c => c._addr |= (ushort)(c.Read((byte)(c._ptr + 1)) << 8);
    private static readonly MicroOp PtrReadHiIndexY = static c => { c._base = (ushort)(c._addr | (c.Read((byte)(c._ptr + 1)) << 8)); c.IndexBy(c.Y); };
    private static readonly MicroOp DummyReadWrong = static c => c.Read(c._wrongAddr);
    private static readonly MicroOp RmwRead = static c => c._tmp = c.Read(c._addr);

    private delegate void ReadOperation(Cpu6502 c, byte value);
    private delegate byte WriteOperation(Cpu6502 c);
    private delegate byte RmwOperation(Cpu6502 c, byte value);
    private delegate bool Condition(Cpu6502 c);

    private static void ReadGroup(string m, int imm, int zp, int zpx, int abs, int absx, int absy, int indx, int indy, ReadOperation op)
    {
        Read(m, imm, AddressingMode.Immediate, op);
        Read(m, zp, AddressingMode.ZeroPage, op);
        Read(m, zpx, AddressingMode.ZeroPageX, op);
        Read(m, abs, AddressingMode.Absolute, op);
        Read(m, absx, AddressingMode.AbsoluteX, op);
        Read(m, absy, AddressingMode.AbsoluteY, op);
        Read(m, indx, AddressingMode.IndirectX, op);
        Read(m, indy, AddressingMode.IndirectY, op);
    }

    private static void RmwGroup(string m, int acc, int zp, int zpx, int abs, int absx, RmwOperation op)
    {
        if (acc >= 0)
            Set(acc, m, AddressingMode.Accumulator, false, R(c => { c.Read(c.PC); c.A = op(c, c.A); }));
        Rmw(m, zp, AddressingMode.ZeroPage, op);
        Rmw(m, zpx, AddressingMode.ZeroPageX, op);
        Rmw(m, abs, AddressingMode.Absolute, op);
        Rmw(m, absx, AddressingMode.AbsoluteX, op);
    }

    private static void RmwGroupIllegal(string m, int zp, int zpx, int abs, int absx, int absy, int indx, int indy, RmwOperation op)
    {
        Rmw(m, zp, AddressingMode.ZeroPage, op, true);
        Rmw(m, zpx, AddressingMode.ZeroPageX, op, true);
        Rmw(m, abs, AddressingMode.Absolute, op, true);
        Rmw(m, absx, AddressingMode.AbsoluteX, op, true);
        Rmw(m, absy, AddressingMode.AbsoluteY, op, true);
        Rmw(m, indx, AddressingMode.IndirectX, op, true);
        Rmw(m, indy, AddressingMode.IndirectY, op, true);
    }

    private static void Implied(string m, int opcode, MicroOp op, bool illegal = false)
    {
        Set(opcode, m, AddressingMode.Implied, illegal, R(c => { c.Read(c.PC); op(c); }));
    }

    private static void Branch(string m, int opcode, Condition cond)
    {
        Set(opcode, m, AddressingMode.Relative, false,
            R(c => { c._tmp = c.Read(c.PC++); if (!cond(c)) c.End(); }),
            R(static c =>
            {
                c.Read(c.PC);
                ushort target = (ushort)(c.PC + (sbyte)c._tmp);
                if ((target & 0xFF00) == (c.PC & 0xFF00))
                {
                    c.PC = target;
                    c._pollTwoBack = true;
                    c.End();
                }
                else
                {
                    c._addr = target;
                    c.PC = (ushort)((c.PC & 0xFF00) | (target & 0xFF));
                }
            }),
            R(static c => { c.Read(c.PC); c.PC = c._addr; }));
    }

    private static void Read(string m, int opcode, AddressingMode mode, ReadOperation op, bool illegal = false)
    {
        Cycle final = R(c => { op(c, c.Read(c._addr)); });
        Cycle finalOrCross = R(c =>
        {
            if (!c._pageCross)
            {
                op(c, c.Read(c._addr));
                c.End();
            }
            else
            {
                c.Read(c._wrongAddr);
            }
        });
        Cycle[] steps = mode switch
        {
            AddressingMode.Immediate => new[] { R(c => op(c, c.Read(c.PC++))) },
            AddressingMode.ZeroPage => new[] { R(FetchZp), final },
            AddressingMode.ZeroPageX => new[] { R(FetchZp), R(ZpIndexX), final },
            AddressingMode.ZeroPageY => new[] { R(FetchZp), R(ZpIndexY), final },
            AddressingMode.Absolute => new[] { R(FetchAdl), R(FetchAdh), final },
            AddressingMode.AbsoluteX => new[] { R(FetchAdl), R(FetchAdhIndexX), finalOrCross, final },
            AddressingMode.AbsoluteY => new[] { R(FetchAdl), R(FetchAdhIndexY), finalOrCross, final },
            AddressingMode.IndirectX => new[] { R(FetchPtr), R(PtrIndexX), R(PtrReadLo), R(PtrReadHi), final },
            AddressingMode.IndirectY => new[] { R(FetchPtr), R(PtrReadLo), R(PtrReadHiIndexY), finalOrCross, final },
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
        Set(opcode, m, mode, illegal, steps);
    }

    private static void WriteOp(string m, int opcode, AddressingMode mode, WriteOperation op, bool illegal = false)
    {
        Cycle final = W(c => { byte v = op(c); c.Write(c._addr, v); });
        Cycle[] steps = mode switch
        {
            AddressingMode.ZeroPage => new[] { R(FetchZp), final },
            AddressingMode.ZeroPageX => new[] { R(FetchZp), R(ZpIndexX), final },
            AddressingMode.ZeroPageY => new[] { R(FetchZp), R(ZpIndexY), final },
            AddressingMode.Absolute => new[] { R(FetchAdl), R(FetchAdh), final },
            AddressingMode.AbsoluteX => new[] { R(FetchAdl), R(FetchAdhIndexX), R(DummyReadWrong), final },
            AddressingMode.AbsoluteY => new[] { R(FetchAdl), R(FetchAdhIndexY), R(DummyReadWrong), final },
            AddressingMode.IndirectX => new[] { R(FetchPtr), R(PtrIndexX), R(PtrReadLo), R(PtrReadHi), final },
            AddressingMode.IndirectY => new[] { R(FetchPtr), R(PtrReadLo), R(PtrReadHiIndexY), R(DummyReadWrong), final },
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
        Set(opcode, m, mode, illegal, steps);
    }

    private static void Rmw(string m, int opcode, AddressingMode mode, RmwOperation op, bool illegal = false)
    {
        Cycle writeOld = W(c => { c.Write(c._addr, c._tmp); c._tmp = op(c, c._tmp); });
        Cycle writeNew = W(static c => c.Write(c._addr, c._tmp));
        Cycle[] steps = mode switch
        {
            AddressingMode.ZeroPage => new[] { R(FetchZp), R(RmwRead), writeOld, writeNew },
            AddressingMode.ZeroPageX => new[] { R(FetchZp), R(ZpIndexX), R(RmwRead), writeOld, writeNew },
            AddressingMode.Absolute => new[] { R(FetchAdl), R(FetchAdh), R(RmwRead), writeOld, writeNew },
            AddressingMode.AbsoluteX => new[] { R(FetchAdl), R(FetchAdhIndexX), R(DummyReadWrong), R(RmwRead), writeOld, writeNew },
            AddressingMode.AbsoluteY => new[] { R(FetchAdl), R(FetchAdhIndexY), R(DummyReadWrong), R(RmwRead), writeOld, writeNew },
            AddressingMode.IndirectX => new[] { R(FetchPtr), R(PtrIndexX), R(PtrReadLo), R(PtrReadHi), R(RmwRead), writeOld, writeNew },
            AddressingMode.IndirectY => new[] { R(FetchPtr), R(PtrReadLo), R(PtrReadHiIndexY), R(DummyReadWrong), R(RmwRead), writeOld, writeNew },
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
        Set(opcode, m, mode, illegal, steps);
    }

    #endregion
}
