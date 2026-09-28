using NUnit.Framework;
using Tedd.Commodore.Emulator.Cpu;
using Tedd.Commodore.Tests.Support;

namespace Tedd.Commodore.Tests.Cpu;

/// <summary>
/// Flag and register results of the documented instructions. Sources: MOS MCS6500 Microcomputer Family
/// Programming Manual (chapter 2/3/4 for ADC/SBC/CMP/BIT and the flag descriptions), Bruce Clark's
/// "Decimal Mode" (6502.org) for the NMOS decimal-mode flag rules, 64doc.txt for the B-flag/bit-5 behaviour
/// of PHP/PLP/BRK/RTI.
/// </summary>
[TestFixture]
public class OpcodeFlagTests
{
    private const byte C = Cpu6502.FlagC;
    private const byte Z = Cpu6502.FlagZ;
    private const byte I = Cpu6502.FlagI;
    private const byte D = Cpu6502.FlagD;
    private const byte V = Cpu6502.FlagV;
    private const byte N = Cpu6502.FlagN;
    private const byte U = Cpu6502.FlagU;

    /// <summary>Boots a CPU with the code at $1000; P defaults to $20 (only bit 5 set).</summary>
    private static (Cpu6502 Cpu, RecordingBus Bus) Boot(params int[] code)
    {
        var bus = new RecordingBus();
        for (int i = 0; i < code.Length; i++)
            bus.Ram[0x1000 + i] = (byte)code[i];
        return (bus.CreateCpu(p: U), bus);
    }

    // -----------------------------------------------------------------------------------------------
    // ADC binary. The classic overflow table (MOS manual, "ADC" and the two's complement discussion):
    // V is set when both operands have the same sign and the result's sign differs.
    // -----------------------------------------------------------------------------------------------

    // A, operand, carry in, result, C, V, N, Z
    [TestCase(0x50, 0x10, false, 0x60, false, false, false, false)]
    [TestCase(0x50, 0x50, false, 0xA0, false, true, true, false)]
    [TestCase(0x50, 0x90, false, 0xE0, false, false, true, false)]
    [TestCase(0x50, 0xD0, false, 0x20, true, false, false, false)]
    [TestCase(0xD0, 0x10, false, 0xE0, false, false, true, false)]
    [TestCase(0xD0, 0x50, false, 0x20, true, false, false, false)]
    [TestCase(0xD0, 0x90, false, 0x60, true, true, false, false)]
    [TestCase(0xD0, 0xD0, false, 0xA0, true, false, true, false)]
    [TestCase(0x7F, 0x01, false, 0x80, false, true, true, false)]
    [TestCase(0xFF, 0x00, true, 0x00, true, false, false, true)]
    [TestCase(0x00, 0x00, false, 0x00, false, false, false, true)]
    [TestCase(0x3F, 0x40, true, 0x80, false, true, true, false)]
    public void Adc_Binary(int a, int operand, bool carryIn, int result, bool c, bool v, bool n, bool z)
    {
        var (cpu, _) = Boot(0x69, operand);
        cpu.A = (byte)a;
        cpu.FlagCarry = carryIn;
        cpu.RunInstruction();
        Assert.That(cpu.A, Is.EqualTo(result));
        Assert.That(cpu.FlagCarry, Is.EqualTo(c), "C");
        Assert.That(cpu.FlagOverflow, Is.EqualTo(v), "V");
        Assert.That(cpu.FlagNegative, Is.EqualTo(n), "N");
        Assert.That(cpu.FlagZero, Is.EqualTo(z), "Z");
    }

    // SBC binary: A - M - (1 - C). C is set when no borrow was needed.
    [TestCase(0x50, 0xF0, true, 0x60, false, false, false, false)]   // 80 - (-16) = 96: no overflow
    [TestCase(0x50, 0xB0, true, 0xA0, false, true, true, false)]     // 80 - (-80) = 160: overflow
    [TestCase(0x50, 0x70, true, 0xE0, false, false, true, false)]
    [TestCase(0x50, 0x30, true, 0x20, true, false, false, false)]
    [TestCase(0xD0, 0xF0, true, 0xE0, false, false, true, false)]
    [TestCase(0xD0, 0xB0, true, 0x20, true, false, false, false)]
    [TestCase(0xD0, 0x70, true, 0x60, true, true, false, false)]
    [TestCase(0xD0, 0x30, true, 0xA0, true, false, true, false)]
    [TestCase(0x00, 0x01, true, 0xFF, false, false, true, false)]
    [TestCase(0x50, 0x50, true, 0x00, true, false, false, true)]
    [TestCase(0x50, 0x50, false, 0xFF, false, false, true, false)]
    [TestCase(0x80, 0x01, true, 0x7F, true, true, false, false)]
    public void Sbc_Binary(int a, int operand, bool carryIn, int result, bool c, bool v, bool n, bool z)
    {
        var (cpu, _) = Boot(0xE9, operand);
        cpu.A = (byte)a;
        cpu.FlagCarry = carryIn;
        cpu.RunInstruction();
        Assert.That(cpu.A, Is.EqualTo(result));
        Assert.That(cpu.FlagCarry, Is.EqualTo(c), "C");
        Assert.That(cpu.FlagOverflow, Is.EqualTo(v), "V");
        Assert.That(cpu.FlagNegative, Is.EqualTo(n), "N");
        Assert.That(cpu.FlagZero, Is.EqualTo(z), "Z");
    }

    // -----------------------------------------------------------------------------------------------
    // ADC decimal (NMOS). Bruce Clark, "Decimal Mode", appendix A/B: on the NMOS 6502 the Z flag comes from
    // the *binary* sum, C from the decimal result, and N/V from the intermediate result after the low-nibble
    // correction but before the high-nibble correction ("+$60").
    // -----------------------------------------------------------------------------------------------

    // A, operand, carry in, result, C, Z, N, V
    [TestCase(0x09, 0x01, false, 0x10, false, false, false, false)]
    [TestCase(0x12, 0x34, false, 0x46, false, false, false, false)]
    [TestCase(0x00, 0x00, false, 0x00, false, true, false, false)]
    [TestCase(0x99, 0x01, false, 0x00, true, false, true, false)]   // Z=0: binary sum $9A; N=1: intermediate $A0
    [TestCase(0x99, 0x01, true, 0x01, true, false, true, false)]
    [TestCase(0x58, 0x46, false, 0x04, true, false, true, true)]    // intermediate $A4: N=1, V=1
    [TestCase(0x79, 0x00, true, 0x80, false, false, true, true)]    // Bruce Clark's example: N=1, V=1
    [TestCase(0x80, 0x80, false, 0x60, true, true, false, true)]    // Z=1: binary sum is $00
    [TestCase(0x15, 0x27, false, 0x42, false, false, false, false)]
    [TestCase(0x50, 0x50, false, 0x00, true, false, true, true)]    // binary $A0 -> Z=0; intermediate $A0 -> N=1, V=1
    public void Adc_Decimal(int a, int operand, bool carryIn, int result, bool c, bool z, bool n, bool v)
    {
        var (cpu, _) = Boot(0x69, operand);
        cpu.A = (byte)a;
        cpu.FlagDecimal = true;
        cpu.FlagCarry = carryIn;
        cpu.RunInstruction();
        Assert.That(cpu.A, Is.EqualTo(result));
        Assert.That(cpu.FlagCarry, Is.EqualTo(c), "C");
        Assert.That(cpu.FlagZero, Is.EqualTo(z), "Z");
        Assert.That(cpu.FlagNegative, Is.EqualTo(n), "N");
        Assert.That(cpu.FlagOverflow, Is.EqualTo(v), "V");
        Assert.That(cpu.FlagDecimal, Is.True, "D unchanged");
    }

    // SBC decimal (NMOS): N, V and Z are all taken from the binary result (Bruce Clark, appendix A),
    // C is the (inverted) borrow of the binary result.
    // A, operand, carry in, result, C, Z, N, V
    [TestCase(0x00, 0x01, true, 0x99, false, false, true, false)]
    [TestCase(0x46, 0x12, true, 0x34, true, false, false, false)]
    [TestCase(0x40, 0x13, true, 0x27, true, false, false, false)]
    [TestCase(0x00, 0x00, true, 0x00, true, true, false, false)]
    [TestCase(0x10, 0x20, true, 0x90, false, false, true, false)]  // binary $F0: N=1
    [TestCase(0x50, 0x25, false, 0x24, true, false, false, false)] // borrow in
    [TestCase(0x99, 0x99, true, 0x00, true, true, false, false)]
    [TestCase(0x80, 0x01, true, 0x79, true, false, false, true)]   // binary $7F: V=1
    public void Sbc_Decimal(int a, int operand, bool carryIn, int result, bool c, bool z, bool n, bool v)
    {
        var (cpu, _) = Boot(0xE9, operand);
        cpu.A = (byte)a;
        cpu.FlagDecimal = true;
        cpu.FlagCarry = carryIn;
        cpu.RunInstruction();
        Assert.That(cpu.A, Is.EqualTo(result));
        Assert.That(cpu.FlagCarry, Is.EqualTo(c), "C");
        Assert.That(cpu.FlagZero, Is.EqualTo(z), "Z");
        Assert.That(cpu.FlagNegative, Is.EqualTo(n), "N");
        Assert.That(cpu.FlagOverflow, Is.EqualTo(v), "V");
    }

    // -----------------------------------------------------------------------------------------------
    // CMP / CPX / CPY: Z = equal, C = register >= operand (unsigned), N = bit 7 of the difference.
    // -----------------------------------------------------------------------------------------------

    [TestCase(0xC9, 'A', 0x50, 0x50, true, true, false)]
    [TestCase(0xC9, 'A', 0x50, 0x30, true, false, false)]
    [TestCase(0xC9, 'A', 0x30, 0x50, false, false, true)]
    [TestCase(0xC9, 'A', 0x80, 0x01, true, false, false)]
    [TestCase(0xC9, 'A', 0x00, 0xFF, false, false, false)]
    [TestCase(0xE0, 'X', 0x50, 0x50, true, true, false)]
    [TestCase(0xE0, 'X', 0x50, 0x30, true, false, false)]
    [TestCase(0xE0, 'X', 0x30, 0x50, false, false, true)]
    [TestCase(0xC0, 'Y', 0x50, 0x50, true, true, false)]
    [TestCase(0xC0, 'Y', 0x50, 0x30, true, false, false)]
    [TestCase(0xC0, 'Y', 0x30, 0x50, false, false, true)]
    public void Compare_SetsCarryZeroNegative(int opcode, char reg, int regValue, int operand, bool c, bool z, bool n)
    {
        var (cpu, _) = Boot(opcode, operand);
        cpu.P = U | V | D;   // V and D must survive
        switch (reg)
        {
            case 'A': cpu.A = (byte)regValue; break;
            case 'X': cpu.X = (byte)regValue; break;
            default: cpu.Y = (byte)regValue; break;
        }
        cpu.RunInstruction();
        Assert.That(cpu.FlagCarry, Is.EqualTo(c), "C");
        Assert.That(cpu.FlagZero, Is.EqualTo(z), "Z");
        Assert.That(cpu.FlagNegative, Is.EqualTo(n), "N");
        Assert.That(cpu.FlagOverflow, Is.True, "V untouched");
        Assert.That(cpu.FlagDecimal, Is.True, "D untouched");
        byte after = reg switch { 'A' => cpu.A, 'X' => cpu.X, _ => cpu.Y };
        Assert.That(after, Is.EqualTo(regValue), "compared register is unchanged");
    }

    // -----------------------------------------------------------------------------------------------
    // BIT: N = bit 7 of operand, V = bit 6 of operand, Z = (A & operand) == 0; A unchanged.
    // -----------------------------------------------------------------------------------------------

    [TestCase(0x24, 0xC0, 0x00, true, true, true)]
    [TestCase(0x24, 0x40, 0x40, false, true, false)]
    [TestCase(0x24, 0x80, 0x7F, true, false, true)]
    [TestCase(0x24, 0x00, 0xFF, false, false, true)]
    [TestCase(0x24, 0x3F, 0x01, false, false, false)]
    [TestCase(0x2C, 0xC0, 0x00, true, true, true)]
    [TestCase(0x2C, 0x40, 0x40, false, true, false)]
    [TestCase(0x2C, 0x80, 0x7F, true, false, true)]
    public void Bit_TakesNVFromOperandAndZFromAnd(int opcode, int operand, int a, bool n, bool v, bool z)
    {
        var (cpu, bus) = Boot(opcode, 0x80, 0x00);   // zp $80 / abs $0080
        bus.Ram[0x80] = (byte)operand;
        cpu.A = (byte)a;
        cpu.FlagCarry = true;
        cpu.RunInstruction();
        Assert.That(cpu.FlagNegative, Is.EqualTo(n), "N");
        Assert.That(cpu.FlagOverflow, Is.EqualTo(v), "V");
        Assert.That(cpu.FlagZero, Is.EqualTo(z), "Z");
        Assert.That(cpu.FlagCarry, Is.True, "C untouched");
        Assert.That(cpu.A, Is.EqualTo(a), "A unchanged");
    }

    // -----------------------------------------------------------------------------------------------
    // Shifts and rotates, accumulator and memory.
    // -----------------------------------------------------------------------------------------------

    // opcode (accumulator form), value, carry in, result, carry out, N, Z
    [TestCase(0x0A, 0x80, false, 0x00, true, false, true)]   // ASL
    [TestCase(0x0A, 0x40, false, 0x80, false, true, false)]
    [TestCase(0x0A, 0x81, true, 0x02, true, false, false)]   // carry in is ignored by ASL
    [TestCase(0x4A, 0x01, false, 0x00, true, false, true)]   // LSR
    [TestCase(0x4A, 0x81, false, 0x40, true, false, false)]
    [TestCase(0x4A, 0x02, true, 0x01, false, false, false)]  // carry in is ignored by LSR
    [TestCase(0x2A, 0x80, true, 0x01, true, false, false)]   // ROL: carry in -> bit 0, bit 7 -> carry
    [TestCase(0x2A, 0x80, false, 0x00, true, false, true)]
    [TestCase(0x2A, 0x40, false, 0x80, false, true, false)]
    [TestCase(0x6A, 0x01, true, 0x80, true, true, false)]    // ROR: carry in -> bit 7, bit 0 -> carry
    [TestCase(0x6A, 0x01, false, 0x00, true, false, true)]
    [TestCase(0x6A, 0x02, false, 0x01, false, false, false)]
    public void ShiftAccumulator(int opcode, int value, bool carryIn, int result, bool carryOut, bool n, bool z)
    {
        var (cpu, _) = Boot(opcode, 0xEA);
        cpu.A = (byte)value;
        cpu.FlagCarry = carryIn;
        cpu.RunInstruction();
        Assert.That(cpu.A, Is.EqualTo(result));
        Assert.That(cpu.FlagCarry, Is.EqualTo(carryOut), "C");
        Assert.That(cpu.FlagNegative, Is.EqualTo(n), "N");
        Assert.That(cpu.FlagZero, Is.EqualTo(z), "Z");
    }

    // opcode (zero page form), value, carry in, result, carry out, N, Z
    [TestCase(0x06, 0x80, false, 0x00, true, false, true)]   // ASL zp
    [TestCase(0x06, 0x40, false, 0x80, false, true, false)]
    [TestCase(0x46, 0x01, false, 0x00, true, false, true)]   // LSR zp
    [TestCase(0x46, 0x81, true, 0x40, true, false, false)]
    [TestCase(0x26, 0x80, true, 0x01, true, false, false)]   // ROL zp
    [TestCase(0x26, 0x00, false, 0x00, false, false, true)]
    [TestCase(0x66, 0x01, true, 0x80, true, true, false)]    // ROR zp
    [TestCase(0x66, 0x00, false, 0x00, false, false, true)]
    public void ShiftMemory(int opcode, int value, bool carryIn, int result, bool carryOut, bool n, bool z)
    {
        var (cpu, bus) = Boot(opcode, 0x80);
        bus.Ram[0x80] = (byte)value;
        cpu.A = 0x55;
        cpu.FlagCarry = carryIn;
        cpu.RunInstruction();
        Assert.That(bus.Ram[0x80], Is.EqualTo(result));
        Assert.That(cpu.A, Is.EqualTo(0x55), "A unchanged");
        Assert.That(cpu.FlagCarry, Is.EqualTo(carryOut), "C");
        Assert.That(cpu.FlagNegative, Is.EqualTo(n), "N");
        Assert.That(cpu.FlagZero, Is.EqualTo(z), "Z");
    }

    // -----------------------------------------------------------------------------------------------
    // INC / DEC / INX / DEX / INY / DEY wrap around and set N/Z only (C untouched).
    // -----------------------------------------------------------------------------------------------

    [TestCase(0xE6, 0xFF, 0x00, false, true)]
    [TestCase(0xE6, 0x7F, 0x80, true, false)]
    [TestCase(0xE6, 0x00, 0x01, false, false)]
    [TestCase(0xC6, 0x00, 0xFF, true, false)]
    [TestCase(0xC6, 0x01, 0x00, false, true)]
    [TestCase(0xC6, 0x80, 0x7F, false, false)]
    public void IncDecMemory_WrapsAndSetsNZ(int opcode, int value, int result, bool n, bool z)
    {
        var (cpu, bus) = Boot(opcode, 0x80);
        bus.Ram[0x80] = (byte)value;
        cpu.FlagCarry = true;
        cpu.RunInstruction();
        Assert.That(bus.Ram[0x80], Is.EqualTo(result));
        Assert.That(cpu.FlagNegative, Is.EqualTo(n), "N");
        Assert.That(cpu.FlagZero, Is.EqualTo(z), "Z");
        Assert.That(cpu.FlagCarry, Is.True, "C untouched");
    }

    [TestCase(0xE8, 'X', 0xFF, 0x00, false, true)]
    [TestCase(0xE8, 'X', 0x7F, 0x80, true, false)]
    [TestCase(0xCA, 'X', 0x00, 0xFF, true, false)]
    [TestCase(0xCA, 'X', 0x01, 0x00, false, true)]
    [TestCase(0xC8, 'Y', 0xFF, 0x00, false, true)]
    [TestCase(0xC8, 'Y', 0x7F, 0x80, true, false)]
    [TestCase(0x88, 'Y', 0x00, 0xFF, true, false)]
    [TestCase(0x88, 'Y', 0x01, 0x00, false, true)]
    public void IncDecIndex_WrapsAndSetsNZ(int opcode, char reg, int value, int result, bool n, bool z)
    {
        var (cpu, _) = Boot(opcode, 0xEA);
        if (reg == 'X') cpu.X = (byte)value; else cpu.Y = (byte)value;
        cpu.FlagCarry = true;
        cpu.RunInstruction();
        Assert.That(reg == 'X' ? cpu.X : cpu.Y, Is.EqualTo(result));
        Assert.That(cpu.FlagNegative, Is.EqualTo(n), "N");
        Assert.That(cpu.FlagZero, Is.EqualTo(z), "Z");
        Assert.That(cpu.FlagCarry, Is.True, "C untouched");
    }

    // -----------------------------------------------------------------------------------------------
    // Transfers set N/Z from the transferred value; TXS does not affect any flag.
    // -----------------------------------------------------------------------------------------------

    [TestCase(0xAA, "TAX", 0x80, true, false)]
    [TestCase(0xAA, "TAX", 0x00, false, true)]
    [TestCase(0xAA, "TAX", 0x01, false, false)]
    [TestCase(0xA8, "TAY", 0x80, true, false)]
    [TestCase(0xA8, "TAY", 0x00, false, true)]
    [TestCase(0x8A, "TXA", 0x80, true, false)]
    [TestCase(0x8A, "TXA", 0x00, false, true)]
    [TestCase(0x98, "TYA", 0x80, true, false)]
    [TestCase(0x98, "TYA", 0x00, false, true)]
    [TestCase(0xBA, "TSX", 0x80, true, false)]
    [TestCase(0xBA, "TSX", 0x00, false, true)]
    public void Transfers_SetNZ(int opcode, string mnemonic, int value, bool n, bool z)
    {
        var (cpu, _) = Boot(opcode, 0xEA);
        cpu.A = cpu.X = cpu.Y = cpu.S = (byte)value;
        cpu.P = U | C | V;
        cpu.RunInstruction();
        Assert.That(cpu.FlagNegative, Is.EqualTo(n), mnemonic + " N");
        Assert.That(cpu.FlagZero, Is.EqualTo(z), mnemonic + " Z");
        Assert.That(cpu.FlagCarry, Is.True, "C untouched");
        Assert.That(cpu.FlagOverflow, Is.True, "V untouched");
        switch (mnemonic)
        {
            case "TAX": case "TSX": Assert.That(cpu.X, Is.EqualTo(value)); break;
            case "TAY": Assert.That(cpu.Y, Is.EqualTo(value)); break;
            default: Assert.That(cpu.A, Is.EqualTo(value)); break;
        }
    }

    [TestCase(0x00)]
    [TestCase(0x80)]
    [TestCase(0x01)]
    public void Txs_DoesNotChangeFlags(int value)
    {
        var (cpu, _) = Boot(0x9A, 0xEA);
        cpu.X = (byte)value;
        cpu.P = U | C | V;   // neither N nor Z set beforehand; TXS must leave that alone
        cpu.RunInstruction();
        Assert.That(cpu.S, Is.EqualTo(value));
        Assert.That(cpu.P, Is.EqualTo(U | C | V));
    }

    // -----------------------------------------------------------------------------------------------
    // Loads set N/Z.
    // -----------------------------------------------------------------------------------------------

    [TestCase(0xA9, 'A', 0x00, false, true)]
    [TestCase(0xA9, 'A', 0x80, true, false)]
    [TestCase(0xA9, 'A', 0x7F, false, false)]
    [TestCase(0xA2, 'X', 0x00, false, true)]
    [TestCase(0xA2, 'X', 0x80, true, false)]
    [TestCase(0xA2, 'X', 0x01, false, false)]
    [TestCase(0xA0, 'Y', 0x00, false, true)]
    [TestCase(0xA0, 'Y', 0xFF, true, false)]
    [TestCase(0xA0, 'Y', 0x01, false, false)]
    public void Loads_SetNZ(int opcode, char reg, int value, bool n, bool z)
    {
        var (cpu, _) = Boot(opcode, value);
        cpu.P = U | C | V | D | I;
        cpu.RunInstruction();
        byte got = reg switch { 'A' => cpu.A, 'X' => cpu.X, _ => cpu.Y };
        Assert.That(got, Is.EqualTo(value));
        Assert.That(cpu.FlagNegative, Is.EqualTo(n), "N");
        Assert.That(cpu.FlagZero, Is.EqualTo(z), "Z");
        Assert.That(cpu.P & (C | V | D | I), Is.EqualTo(C | V | D | I), "other flags untouched");
    }

    [TestCase(0x09, 0xF0, 0x0F, 0xFF, true, false)]   // ORA
    [TestCase(0x09, 0x00, 0x00, 0x00, false, true)]
    [TestCase(0x29, 0xF0, 0x0F, 0x00, false, true)]   // AND
    [TestCase(0x29, 0xF0, 0x80, 0x80, true, false)]
    [TestCase(0x49, 0xFF, 0x0F, 0xF0, true, false)]   // EOR
    [TestCase(0x49, 0xAA, 0xAA, 0x00, false, true)]
    public void Logic_SetNZ(int opcode, int a, int operand, int result, bool n, bool z)
    {
        var (cpu, _) = Boot(opcode, operand);
        cpu.A = (byte)a;
        cpu.FlagCarry = true;
        cpu.RunInstruction();
        Assert.That(cpu.A, Is.EqualTo(result));
        Assert.That(cpu.FlagNegative, Is.EqualTo(n), "N");
        Assert.That(cpu.FlagZero, Is.EqualTo(z), "Z");
        Assert.That(cpu.FlagCarry, Is.True, "C untouched");
    }

    // -----------------------------------------------------------------------------------------------
    // PHP pushes P with bits 4 (B) and 5 set; PLP and RTI ignore bit 4 and force bit 5 (64doc, "The B flag").
    // -----------------------------------------------------------------------------------------------

    [TestCase(0x00, 0x30)]
    [TestCase(0x20, 0x30)]
    [TestCase(0xC3, 0xF3)]
    [TestCase(0xCF, 0xFF)]
    public void Php_PushesBAndBit5Set(int p, int pushed)
    {
        var (cpu, bus) = Boot(0x08, 0xEA);
        cpu.P = (byte)p;
        cpu.RunInstruction();
        Assert.That(bus.Ram[0x01FD], Is.EqualTo(pushed));
        Assert.That(cpu.P, Is.EqualTo(p), "P itself is not modified");
    }

    [TestCase(0x00, 0x20)]
    [TestCase(0x10, 0x20)]
    [TestCase(0xFF, 0xEF)]
    [TestCase(0xCF, 0xEF)]
    [TestCase(0xC3, 0xE3)]
    public void Plp_IgnoresBit4AndForcesBit5(int stackValue, int expectedP)
    {
        var (cpu, bus) = Boot(0x28, 0xEA);
        cpu.S = 0xFC;
        bus.Ram[0x01FD] = (byte)stackValue;
        cpu.RunInstruction();
        Assert.That(cpu.P, Is.EqualTo(expectedP));
    }

    [TestCase(0x00, 0x20)]
    [TestCase(0x10, 0x20)]
    [TestCase(0xFF, 0xEF)]
    [TestCase(0xC3, 0xE3)]
    public void Rti_IgnoresBit4AndForcesBit5(int stackValue, int expectedP)
    {
        var (cpu, bus) = Boot(0x40, 0xEA);
        cpu.S = 0xFA;
        bus.Ram[0x01FB] = (byte)stackValue;
        bus.Ram[0x01FC] = 0x00;
        bus.Ram[0x01FD] = 0x20;
        cpu.RunInstruction();
        Assert.That(cpu.P, Is.EqualTo(expectedP));
        Assert.That(cpu.PC, Is.EqualTo(0x2000));
    }

    [Test]
    public void Pla_SetsNZ()
    {
        var (cpu, bus) = Boot(0x68, 0x68, 0x68);
        cpu.S = 0xFA;
        bus.Ram[0x01FB] = 0x80;
        bus.Ram[0x01FC] = 0x00;
        bus.Ram[0x01FD] = 0x01;
        cpu.RunInstruction();
        Assert.That((cpu.A, cpu.FlagNegative, cpu.FlagZero), Is.EqualTo(((byte)0x80, true, false)));
        cpu.RunInstruction();
        Assert.That((cpu.A, cpu.FlagNegative, cpu.FlagZero), Is.EqualTo(((byte)0x00, false, true)));
        cpu.RunInstruction();
        Assert.That((cpu.A, cpu.FlagNegative, cpu.FlagZero), Is.EqualTo(((byte)0x01, false, false)));
    }

    // -----------------------------------------------------------------------------------------------
    // Flag set/clear instructions touch exactly one bit.
    // -----------------------------------------------------------------------------------------------

    [TestCase(0x18, "CLC", 0xFF, 0xFE)]
    [TestCase(0x38, "SEC", 0x20, 0x21)]
    [TestCase(0x58, "CLI", 0xFF, 0xFB)]
    [TestCase(0x78, "SEI", 0x20, 0x24)]
    [TestCase(0xB8, "CLV", 0xFF, 0xBF)]
    [TestCase(0xD8, "CLD", 0xFF, 0xF7)]
    [TestCase(0xF8, "SED", 0x20, 0x28)]
    [TestCase(0x18, "CLC", 0x20, 0x20)]
    [TestCase(0x38, "SEC", 0xFF, 0xFF)]
    public void FlagInstructions(int opcode, string mnemonic, int before, int after)
    {
        var (cpu, _) = Boot(opcode, 0xEA);
        cpu.P = (byte)before;
        cpu.RunInstruction();
        Assert.That(cpu.P, Is.EqualTo(after), mnemonic);
    }

    [Test]
    public void SoPin_SetsOverflowImmediately()
    {
        var (cpu, _) = Boot(0xEA);
        cpu.P = U;
        cpu.SetOverflow();
        Assert.That(cpu.FlagOverflow, Is.True);
        Assert.That(cpu.P, Is.EqualTo(U | V));
    }

    // -----------------------------------------------------------------------------------------------
    // Reset: PC from $FFFC/$FFFD, S = $FD, I set, bit 5 set; other flags are not touched by reset on the
    // NMOS 6502 (D in particular is undefined after power-up and left alone by reset; MOS hardware manual).
    // -----------------------------------------------------------------------------------------------

    [Test]
    public void Reset_LoadsVectorSetsInterruptAndStackPointer()
    {
        var bus = new RecordingBus();
        bus.Ram[0xFFFC] = 0x00;
        bus.Ram[0xFFFD] = 0xC0;
        var cpu = new Cpu6502(bus);
        Assert.That(cpu.P & (U | I), Is.EqualTo(U | I), "after construction");
        cpu.S = 0x00;
        cpu.P = U;
        cpu.Reset();
        Assert.That(cpu.PC, Is.EqualTo(0xC000));
        Assert.That(cpu.S, Is.EqualTo(0xFD));
        Assert.That(cpu.FlagInterrupt, Is.True, "I set by reset");
        Assert.That(cpu.P & U, Is.EqualTo(U), "bit 5 reads as 1");
        Assert.That(cpu.Cycles, Is.EqualTo(7), "the reset sequence takes 7 cycles");
        Assert.That(cpu.AtInstructionBoundary, Is.True);
        Assert.That(cpu.Jammed, Is.False);
    }

    [Test]
    public void Reset_DoesNotClearDecimalOrOtherFlags()
    {
        var bus = new RecordingBus();
        var cpu = new Cpu6502(bus) { P = 0xFF };
        cpu.Reset();
        Assert.That(cpu.P, Is.EqualTo(0xFF));
    }
}
