namespace Tedd.Commodore.Emulator.Cpu;

/// <summary>The CB, ED, DDCB and FDCB opcode groups.</summary>
public sealed partial class Z80
{
    /// <summary>CB prefix: rotates/shifts, BIT, RES, SET on registers and (HL).</summary>
    private void ExecuteCb()
    {
        byte op = Fetch();
        IncR();
        int group = op >> 6, n = (op >> 3) & 7, r = op & 7;
        if (r == 6)
        {
            ushort a = HL;
            byte v = Read(a);
            switch (group)
            {
                case 0: Write(a, Shift(n, v)); _t += 15; break;
                case 1: Bit(n, v, WZ >> 8); _t += 12; break;
                case 2: Write(a, (byte)(v & ~(1 << n))); _t += 15; Q = 0; break;
                default: Write(a, (byte)(v | (1 << n))); _t += 15; Q = 0; break;
            }
        }
        else
        {
            byte v = GetRPlain(r);
            switch (group)
            {
                case 0: SetRPlain(r, Shift(n, v)); break;
                case 1: Bit(n, v, v); break;
                case 2: SetRPlain(r, (byte)(v & ~(1 << n))); Q = 0; break;
                default: SetRPlain(r, (byte)(v | (1 << n))); Q = 0; break;
            }
            _t += 8;
        }
    }

    /// <summary>
    /// DD CB d op / FD CB d op: the operation is done on (IX+d); the undocumented forms with r != 6 also copy the
    /// result into register r. R is not incremented for the op byte (it is not an M1 cycle).
    /// </summary>
    private void ExecuteIndexedCb()
    {
        sbyte d = (sbyte)Fetch();
        byte op = Fetch();
        ushort a = (ushort)((_idx == 1 ? IX : IY) + d);
        WZ = a;
        int group = op >> 6, n = (op >> 3) & 7, r = op & 7;
        byte v = Read(a);
        byte res;
        switch (group)
        {
            case 0: res = Shift(n, v); break;
            case 1: Bit(n, v, a >> 8); _t += 16; return;          // 20 with the prefix
            case 2: res = (byte)(v & ~(1 << n)); Q = 0; break;
            default: res = (byte)(v | (1 << n)); Q = 0; break;
        }
        Write(a, res);
        if (r != 6) SetRPlain(r, res);
        _t += 19;                                              // 23 with the prefix
    }

    /// <summary>ED prefix: I/O through BC, 16-bit arithmetic, block instructions, interrupt modes, LD I/R, RRD/RLD.</summary>
    private void ExecuteEd()
    {
        byte op = Fetch();
        IncR();
        switch (op)
        {
            case 0x40: case 0x48: case 0x50: case 0x58: case 0x60: case 0x68: case 0x70: case 0x78: // IN r,(C)
            {
                byte v = In(BC);
                WZ = (ushort)(BC + 1);
                int r = (op >> 3) & 7;
                if (r != 6) SetRPlain(r, v);
                SetF((F & FlagC) | Sz53p[v]);
                _t += 12;
                break;
            }
            case 0x41: case 0x49: case 0x51: case 0x59: case 0x61: case 0x69: case 0x71: case 0x79: // OUT (C),r
            {
                int r = (op >> 3) & 7;
                Out(BC, r == 6 ? (byte)0 : GetRPlain(r));
                WZ = (ushort)(BC + 1);
                _t += 12;
                Q = 0;
                break;
            }
            case 0x42: Sbc16(BC); _t += 15; break;
            case 0x52: Sbc16(DE); _t += 15; break;
            case 0x62: Sbc16(HL); _t += 15; break;
            case 0x72: Sbc16(SP); _t += 15; break;
            case 0x4A: Adc16(BC); _t += 15; break;
            case 0x5A: Adc16(DE); _t += 15; break;
            case 0x6A: Adc16(HL); _t += 15; break;
            case 0x7A: Adc16(SP); _t += 15; break;
            case 0x43: case 0x53: case 0x63: case 0x73: // LD (nn),rp
            {
                ushort a = Fetch16();
                Write16(a, GetRp((op >> 4) & 3));
                WZ = (ushort)(a + 1);
                _t += 20;
                Q = 0;
                break;
            }
            case 0x4B: case 0x5B: case 0x6B: case 0x7B: // LD rp,(nn)
            {
                ushort a = Fetch16();
                SetRp((op >> 4) & 3, Read16(a));
                WZ = (ushort)(a + 1);
                _t += 20;
                Q = 0;
                break;
            }
            case 0x44: case 0x4C: case 0x54: case 0x5C: case 0x64: case 0x6C: case 0x74: case 0x7C: // NEG
            {
                byte v = A;
                A = 0;
                Sub8(v, 0);
                _t += 8;
                break;
            }
            case 0x45: case 0x4D: case 0x55: case 0x5D: case 0x65: case 0x6D: case 0x75: case 0x7D: // RETN / RETI
                Iff1 = Iff2;
                PC = WZ = Pop();
                _t += 14;
                Q = 0;
                break;
            case 0x46: case 0x4E: case 0x66: case 0x6E: InterruptMode = 0; _t += 8; Q = 0; break;
            case 0x56: case 0x76: InterruptMode = 1; _t += 8; Q = 0; break;
            case 0x5E: case 0x7E: InterruptMode = 2; _t += 8; Q = 0; break;
            case 0x47: I = A; _t += 9; Q = 0; break;                                                   // LD I,A
            case 0x4F: R = A; _t += 9; Q = 0; break;                                                   // LD R,A
            case 0x57: A = I; SetF((F & FlagC) | (Sz53p[A] & ~FlagP) | (Iff2 ? FlagP : 0)); _t += 9; break; // LD A,I
            case 0x5F: A = R; SetF((F & FlagC) | (Sz53p[A] & ~FlagP) | (Iff2 ? FlagP : 0)); _t += 9; break; // LD A,R
            case 0x67: // RRD
            {
                byte v = Read(HL);
                Write(HL, (byte)((A << 4) | (v >> 4)));
                A = (byte)((A & 0xF0) | (v & 0x0F));
                WZ = (ushort)(HL + 1);
                SetF((F & FlagC) | Sz53p[A]);
                _t += 18;
                break;
            }
            case 0x6F: // RLD
            {
                byte v = Read(HL);
                Write(HL, (byte)((v << 4) | (A & 0x0F)));
                A = (byte)((A & 0xF0) | (v >> 4));
                WZ = (ushort)(HL + 1);
                SetF((F & FlagC) | Sz53p[A]);
                _t += 18;
                break;
            }
            case 0xA0: Ldi(1); break;
            case 0xA8: Ldi(-1); break;
            case 0xB0: Ldir(1); break;
            case 0xB8: Ldir(-1); break;
            case 0xA1: Cpi(1); break;
            case 0xA9: Cpi(-1); break;
            case 0xB1: Cpir(1); break;
            case 0xB9: Cpir(-1); break;
            case 0xA2: BlockIo(true, 1); break;
            case 0xAA: BlockIo(true, -1); break;
            case 0xB2: BlockIoRepeat(true, 1); break;
            case 0xBA: BlockIoRepeat(true, -1); break;
            case 0xA3: BlockIo(false, 1); break;
            case 0xAB: BlockIo(false, -1); break;
            case 0xB3: BlockIoRepeat(false, 1); break;
            case 0xBB: BlockIoRepeat(false, -1); break;
            default: _t += 8; Q = 0; break;                                                            // ED NOP
        }
    }
}
