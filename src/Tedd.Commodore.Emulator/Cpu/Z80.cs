using System;
using System.Runtime.CompilerServices;
using Tedd.Commodore.Emulator.Bus;

namespace Tedd.Commodore.Emulator.Cpu;

/// <summary>
/// A Z80 bus: the 16-bit memory bus plus the 16-bit I/O port space of IN/OUT (the full BC or A:n address is
/// passed, as the chip puts it on the address lines).
/// </summary>
public interface IZ80Bus : IBus
{
    byte In(ushort port);
    void Out(ushort port, byte value);
}

/// <summary>
/// Zilog Z80 (the Z80A in the Commodore 128). Instruction stepped: <see cref="Step"/> executes one whole
/// instruction (or one interrupt acknowledge, or one HALT cycle) and returns the number of T-states it took, so
/// a machine that clocks its chips per cycle keeps a T-state budget and calls <see cref="Step"/> whenever it goes
/// positive. Cycle counts are the documented ones (Zilog Z80 CPU User Manual), including the conditional
/// branches, and the instruction set is complete: all documented opcodes, the undocumented ones (IXH/IXL/IYH/IYL,
/// SLL, IN F,(C), OUT (C),0, duplicate NEG/RETN/IM, the DDCB/FDCB result-to-register forms), the undocumented
/// X/Y flag bits (bits 3 and 5), the internal WZ/MEMPTR register (visible through BIT n,(HL)) and the Q
/// register that decides the X/Y bits of SCF/CCF. Verified against the SingleStepTests/z80 vectors
/// (<c>HARTE_Z80_TESTS</c>).
/// <para>No allocations after construction; the whole decoder is <c>switch</c> based.</para>
/// </summary>
public sealed partial class Z80
{
    public const byte FlagS = 0x80;
    public const byte FlagZ = 0x40;
    public const byte FlagY = 0x20;
    public const byte FlagH = 0x10;
    public const byte FlagX = 0x08;
    public const byte FlagP = 0x04;
    public const byte FlagV = FlagP;
    public const byte FlagN = 0x02;
    public const byte FlagC = 0x01;
    private const byte FlagXY = FlagX | FlagY;
    private const byte FlagSZP = FlagS | FlagZ | FlagP;

    /// <summary>NMI vector.</summary>
    public const ushort NmiVector = 0x0066;
    /// <summary>IM 1 vector (RST 38h).</summary>
    public const ushort Im1Vector = 0x0038;

    /// <summary>S, Z, X, Y and parity flags for every 8-bit value.</summary>
    private static readonly byte[] Sz53p = BuildSz53p();

    private static byte[] BuildSz53p()
    {
        var t = new byte[256];
        for (int i = 0; i < 256; i++)
        {
            int bits = 0;
            for (int b = i; b != 0; b >>= 1) bits += b & 1;
            t[i] = (byte)((i & (FlagS | FlagXY)) | (i == 0 ? FlagZ : 0) | ((bits & 1) == 0 ? FlagP : 0));
        }
        return t;
    }

    private readonly IZ80Bus _bus;

    /// <summary>
    /// When set, every memory read and write the CPU performs is recorded here (the memory viewer's read/write
    /// highlighting); I/O port accesses are not memory and are not recorded. null costs one branch per access.
    /// </summary>
    public MemoryAccessTracker? AccessTracker;

    // Main register set
    public byte A, F, B, C, D, E, H, L;
    // Alternate register set
    public byte A2, F2, B2, C2, D2, E2, H2, L2;
    public byte I, R;
    public ushort IX, IY, SP, PC;
    /// <summary>The internal WZ (MEMPTR) register; its high byte leaks into the X/Y flags of BIT n,(HL).</summary>
    public ushort WZ;
    /// <summary>The internal Q "flags were just written" latch: F after an instruction that changed the flags, else 0.</summary>
    public byte Q;
    public bool Iff1, Iff2;
    /// <summary>Interrupt mode 0, 1 or 2 (mode 0 executes RST 38h like mode 1: the data bus is assumed to read $FF).</summary>
    public int InterruptMode;
    /// <summary>True after HALT until an interrupt is accepted.</summary>
    public bool Halted;

    /// <summary>/INT line, level sensitive: true = asserted.</summary>
    public bool Irq;
    private bool _nmiLine;
    private bool _nmiPending;
    private bool _eiDelay;

    /// <summary>T-states executed since construction.</summary>
    public long Cycles;
    /// <summary>Instructions executed (interrupt acknowledges and HALT cycles count as one).</summary>
    public long Instructions;

    private int _idx;   // 0 = HL, 1 = IX, 2 = IY (DD / FD prefix in effect)
    private int _t;     // T-states of the instruction being executed

    public Z80(IZ80Bus bus)
    {
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        Reset();
    }

    public IZ80Bus Bus => _bus;

    // 16-bit register pairs
    public ushort AF { get => (ushort)((A << 8) | F); set { A = (byte)(value >> 8); F = (byte)value; } }
    public ushort BC { get => (ushort)((B << 8) | C); set { B = (byte)(value >> 8); C = (byte)value; } }
    public ushort DE { get => (ushort)((D << 8) | E); set { D = (byte)(value >> 8); E = (byte)value; } }
    public ushort HL { get => (ushort)((H << 8) | L); set { H = (byte)(value >> 8); L = (byte)value; } }
    public ushort AF2 { get => (ushort)((A2 << 8) | F2); set { A2 = (byte)(value >> 8); F2 = (byte)value; } }
    public ushort BC2 { get => (ushort)((B2 << 8) | C2); set { B2 = (byte)(value >> 8); C2 = (byte)value; } }
    public ushort DE2 { get => (ushort)((D2 << 8) | E2); set { D2 = (byte)(value >> 8); E2 = (byte)value; } }
    public ushort HL2 { get => (ushort)((H2 << 8) | L2); set { H2 = (byte)(value >> 8); L2 = (byte)value; } }

    public bool FlagCarry => (F & FlagC) != 0;
    public bool FlagZero => (F & FlagZ) != 0;
    public bool FlagSign => (F & FlagS) != 0;
    public bool FlagParityOverflow => (F & FlagP) != 0;
    public bool FlagHalfCarry => (F & FlagH) != 0;
    public bool FlagSubtract => (F & FlagN) != 0;

    /// <summary>True when an NMI is latched and will be taken at the next <see cref="Step"/>.</summary>
    public bool NmiPending => _nmiPending;

    /// <summary>
    /// /NMI line level, true = asserted. The chip is edge triggered: a false → true transition latches the
    /// request, so the line can be sampled every machine cycle.
    /// </summary>
    public void SetNmi(bool asserted)
    {
        if (asserted && !_nmiLine)
            _nmiPending = true;
        _nmiLine = asserted;
    }

    /// <summary>/RESET: PC = 0, I = R = 0, IFF1 = IFF2 = 0, IM 0, AF = SP = $FFFF (the other registers keep their values).</summary>
    public void Reset()
    {
        PC = 0;
        I = 0;
        R = 0;
        Iff1 = Iff2 = false;
        InterruptMode = 0;
        Halted = false;
        AF = 0xFFFF;
        SP = 0xFFFF;
        WZ = 0;
        Q = 0;
        _nmiPending = false;
        _eiDelay = false;
        _idx = 0;
    }

    /// <summary>Executes one instruction (or takes a pending interrupt). Returns the T-states used.</summary>
    public int Step()
    {
        _t = 0;
        bool eiDelay = _eiDelay;
        _eiDelay = false;
        Instructions++;

        if (_nmiPending)
        {
            _nmiPending = false;
            Halted = false;
            IncR();
            Iff2 = Iff1;
            Iff1 = false;
            Push(PC);
            PC = NmiVector;
            WZ = PC;
            Q = 0;
            _t = 11;
        }
        else if (Irq && Iff1 && !eiDelay)
        {
            Halted = false;
            IncR();
            Iff1 = Iff2 = false;
            Push(PC);
            if (InterruptMode == 2)
            {
                PC = Read16((ushort)((I << 8) | 0xFF));
                _t = 19;
            }
            else
            {
                PC = Im1Vector;
                _t = 13;
            }
            WZ = PC;
            Q = 0;
        }
        else if (Halted)
        {
            IncR();
            _t = 4;
        }
        else
        {
            byte op = Fetch();
            IncR();
            _idx = 0;
            while (op == 0xDD || op == 0xFD)
            {
                _idx = op == 0xDD ? 1 : 2;
                _t += 4;
                Q = 0;                                         // a prefix behaves like an instruction that leaves F alone
                op = Fetch();
                IncR();
            }
            if (op == 0xCB)
            {
                if (_idx != 0) ExecuteIndexedCb(); else ExecuteCb();
            }
            else if (op == 0xED)
            {
                _idx = 0;
                ExecuteEd();
            }
            else
            {
                Execute(op);
            }
        }

        Cycles += _t;
        return _t;
    }

    // ------------------------------------------------------------------------------------------------------
    // Bus helpers
    // ------------------------------------------------------------------------------------------------------

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
    private ushort Read16(ushort address) => (ushort)(Read(address) | (Read((ushort)(address + 1)) << 8));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Write16(ushort address, ushort value)
    {
        Write(address, (byte)value);
        Write((ushort)(address + 1), (byte)(value >> 8));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte Fetch() => Read(PC++);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ushort Fetch16()
    {
        ushort v = Read16(PC);
        PC += 2;
        return v;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Push(ushort value)
    {
        SP--;
        Write(SP, (byte)(value >> 8));
        SP--;
        Write(SP, (byte)value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ushort Pop()
    {
        ushort v = Read16(SP);
        SP += 2;
        return v;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void IncR() => R = (byte)((R & 0x80) | ((R + 1) & 0x7F));

    // ------------------------------------------------------------------------------------------------------
    // Register access with the DD/FD prefix taken into account
    // ------------------------------------------------------------------------------------------------------

    /// <summary>HL, IX or IY depending on the prefix in effect.</summary>
    private ushort Hl
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _idx == 0 ? HL : _idx == 1 ? IX : IY;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set { if (_idx == 0) HL = value; else if (_idx == 1) IX = value; else IY = value; }
    }

    private byte Hh
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _idx == 0 ? H : (byte)((_idx == 1 ? IX : IY) >> 8);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set { if (_idx == 0) H = value; else if (_idx == 1) IX = (ushort)((value << 8) | (IX & 0xFF)); else IY = (ushort)((value << 8) | (IY & 0xFF)); }
    }

    private byte Ll
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _idx == 0 ? L : (byte)(_idx == 1 ? IX : IY);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set { if (_idx == 0) L = value; else if (_idx == 1) IX = (ushort)((IX & 0xFF00) | value); else IY = (ushort)((IY & 0xFF00) | value); }
    }

    /// <summary>
    /// Address of the (HL) / (IX+d) / (IY+d) operand. With a prefix the displacement is fetched here, WZ gets
    /// the address and the extra 8 T-states of the indexed form are added (5 for LD (IX+d),n, see the caller).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ushort MemOperand(int extra = 8)
    {
        if (_idx == 0) return HL;
        sbyte d = (sbyte)Fetch();
        _t += extra;
        WZ = (ushort)((_idx == 1 ? IX : IY) + d);
        return WZ;
    }

    /// <summary>Register B, C, D, E, H/IXH/IYH, L/IXL/IYL, -, A for r = 0..7 (r = 6 is not a register).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte GetR(int r) => r switch
    {
        0 => B, 1 => C, 2 => D, 3 => E, 4 => Hh, 5 => Ll, _ => A,
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SetR(int r, byte v)
    {
        switch (r)
        {
            case 0: B = v; break;
            case 1: C = v; break;
            case 2: D = v; break;
            case 3: E = v; break;
            case 4: Hh = v; break;
            case 5: Ll = v; break;
            default: A = v; break;
        }
    }

    /// <summary>Like <see cref="GetR"/> but always the real H and L (used next to a (IX+d) operand).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte GetRPlain(int r) => r switch
    {
        0 => B, 1 => C, 2 => D, 3 => E, 4 => H, 5 => L, _ => A,
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SetRPlain(int r, byte v)
    {
        switch (r)
        {
            case 0: B = v; break;
            case 1: C = v; break;
            case 2: D = v; break;
            case 3: E = v; break;
            case 4: H = v; break;
            case 5: L = v; break;
            default: A = v; break;
        }
    }

    /// <summary>BC, DE, HL/IX/IY, SP for p = 0..3.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ushort GetRp(int p) => p switch { 0 => BC, 1 => DE, 2 => Hl, _ => SP };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SetRp(int p, ushort v)
    {
        switch (p)
        {
            case 0: BC = v; break;
            case 1: DE = v; break;
            case 2: Hl = v; break;
            default: SP = v; break;
        }
    }

    /// <summary>Condition NZ, Z, NC, C, PO, PE, P, M for cc = 0..7.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool Condition(int cc)
    {
        int flag = (cc >> 1) switch { 0 => FlagZ, 1 => FlagC, 2 => FlagP, _ => FlagS };
        return ((F & flag) != 0) == ((cc & 1) != 0);
    }

    // ------------------------------------------------------------------------------------------------------
    // ALU
    // ------------------------------------------------------------------------------------------------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SetF(int f) => Q = F = (byte)f;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Add8(byte v, int carry)
    {
        int r = A + v + carry;
        byte res = (byte)r;
        SetF((Sz53p[res] & ~FlagP) | ((r >> 8) & FlagC) | ((A ^ v ^ res) & FlagH) | ((((A ^ v ^ 0x80) & (v ^ res)) >> 5) & FlagV));
        A = res;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Sub8(byte v, int carry)
    {
        int r = A - v - carry;
        byte res = (byte)r;
        SetF((Sz53p[res] & ~FlagP) | FlagN | ((r >> 8) & FlagC) | ((A ^ v ^ res) & FlagH) | ((((A ^ v) & (A ^ res)) >> 5) & FlagV));
        A = res;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Cp8(byte v)
    {
        int r = A - v;
        byte res = (byte)r;
        // X and Y come from the operand, not from the result.
        SetF((Sz53p[res] & (FlagS | FlagZ)) | (v & FlagXY) | FlagN | ((r >> 8) & FlagC) | ((A ^ v ^ res) & FlagH) | ((((A ^ v) & (A ^ res)) >> 5) & FlagV));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void And8(byte v) { A &= v; SetF(Sz53p[A] | FlagH); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Or8(byte v) { A |= v; SetF(Sz53p[A]); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Xor8(byte v) { A ^= v; SetF(Sz53p[A]); }

    /// <summary>ADD/ADC/SUB/SBC/AND/XOR/OR/CP for the operation index 0..7.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Alu(int op, byte v)
    {
        switch (op)
        {
            case 0: Add8(v, 0); break;
            case 1: Add8(v, F & FlagC); break;
            case 2: Sub8(v, 0); break;
            case 3: Sub8(v, F & FlagC); break;
            case 4: And8(v); break;
            case 5: Xor8(v); break;
            case 6: Or8(v); break;
            default: Cp8(v); break;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte Inc8(byte v)
    {
        byte r = (byte)(v + 1);
        SetF((F & FlagC) | (Sz53p[r] & ~FlagP) | (r == 0x80 ? FlagV : 0) | ((r & 0x0F) == 0 ? FlagH : 0));
        return r;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte Dec8(byte v)
    {
        byte r = (byte)(v - 1);
        SetF((F & FlagC) | FlagN | (Sz53p[r] & ~FlagP) | (r == 0x7F ? FlagV : 0) | ((r & 0x0F) == 0x0F ? FlagH : 0));
        return r;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ushort Add16(ushort hl, ushort v)
    {
        int r = hl + v;
        WZ = (ushort)(hl + 1);
        SetF((F & FlagSZP) | ((r >> 16) & FlagC) | (((hl ^ v ^ r) >> 8) & FlagH) | ((r >> 8) & FlagXY));
        return (ushort)r;
    }

    private void Adc16(ushort v)
    {
        ushort hl = HL;
        int r = hl + v + (F & FlagC);
        ushort res = (ushort)r;
        WZ = (ushort)(hl + 1);
        SetF(((res >> 8) & (FlagS | FlagXY)) | (res == 0 ? FlagZ : 0) | (((hl ^ v ^ res) >> 8) & FlagH) | ((r >> 16) & FlagC) | ((((hl ^ v ^ 0x8000) & (v ^ res)) >> 13) & FlagV));
        HL = res;
    }

    private void Sbc16(ushort v)
    {
        ushort hl = HL;
        int r = hl - v - (F & FlagC);
        ushort res = (ushort)r;
        WZ = (ushort)(hl + 1);
        SetF(((res >> 8) & (FlagS | FlagXY)) | (res == 0 ? FlagZ : 0) | (((hl ^ v ^ res) >> 8) & FlagH) | FlagN | ((r >> 16) & FlagC) | ((((hl ^ v) & (hl ^ res)) >> 13) & FlagV));
        HL = res;
    }

    private void Daa()
    {
        int a = A;
        int f = F;
        int add = 0;
        int carry = f & FlagC;
        if ((f & FlagH) != 0 || (a & 0x0F) > 9) add = 0x06;
        if (carry != 0 || a > 0x99) { add |= 0x60; carry = FlagC; }
        byte r = (byte)((f & FlagN) != 0 ? a - add : a + add);
        SetF(Sz53p[r] | carry | (f & FlagN) | ((a ^ r) & FlagH));
        A = r;
    }

    /// <summary>RLC, RRC, RL, RR, SLA, SRA, SLL, SRL for op = 0..7 (the CB rotate/shift group).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte Shift(int op, byte v)
    {
        int r;
        int c;
        switch (op)
        {
            case 0: r = (v << 1) | (v >> 7); c = v >> 7; break;
            case 1: r = (v >> 1) | (v << 7); c = v & 1; break;
            case 2: r = (v << 1) | (F & FlagC); c = v >> 7; break;
            case 3: r = (v >> 1) | ((F & FlagC) << 7); c = v & 1; break;
            case 4: r = v << 1; c = v >> 7; break;
            case 5: r = (v & 0x80) | (v >> 1); c = v & 1; break;
            case 6: r = (v << 1) | 1; c = v >> 7; break;
            default: r = v >> 1; c = v & 1; break;
        }
        byte res = (byte)r;
        SetF(Sz53p[res] | c);
        return res;
    }

    /// <summary>BIT n,v with the X/Y bits taken from <paramref name="xy"/> (the register, WZ high or the indexed address high).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Bit(int n, byte v, int xy)
    {
        SetF((F & FlagC) | FlagH | (Sz53p[v & (1 << n)] & FlagSZP) | (xy & FlagXY));
    }

    // ------------------------------------------------------------------------------------------------------
    // Block transfer / search / I/O
    // ------------------------------------------------------------------------------------------------------

    private void Ldi(int step)
    {
        byte v = Read(HL);
        Write(DE, v);
        HL = (ushort)(HL + step);
        DE = (ushort)(DE + step);
        BC = (ushort)(BC - 1);
        int n = A + v;
        SetF((F & (FlagS | FlagZ | FlagC)) | (BC != 0 ? FlagP : 0) | ((n << 4) & FlagY) | (n & FlagX));
        _t += 16;
    }

    private void Ldir(int step)
    {
        Ldi(step);
        if (BC != 0)
        {
            PC -= 2;
            WZ = (ushort)(PC + 1);
            _t += 5;
            SetF((F & ~FlagXY) | ((PC >> 8) & FlagXY));       // undocumented: X/Y from the instruction address
        }
    }

    private void Cpi(int step)
    {
        byte v = Read(HL);
        int r = A - v;
        byte res = (byte)r;
        int h = (A ^ v ^ res) & FlagH;
        HL = (ushort)(HL + step);
        BC = (ushort)(BC - 1);
        WZ = (ushort)(WZ + step);
        int n = res - (h >> 4);
        SetF((F & FlagC) | (Sz53p[res] & (FlagS | FlagZ)) | h | FlagN | (BC != 0 ? FlagP : 0) | ((n << 4) & FlagY) | (n & FlagX));
        _t += 16;
    }

    private void Cpir(int step)
    {
        Cpi(step);
        if (BC != 0 && (F & FlagZ) == 0)
        {
            PC -= 2;
            WZ = (ushort)(PC + 1);
            _t += 5;
            SetF((F & ~FlagXY) | ((PC >> 8) & FlagXY));       // undocumented: X/Y from the instruction address
        }
    }

    /// <summary>INI/IND (in = true) and OUTI/OUTD (in = false) with the undocumented flag rules.</summary>
    private void BlockIo(bool input, int step)
    {
        byte v;
        int t;
        if (input)
        {
            v = In(BC);
            WZ = (ushort)(BC + step);
            B--;
            Write(HL, v);
            HL = (ushort)(HL + step);
            t = v + ((C + step) & 0xFF);
        }
        else
        {
            v = Read(HL);
            B--;
            Out(BC, v);
            WZ = (ushort)(BC + step);
            HL = (ushort)(HL + step);
            t = v + L;
        }
        int f = (Sz53p[B] & (FlagS | FlagZ | FlagXY)) | ((v >> 6) & FlagN);
        if (t > 255) f |= FlagH | FlagC;
        f |= Sz53p[(t & 7) ^ B] & FlagP;
        SetF(f);
        _t += 16;
    }

    private void BlockIoRepeat(bool input, int step)
    {
        BlockIo(input, step);
        if (B != 0)
        {
            PC -= 2;
            WZ = (ushort)(PC + 1);
            _t += 5;
            // Undocumented: when the instruction repeats, X/Y come from the high byte of the address of the
            // instruction and H/P are adjusted from the B and C flags (see the block I/O flag research).
            int f = (F & ~FlagXY) | ((PC >> 8) & FlagXY);
            if ((f & FlagC) != 0)
            {
                if ((f & FlagN) != 0)
                {
                    f = (f & ~FlagH) | ((B & 0x0F) == 0x00 ? FlagH : 0);
                    f ^= Sz53p[(B - 1) & 7] & FlagP;
                }
                else
                {
                    f = (f & ~FlagH) | ((B & 0x0F) == 0x0F ? FlagH : 0);
                    f ^= Sz53p[(B + 1) & 7] & FlagP;
                }
            }
            else
            {
                f ^= Sz53p[B & 7] & FlagP;
            }
            f ^= FlagP;
            SetF(f);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte In(ushort port) => _bus.In(port);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Out(ushort port, byte value) => _bus.Out(port, value);

    /// <summary>A one-line register dump.</summary>
    public override string ToString() =>
        $"PC={PC:X4} SP={SP:X4} AF={AF:X4} BC={BC:X4} DE={DE:X4} HL={HL:X4} IX={IX:X4} IY={IY:X4} I={I:X2} R={R:X2} IFF1={(Iff1 ? 1 : 0)} IM{InterruptMode} T={Cycles}";
}
