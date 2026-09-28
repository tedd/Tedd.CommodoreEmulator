using System.Runtime.CompilerServices;

namespace Tedd.Commodore.Emulator.Cpu;

/// <summary>The unprefixed opcodes (with the DD/FD prefix applied through <see cref="Hl"/> / <see cref="MemOperand"/>).</summary>
public sealed partial class Z80
{
    private void Execute(byte op)
    {
        switch (op)
        {
            case 0x00: _t += 4; Q = 0; break;                                                          // NOP
            case 0x01: BC = Fetch16(); _t += 10; Q = 0; break;                                         // LD BC,nn
            case 0x02: Write(BC, A); WZ = (ushort)((A << 8) | ((BC + 1) & 0xFF)); _t += 7; Q = 0; break; // LD (BC),A
            case 0x03: BC++; _t += 6; Q = 0; break;                                                    // INC BC
            case 0x04: B = Inc8(B); _t += 4; break;
            case 0x05: B = Dec8(B); _t += 4; break;
            case 0x06: B = Fetch(); _t += 7; Q = 0; break;
            case 0x07: { int c = A >> 7; A = (byte)((A << 1) | c); SetF((F & FlagSZP) | (A & FlagXY) | c); _t += 4; break; } // RLCA
            case 0x08: { ushort t = AF; AF = AF2; AF2 = t; _t += 4; Q = 0; break; }                    // EX AF,AF'
            case 0x09: Hl = Add16(Hl, BC); _t += 11; break;                                            // ADD HL,BC
            case 0x0A: A = Read(BC); WZ = (ushort)(BC + 1); _t += 7; Q = 0; break;                     // LD A,(BC)
            case 0x0B: BC--; _t += 6; Q = 0; break;
            case 0x0C: C = Inc8(C); _t += 4; break;
            case 0x0D: C = Dec8(C); _t += 4; break;
            case 0x0E: C = Fetch(); _t += 7; Q = 0; break;
            case 0x0F: { int c = A & 1; A = (byte)((A >> 1) | (c << 7)); SetF((F & FlagSZP) | (A & FlagXY) | c); _t += 4; break; } // RRCA

            case 0x10: // DJNZ e
            {
                sbyte d = (sbyte)Fetch();
                B--;
                if (B != 0) { PC = (ushort)(PC + d); WZ = PC; _t += 13; }
                else _t += 8;
                Q = 0;
                break;
            }
            case 0x11: DE = Fetch16(); _t += 10; Q = 0; break;
            case 0x12: Write(DE, A); WZ = (ushort)((A << 8) | ((DE + 1) & 0xFF)); _t += 7; Q = 0; break;
            case 0x13: DE++; _t += 6; Q = 0; break;
            case 0x14: D = Inc8(D); _t += 4; break;
            case 0x15: D = Dec8(D); _t += 4; break;
            case 0x16: D = Fetch(); _t += 7; Q = 0; break;
            case 0x17: { int c = A >> 7; A = (byte)((A << 1) | (F & FlagC)); SetF((F & FlagSZP) | (A & FlagXY) | c); _t += 4; break; } // RLA
            case 0x18: { sbyte d = (sbyte)Fetch(); PC = (ushort)(PC + d); WZ = PC; _t += 12; Q = 0; break; } // JR e
            case 0x19: Hl = Add16(Hl, DE); _t += 11; break;
            case 0x1A: A = Read(DE); WZ = (ushort)(DE + 1); _t += 7; Q = 0; break;
            case 0x1B: DE--; _t += 6; Q = 0; break;
            case 0x1C: E = Inc8(E); _t += 4; break;
            case 0x1D: E = Dec8(E); _t += 4; break;
            case 0x1E: E = Fetch(); _t += 7; Q = 0; break;
            case 0x1F: { int c = A & 1; A = (byte)((A >> 1) | ((F & FlagC) << 7)); SetF((F & FlagSZP) | (A & FlagXY) | c); _t += 4; break; } // RRA

            case 0x20: JrCc((F & FlagZ) == 0); break;
            case 0x21: Hl = Fetch16(); _t += 10; Q = 0; break;
            case 0x22: { ushort a = Fetch16(); Write16(a, Hl); WZ = (ushort)(a + 1); _t += 16; Q = 0; break; } // LD (nn),HL
            case 0x23: Hl++; _t += 6; Q = 0; break;
            case 0x24: Hh = Inc8(Hh); _t += 4; break;
            case 0x25: Hh = Dec8(Hh); _t += 4; break;
            case 0x26: Hh = Fetch(); _t += 7; Q = 0; break;
            case 0x27: Daa(); _t += 4; break;
            case 0x28: JrCc((F & FlagZ) != 0); break;
            case 0x29: { ushort h = Hl; Hl = Add16(h, h); _t += 11; break; }
            case 0x2A: { ushort a = Fetch16(); Hl = Read16(a); WZ = (ushort)(a + 1); _t += 16; Q = 0; break; } // LD HL,(nn)
            case 0x2B: Hl--; _t += 6; Q = 0; break;
            case 0x2C: Ll = Inc8(Ll); _t += 4; break;
            case 0x2D: Ll = Dec8(Ll); _t += 4; break;
            case 0x2E: Ll = Fetch(); _t += 7; Q = 0; break;
            case 0x2F: A = (byte)~A; SetF((F & (FlagSZP | FlagC)) | FlagH | FlagN | (A & FlagXY)); _t += 4; break; // CPL

            case 0x30: JrCc((F & FlagC) == 0); break;
            case 0x31: SP = Fetch16(); _t += 10; Q = 0; break;
            case 0x32: { ushort a = Fetch16(); Write(a, A); WZ = (ushort)((A << 8) | ((a + 1) & 0xFF)); _t += 13; Q = 0; break; } // LD (nn),A
            case 0x33: SP++; _t += 6; Q = 0; break;
            case 0x34: { ushort a = MemOperand(); Write(a, Inc8(Read(a))); _t += 11; break; }          // INC (HL)
            case 0x35: { ushort a = MemOperand(); Write(a, Dec8(Read(a))); _t += 11; break; }          // DEC (HL)
            case 0x36: { ushort a = MemOperand(5); Write(a, Fetch()); _t += 10; Q = 0; break; }        // LD (HL),n
            case 0x37: SetF((F & FlagSZP) | FlagC | (((Q ^ F) | A) & FlagXY)); _t += 4; break;         // SCF
            case 0x38: JrCc((F & FlagC) != 0); break;
            case 0x39: Hl = Add16(Hl, SP); _t += 11; break;
            case 0x3A: { ushort a = Fetch16(); A = Read(a); WZ = (ushort)(a + 1); _t += 13; Q = 0; break; } // LD A,(nn)
            case 0x3B: SP--; _t += 6; Q = 0; break;
            case 0x3C: A = Inc8(A); _t += 4; break;
            case 0x3D: A = Dec8(A); _t += 4; break;
            case 0x3E: A = Fetch(); _t += 7; Q = 0; break;
            case 0x3F: SetF((F & FlagSZP) | ((F & FlagC) << 4) | ((F & FlagC) ^ FlagC) | (((Q ^ F) | A) & FlagXY)); _t += 4; break; // CCF

            // LD r,r' (0x40-0x7F), HALT at 0x76
            case 0x76: Halted = true; _t += 4; Q = 0; break;
            case >= 0x40 and <= 0x7F:
            {
                int dst = (op >> 3) & 7, src = op & 7;
                if (src == 6) { ushort a = MemOperand(); SetRPlain(dst, Read(a)); _t += 7; }
                else if (dst == 6) { ushort a = MemOperand(); Write(a, GetRPlain(src)); _t += 7; }
                else { SetR(dst, GetR(src)); _t += 4; }
                Q = 0;
                break;
            }

            // ALU A,r (0x80-0xBF)
            case >= 0x80 and <= 0xBF:
            {
                int src = op & 7;
                byte v;
                if (src == 6) { v = Read(MemOperand()); _t += 7; }
                else { v = GetR(src); _t += 4; }
                Alu((op >> 3) & 7, v);
                break;
            }

            case 0xC0: RetCc((F & FlagZ) == 0); break;
            case 0xC1: BC = Pop(); _t += 10; Q = 0; break;
            case 0xC2: JpCc((F & FlagZ) == 0); break;
            case 0xC3: PC = WZ = Fetch16(); _t += 10; Q = 0; break;
            case 0xC4: CallCc((F & FlagZ) == 0); break;
            case 0xC5: Push(BC); _t += 11; Q = 0; break;
            case 0xC6: Add8(Fetch(), 0); _t += 7; break;
            case 0xC7: Rst(0x00); break;
            case 0xC8: RetCc((F & FlagZ) != 0); break;
            case 0xC9: PC = WZ = Pop(); _t += 10; Q = 0; break;
            case 0xCA: JpCc((F & FlagZ) != 0); break;
            // 0xCB: handled by the caller
            case 0xCC: CallCc((F & FlagZ) != 0); break;
            case 0xCD: { ushort a = Fetch16(); Push(PC); PC = WZ = a; _t += 17; Q = 0; break; }
            case 0xCE: Add8(Fetch(), F & FlagC); _t += 7; break;
            case 0xCF: Rst(0x08); break;

            case 0xD0: RetCc((F & FlagC) == 0); break;
            case 0xD1: DE = Pop(); _t += 10; Q = 0; break;
            case 0xD2: JpCc((F & FlagC) == 0); break;
            case 0xD3: { byte n = Fetch(); ushort port = (ushort)((A << 8) | n); Out(port, A); WZ = (ushort)((A << 8) | ((n + 1) & 0xFF)); _t += 11; Q = 0; break; } // OUT (n),A
            case 0xD4: CallCc((F & FlagC) == 0); break;
            case 0xD5: Push(DE); _t += 11; Q = 0; break;
            case 0xD6: Sub8(Fetch(), 0); _t += 7; break;
            case 0xD7: Rst(0x10); break;
            case 0xD8: RetCc((F & FlagC) != 0); break;
            case 0xD9: { ushort t = BC; BC = BC2; BC2 = t; t = DE; DE = DE2; DE2 = t; t = HL; HL = HL2; HL2 = t; _t += 4; Q = 0; break; } // EXX
            case 0xDA: JpCc((F & FlagC) != 0); break;
            case 0xDB: { byte n = Fetch(); ushort port = (ushort)((A << 8) | n); A = In(port); WZ = (ushort)(port + 1); _t += 11; Q = 0; break; } // IN A,(n)
            case 0xDC: CallCc((F & FlagC) != 0); break;
            // 0xDD: prefix, handled by the caller
            case 0xDE: Sub8(Fetch(), F & FlagC); _t += 7; break;
            case 0xDF: Rst(0x18); break;

            case 0xE0: RetCc((F & FlagP) == 0); break;
            case 0xE1: Hl = Pop(); _t += 10; Q = 0; break;
            case 0xE2: JpCc((F & FlagP) == 0); break;
            case 0xE3: { ushort t = Read16(SP); Write16(SP, Hl); Hl = WZ = t; _t += 19; Q = 0; break; } // EX (SP),HL
            case 0xE4: CallCc((F & FlagP) == 0); break;
            case 0xE5: Push(Hl); _t += 11; Q = 0; break;
            case 0xE6: And8(Fetch()); _t += 7; break;
            case 0xE7: Rst(0x20); break;
            case 0xE8: RetCc((F & FlagP) != 0); break;
            case 0xE9: PC = Hl; _t += 4; Q = 0; break;                                                 // JP (HL)
            case 0xEA: JpCc((F & FlagP) != 0); break;
            case 0xEB: { ushort t = DE; DE = HL; HL = t; _t += 4; Q = 0; break; }                      // EX DE,HL
            case 0xEC: CallCc((F & FlagP) != 0); break;
            // 0xED: prefix, handled by the caller
            case 0xEE: Xor8(Fetch()); _t += 7; break;
            case 0xEF: Rst(0x28); break;

            case 0xF0: RetCc((F & FlagS) == 0); break;
            case 0xF1: AF = Pop(); _t += 10; Q = 0; break;
            case 0xF2: JpCc((F & FlagS) == 0); break;
            case 0xF3: Iff1 = Iff2 = false; _t += 4; Q = 0; break;                                     // DI
            case 0xF4: CallCc((F & FlagS) == 0); break;
            case 0xF5: Push(AF); _t += 11; Q = 0; break;
            case 0xF6: Or8(Fetch()); _t += 7; break;
            case 0xF7: Rst(0x30); break;
            case 0xF8: RetCc((F & FlagS) != 0); break;
            case 0xF9: SP = Hl; _t += 6; Q = 0; break;                                                 // LD SP,HL
            case 0xFA: JpCc((F & FlagS) != 0); break;
            case 0xFB: Iff1 = Iff2 = true; _eiDelay = true; _t += 4; Q = 0; break;                     // EI
            case 0xFC: CallCc((F & FlagS) != 0); break;
            // 0xFD: prefix, handled by the caller
            case 0xFE: Cp8(Fetch()); _t += 7; break;
            case 0xFF: Rst(0x38); break;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void JrCc(bool taken)
    {
        sbyte d = (sbyte)Fetch();
        if (taken) { PC = (ushort)(PC + d); WZ = PC; _t += 12; }
        else _t += 7;
        Q = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void JpCc(bool taken)
    {
        WZ = Fetch16();
        if (taken) PC = WZ;
        _t += 10;
        Q = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void CallCc(bool taken)
    {
        WZ = Fetch16();
        if (taken) { Push(PC); PC = WZ; _t += 17; }
        else _t += 10;
        Q = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void RetCc(bool taken)
    {
        if (taken) { PC = WZ = Pop(); _t += 11; }
        else _t += 5;
        Q = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Rst(ushort address)
    {
        Push(PC);
        PC = WZ = address;
        _t += 11;
        Q = 0;
    }
}
