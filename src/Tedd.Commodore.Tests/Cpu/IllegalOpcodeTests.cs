using NUnit.Framework;
using Tedd.Commodore.Emulator.Cpu;
using Tedd.Commodore.Tests.Support;
using static Tedd.Commodore.Tests.Support.CpuTestExtensions;

namespace Tedd.Commodore.Tests.Cpu;

/// <summary>
/// Undocumented NMOS opcodes. Source: "NMOS 6510 Unintended Opcodes - No More Secrets" (Groepaz et al.),
/// with the cycle sequences from 64doc.txt (the illegal RMW opcodes use the same cycle tables as the
/// documented RMW instructions, including the double write and the always-present dummy read in the
/// indexed modes).
/// </summary>
[TestFixture]
public class IllegalOpcodeTests
{
    private const byte U = Cpu6502.FlagU;

    private static (Cpu6502 Cpu, RecordingBus Bus) Boot(params int[] code)
    {
        var bus = new RecordingBus();
        for (int i = 0; i < code.Length; i++)
            bus.Ram[0x1000 + i] = (byte)code[i];
        return (bus.CreateCpu(p: U), bus);
    }

    // -----------------------------------------------------------------------------------------------
    // RMW + accumulator combos: SLO (ASL + ORA), RLA (ROL + AND), SRE (LSR + EOR), RRA (ROR + ADC),
    // DCP (DEC + CMP), ISC (INC + SBC).
    // -----------------------------------------------------------------------------------------------

    // opcode (zp form), mnemonic, A in, memory in, carry in, memory out, A out, C, N, Z
    [TestCase(0x07, "SLO", 0x01, 0x41, false, 0x82, 0x83, false, true, false)]
    [TestCase(0x07, "SLO", 0x00, 0x80, false, 0x00, 0x00, true, false, true)]
    [TestCase(0x27, "RLA", 0xFF, 0x41, true, 0x83, 0x83, false, true, false)]
    [TestCase(0x27, "RLA", 0x0F, 0x80, false, 0x00, 0x00, true, false, true)]
    [TestCase(0x47, "SRE", 0xFF, 0x41, false, 0x20, 0xDF, true, true, false)]
    [TestCase(0x47, "SRE", 0x01, 0x02, true, 0x01, 0x00, false, false, true)]
    [TestCase(0x67, "RRA", 0x10, 0x41, false, 0x20, 0x31, false, false, false)]   // ROR -> $20, C=1; ADC $20 + C
    [TestCase(0x67, "RRA", 0x00, 0x00, true, 0x80, 0x80, false, true, false)]     // ROR with C in -> $80, C=0; ADC
    [TestCase(0xC7, "DCP", 0x41, 0x42, false, 0x41, 0x41, true, false, true)]     // DEC -> $41; CMP equal
    [TestCase(0xC7, "DCP", 0x00, 0x00, false, 0xFF, 0x00, false, false, false)]   // DEC wraps; $00 < $FF
    [TestCase(0xE7, "ISC", 0x50, 0x0F, true, 0x10, 0x40, true, false, false)]     // INC -> $10; SBC
    [TestCase(0xE7, "ISC", 0x00, 0xFF, true, 0x00, 0x00, true, false, true)]      // INC wraps to 0; 0 - 0
    public void RmwCombos_ZeroPage(int opcode, string mnemonic, int a, int memIn, bool carryIn, int memOut, int aOut, bool c, bool n, bool z)
    {
        var (cpu, bus) = Boot(opcode, 0x80, 0x77);
        cpu.A = (byte)a;
        cpu.FlagCarry = carryIn;
        bus.Ram[0x80] = (byte)memIn;
        int cycles = cpu.RunInstruction();
        Assert.That(cycles, Is.EqualTo(5), mnemonic + " zp is 5 cycles");
        bus.AssertTrace(
            $"R 1000 {Hex2(opcode)}",
            "R 1001 80",
            $"R 0080 {Hex2(memIn)}",
            $"W 0080 {Hex2(memIn)}",
            $"W 0080 {Hex2(memOut)}");
        Assert.That(cpu.A, Is.EqualTo(aOut), "A");
        Assert.That(cpu.FlagCarry, Is.EqualTo(c), "C");
        Assert.That(cpu.FlagNegative, Is.EqualTo(n), "N");
        Assert.That(cpu.FlagZero, Is.EqualTo(z), "Z");
    }

    // abs,X (7 cycles, dummy read always): 64doc "Absolute indexed addressing, Read-Modify-Write instructions".
    [TestCase(0x1F, "SLO", 0x82)] [TestCase(0x3F, "RLA", 0x82)] [TestCase(0x5F, "SRE", 0x20)]
    [TestCase(0x7F, "RRA", 0x20)] [TestCase(0xDF, "DCP", 0x40)] [TestCase(0xFF, "ISC", 0x42)]
    public void RmwCombos_AbsoluteX_SevenCycles_DoubleWrite(int opcode, string mnemonic, int newValue)
    {
        var (cpu, bus) = Boot(opcode, 0x34, 0x12, 0x77);
        cpu.X = 0x05;
        bus.Ram[0x1239] = 0x41;
        int cycles = cpu.RunInstruction();
        Assert.That(cycles, Is.EqualTo(7), mnemonic);
        bus.AssertTrace(
            $"R 1000 {Hex2(opcode)}",
            "R 1001 34",
            "R 1002 12",
            "R 1239 41",
            "R 1239 41",
            "W 1239 41",
            $"W 1239 {Hex2(newValue)}");
    }

    [TestCase(0x1F, "SLO", 0x82)] [TestCase(0x3F, "RLA", 0x82)] [TestCase(0x5F, "SRE", 0x20)]
    [TestCase(0x7F, "RRA", 0x20)] [TestCase(0xDF, "DCP", 0x40)] [TestCase(0xFF, "ISC", 0x42)]
    public void RmwCombos_AbsoluteX_PageCross_DummyReadAtUnfixedAddress(int opcode, string mnemonic, int newValue)
    {
        var (cpu, bus) = Boot(opcode, 0x34, 0x12, 0x77);
        cpu.X = 0xF0;
        bus.Ram[0x1224] = 0x22;
        bus.Ram[0x1324] = 0x41;
        int cycles = cpu.RunInstruction();
        Assert.That(cycles, Is.EqualTo(7), mnemonic);
        bus.AssertTrace(
            $"R 1000 {Hex2(opcode)}",
            "R 1001 34",
            "R 1002 12",
            "R 1224 22",
            "R 1324 41",
            "W 1324 41",
            $"W 1324 {Hex2(newValue)}");
    }

    // abs,Y (7 cycles): the documented set has no RMW abs,Y, these are the only users of that cycle table.
    [TestCase(0x1B, "SLO", 0x82)] [TestCase(0x3B, "RLA", 0x82)] [TestCase(0x5B, "SRE", 0x20)]
    [TestCase(0x7B, "RRA", 0x20)] [TestCase(0xDB, "DCP", 0x40)] [TestCase(0xFB, "ISC", 0x42)]
    public void RmwCombos_AbsoluteY_SevenCycles(int opcode, string mnemonic, int newValue)
    {
        var (cpu, bus) = Boot(opcode, 0x34, 0x12, 0x77);
        cpu.Y = 0x05;
        bus.Ram[0x1239] = 0x41;
        int cycles = cpu.RunInstruction();
        Assert.That(cycles, Is.EqualTo(7), mnemonic);
        bus.AssertTrace(
            $"R 1000 {Hex2(opcode)}",
            "R 1001 34",
            "R 1002 12",
            "R 1239 41",
            "R 1239 41",
            "W 1239 41",
            $"W 1239 {Hex2(newValue)}");
    }

    // (zp,X) (8 cycles)
    [TestCase(0x03, "SLO", 0x82)] [TestCase(0x23, "RLA", 0x82)] [TestCase(0x43, "SRE", 0x20)]
    [TestCase(0x63, "RRA", 0x20)] [TestCase(0xC3, "DCP", 0x40)] [TestCase(0xE3, "ISC", 0x42)]
    public void RmwCombos_IndirectX_EightCycles(int opcode, string mnemonic, int newValue)
    {
        var (cpu, bus) = Boot(opcode, 0x20, 0x77);
        cpu.X = 0x04;
        bus.Ram[0x20] = 0x11;
        bus.Ram[0x24] = 0x34;
        bus.Ram[0x25] = 0x12;
        bus.Ram[0x1234] = 0x41;
        int cycles = cpu.RunInstruction();
        Assert.That(cycles, Is.EqualTo(8), mnemonic);
        bus.AssertTrace(
            $"R 1000 {Hex2(opcode)}",
            "R 1001 20",
            "R 0020 11",
            "R 0024 34",
            "R 0025 12",
            "R 1234 41",
            "W 1234 41",
            $"W 1234 {Hex2(newValue)}");
    }

    // (zp),Y (8 cycles, dummy read always, even without page crossing)
    [TestCase(0x13, "SLO", 0x82)] [TestCase(0x33, "RLA", 0x82)] [TestCase(0x53, "SRE", 0x20)]
    [TestCase(0x73, "RRA", 0x20)] [TestCase(0xD3, "DCP", 0x40)] [TestCase(0xF3, "ISC", 0x42)]
    public void RmwCombos_IndirectY_EightCycles_NoPageCross(int opcode, string mnemonic, int newValue)
    {
        var (cpu, bus) = Boot(opcode, 0x20, 0x77);
        cpu.Y = 0x05;
        bus.Ram[0x20] = 0x34;
        bus.Ram[0x21] = 0x12;
        bus.Ram[0x1239] = 0x41;
        int cycles = cpu.RunInstruction();
        Assert.That(cycles, Is.EqualTo(8), mnemonic);
        bus.AssertTrace(
            $"R 1000 {Hex2(opcode)}",
            "R 1001 20",
            "R 0020 34",
            "R 0021 12",
            "R 1239 41",
            "R 1239 41",
            "W 1239 41",
            $"W 1239 {Hex2(newValue)}");
    }

    [TestCase(0x13, "SLO", 0x82)] [TestCase(0x33, "RLA", 0x82)] [TestCase(0x53, "SRE", 0x20)]
    [TestCase(0x73, "RRA", 0x20)] [TestCase(0xD3, "DCP", 0x40)] [TestCase(0xF3, "ISC", 0x42)]
    public void RmwCombos_IndirectY_EightCycles_PageCross(int opcode, string mnemonic, int newValue)
    {
        var (cpu, bus) = Boot(opcode, 0x20, 0x77);
        cpu.Y = 0xF0;
        bus.Ram[0x20] = 0x34;
        bus.Ram[0x21] = 0x12;
        bus.Ram[0x1224] = 0x22;
        bus.Ram[0x1324] = 0x41;
        int cycles = cpu.RunInstruction();
        Assert.That(cycles, Is.EqualTo(8), mnemonic);
        bus.AssertTrace(
            $"R 1000 {Hex2(opcode)}",
            "R 1001 20",
            "R 0020 34",
            "R 0021 12",
            "R 1224 22",
            "R 1324 41",
            "W 1324 41",
            $"W 1324 {Hex2(newValue)}");
    }

    // zp,X (6) and abs (6)
    [TestCase(0x17, "SLO", 0x82)] [TestCase(0x37, "RLA", 0x82)] [TestCase(0x57, "SRE", 0x20)]
    [TestCase(0x77, "RRA", 0x20)] [TestCase(0xD7, "DCP", 0x40)] [TestCase(0xF7, "ISC", 0x42)]
    public void RmwCombos_ZeroPageX_SixCycles(int opcode, string mnemonic, int newValue)
    {
        var (cpu, bus) = Boot(opcode, 0x80, 0x77);
        cpu.X = 0x05;
        bus.Ram[0x80] = 0x11;
        bus.Ram[0x85] = 0x41;
        int cycles = cpu.RunInstruction();
        Assert.That(cycles, Is.EqualTo(6), mnemonic);
        bus.AssertTrace(
            $"R 1000 {Hex2(opcode)}",
            "R 1001 80",
            "R 0080 11",
            "R 0085 41",
            "W 0085 41",
            $"W 0085 {Hex2(newValue)}");
    }

    [TestCase(0x0F, "SLO", 0x82)] [TestCase(0x2F, "RLA", 0x82)] [TestCase(0x4F, "SRE", 0x20)]
    [TestCase(0x6F, "RRA", 0x20)] [TestCase(0xCF, "DCP", 0x40)] [TestCase(0xEF, "ISC", 0x42)]
    public void RmwCombos_Absolute_SixCycles(int opcode, string mnemonic, int newValue)
    {
        var (cpu, bus) = Boot(opcode, 0x34, 0x12, 0x77);
        bus.Ram[0x1234] = 0x41;
        int cycles = cpu.RunInstruction();
        Assert.That(cycles, Is.EqualTo(6), mnemonic);
        bus.AssertTrace(
            $"R 1000 {Hex2(opcode)}",
            "R 1001 34",
            "R 1002 12",
            "R 1234 41",
            "W 1234 41",
            $"W 1234 {Hex2(newValue)}");
    }

    // -----------------------------------------------------------------------------------------------
    // SAX: stores A & X, no flags. LAX: loads A and X, N/Z.
    // -----------------------------------------------------------------------------------------------

    [TestCase(0x87, "SAX zp")]
    public void Sax_ZeroPage_StoresAAndXWithoutTouchingFlags(int opcode, string mnemonic)
    {
        var (cpu, bus) = Boot(opcode, 0x80, 0x77);
        cpu.A = 0xF3;
        cpu.X = 0x3C;
        cpu.P = 0xFF;
        cpu.RunInstruction();
        bus.AssertTrace($"R 1000 {Hex2(opcode)}", "R 1001 80", "W 0080 30");
        Assert.That(cpu.P, Is.EqualTo(0xFF), mnemonic + " leaves P alone");
        Assert.That((cpu.A, cpu.X), Is.EqualTo(((byte)0xF3, (byte)0x3C)), "A and X unchanged");
    }

    [Test]
    public void Sax_ZeroPageY_FourCycles()
    {
        var (cpu, bus) = Boot(0x97, 0x80, 0x77);
        cpu.A = 0xF3;
        cpu.X = 0x3C;
        cpu.Y = 0x05;
        bus.Ram[0x80] = 0x11;
        Assert.That(cpu.RunInstruction(), Is.EqualTo(4));
        bus.AssertTrace("R 1000 97", "R 1001 80", "R 0080 11", "W 0085 30");
    }

    [Test]
    public void Sax_Absolute_FourCycles()
    {
        var (cpu, bus) = Boot(0x8F, 0x34, 0x12, 0x77);
        cpu.A = 0xF3;
        cpu.X = 0x3C;
        Assert.That(cpu.RunInstruction(), Is.EqualTo(4));
        bus.AssertTrace("R 1000 8F", "R 1001 34", "R 1002 12", "W 1234 30");
    }

    [Test]
    public void Sax_IndirectX_SixCycles()
    {
        var (cpu, bus) = Boot(0x83, 0x20, 0x77);
        cpu.A = 0xF3;
        cpu.X = 0x04;
        bus.Ram[0x20] = 0x11;
        bus.Ram[0x24] = 0x34;
        bus.Ram[0x25] = 0x12;
        Assert.That(cpu.RunInstruction(), Is.EqualTo(6));
        bus.AssertTrace("R 1000 83", "R 1001 20", "R 0020 11", "R 0024 34", "R 0025 12", "W 1234 00");  // $F3 & $04 = 0
    }

    [TestCase(0xA7, 0x80, true, false)]
    [TestCase(0xA7, 0x00, false, true)]
    [TestCase(0xA7, 0x37, false, false)]
    public void Lax_ZeroPage_LoadsAAndX(int opcode, int value, bool n, bool z)
    {
        var (cpu, bus) = Boot(opcode, 0x80, 0x77);
        bus.Ram[0x80] = (byte)value;
        Assert.That(cpu.RunInstruction(), Is.EqualTo(3));
        bus.AssertTrace("R 1000 A7", "R 1001 80", $"R 0080 {Hex2(value)}");
        Assert.That((cpu.A, cpu.X), Is.EqualTo(((byte)value, (byte)value)));
        Assert.That(cpu.FlagNegative, Is.EqualTo(n), "N");
        Assert.That(cpu.FlagZero, Is.EqualTo(z), "Z");
    }

    [Test]
    public void Lax_AbsoluteY_PageCross_FiveCycles()
    {
        var (cpu, bus) = Boot(0xBF, 0x34, 0x12, 0x77);
        cpu.Y = 0xF0;
        bus.Ram[0x1224] = 0x22;
        bus.Ram[0x1324] = 0x37;
        Assert.That(cpu.RunInstruction(), Is.EqualTo(5));
        bus.AssertTrace("R 1000 BF", "R 1001 34", "R 1002 12", "R 1224 22", "R 1324 37");
        Assert.That((cpu.A, cpu.X), Is.EqualTo(((byte)0x37, (byte)0x37)));
    }

    [Test]
    public void Lax_IndirectY_NoPageCross_FiveCycles()
    {
        var (cpu, bus) = Boot(0xB3, 0x20, 0x77);
        cpu.Y = 0x05;
        bus.Ram[0x20] = 0x34;
        bus.Ram[0x21] = 0x12;
        bus.Ram[0x1239] = 0x37;
        Assert.That(cpu.RunInstruction(), Is.EqualTo(5));
        bus.AssertTrace("R 1000 B3", "R 1001 20", "R 0020 34", "R 0021 12", "R 1239 37");
        Assert.That((cpu.A, cpu.X), Is.EqualTo(((byte)0x37, (byte)0x37)));
    }

    // -----------------------------------------------------------------------------------------------
    // ANC: A &= imm, C = N.  ALR: A = (A & imm) >> 1, C = bit 0 shifted out.
    // -----------------------------------------------------------------------------------------------

    [TestCase(0x0B, 0xFF, 0x80, 0x80, true, true, false)]
    [TestCase(0x0B, 0xFF, 0x7F, 0x7F, false, false, false)]
    [TestCase(0x0B, 0x0F, 0xF0, 0x00, false, false, true)]
    [TestCase(0x2B, 0xFF, 0x80, 0x80, true, true, false)]
    [TestCase(0x2B, 0x80, 0x00, 0x00, false, false, true)]
    public void Anc_CarryEqualsNegative(int opcode, int a, int imm, int result, bool c, bool n, bool z)
    {
        var (cpu, _) = Boot(opcode, imm, 0x77);
        cpu.A = (byte)a;
        cpu.FlagCarry = !c;   // must be overwritten
        Assert.That(cpu.RunInstruction(), Is.EqualTo(2));
        Assert.That(cpu.A, Is.EqualTo(result));
        Assert.That(cpu.FlagCarry, Is.EqualTo(c), "C");
        Assert.That(cpu.FlagNegative, Is.EqualTo(n), "N");
        Assert.That(cpu.FlagZero, Is.EqualTo(z), "Z");
    }

    [TestCase(0xFF, 0xFF, 0x7F, true, false)]
    [TestCase(0xFE, 0xFF, 0x7F, false, false)]
    [TestCase(0x01, 0x01, 0x00, true, true)]
    [TestCase(0xF0, 0x0F, 0x00, false, true)]
    public void Alr_AndThenLsr(int a, int imm, int result, bool c, bool z)
    {
        var (cpu, _) = Boot(0x4B, imm, 0x77);
        cpu.A = (byte)a;
        Assert.That(cpu.RunInstruction(), Is.EqualTo(2));
        Assert.That(cpu.A, Is.EqualTo(result));
        Assert.That(cpu.FlagCarry, Is.EqualTo(c), "C");
        Assert.That(cpu.FlagZero, Is.EqualTo(z), "Z");
        Assert.That(cpu.FlagNegative, Is.False, "N is always clear after a right shift");
    }

    // -----------------------------------------------------------------------------------------------
    // ARR. Binary: A = (A & imm) ROR (with C in), then C = bit 6 of the result and V = bit 6 XOR bit 5.
    // Decimal (unintended opcodes doc, "ARR"): N = old C, Z from the rotated value, V = bit 6 changed by the
    // rotate, then BCD fix-up of both nibbles based on the AND result, C = high nibble was fixed.
    // -----------------------------------------------------------------------------------------------

    // A, imm, carry in, result, C, V, N, Z
    [TestCase(0xFF, 0xFF, true, 0xFF, true, false, true, false)]
    [TestCase(0xFF, 0xFF, false, 0x7F, true, false, false, false)]
    [TestCase(0x40, 0xFF, false, 0x20, false, true, false, false)]   // bit6=0, bit5=1 -> V
    [TestCase(0x80, 0xFF, false, 0x40, true, true, false, false)]    // bit6=1, bit5=0 -> V, C
    [TestCase(0x00, 0xFF, false, 0x00, false, false, false, true)]
    [TestCase(0x00, 0xFF, true, 0x80, false, false, true, false)]
    public void Arr_Binary(int a, int imm, bool carryIn, int result, bool c, bool v, bool n, bool z)
    {
        var (cpu, _) = Boot(0x6B, imm, 0x77);
        cpu.A = (byte)a;
        cpu.FlagCarry = carryIn;
        Assert.That(cpu.RunInstruction(), Is.EqualTo(2));
        Assert.That(cpu.A, Is.EqualTo(result));
        Assert.That(cpu.FlagCarry, Is.EqualTo(c), "C");
        Assert.That(cpu.FlagOverflow, Is.EqualTo(v), "V");
        Assert.That(cpu.FlagNegative, Is.EqualTo(n), "N");
        Assert.That(cpu.FlagZero, Is.EqualTo(z), "Z");
    }

    // A, imm, carry in, result, C, V, N, Z
    [TestCase(0xFF, 0xFF, false, 0xD5, true, false, false, false)]   // both nibbles fixed up (+6, +$60), C from high fix
    [TestCase(0x01, 0x01, true, 0x80, false, false, true, false)]    // N = old carry, no fix-ups
    [TestCase(0x00, 0x00, false, 0x00, false, false, false, true)]
    [TestCase(0x0F, 0xFF, false, 0x0D, false, false, false, false)]  // low nibble $F: ($07 + 6) & $0F = $0D
    public void Arr_Decimal(int a, int imm, bool carryIn, int result, bool c, bool v, bool n, bool z)
    {
        var (cpu, _) = Boot(0x6B, imm, 0x77);
        cpu.A = (byte)a;
        cpu.FlagDecimal = true;
        cpu.FlagCarry = carryIn;
        cpu.RunInstruction();
        Assert.That(cpu.A, Is.EqualTo(result));
        Assert.That(cpu.FlagCarry, Is.EqualTo(c), "C");
        Assert.That(cpu.FlagOverflow, Is.EqualTo(v), "V");
        Assert.That(cpu.FlagNegative, Is.EqualTo(n), "N");
        Assert.That(cpu.FlagZero, Is.EqualTo(z), "Z");
    }

    // -----------------------------------------------------------------------------------------------
    // SBX / AXS: X = (A & X) - imm, C = no borrow, N/Z from X. Decimal mode has no effect.
    // -----------------------------------------------------------------------------------------------

    [TestCase(0xFF, 0x0F, 0x05, false, 0x0A, true, false, false)]
    [TestCase(0x0F, 0x0F, 0x10, false, 0xFF, false, true, false)]
    [TestCase(0xF0, 0x0F, 0x00, false, 0x00, true, false, true)]
    [TestCase(0xFF, 0x0F, 0x05, true, 0x0A, true, false, false)]    // D set: still binary
    [TestCase(0x19, 0x19, 0x01, true, 0x18, true, false, false)]    // D set: no BCD adjust ($19 - 1 = $18)
    public void Sbx_SubtractsFromAAndX(int a, int x, int imm, bool decimalMode, int result, bool c, bool n, bool z)
    {
        var (cpu, _) = Boot(0xCB, imm, 0x77);
        cpu.A = (byte)a;
        cpu.X = (byte)x;
        cpu.FlagDecimal = decimalMode;
        cpu.FlagCarry = false;   // carry in is ignored
        Assert.That(cpu.RunInstruction(), Is.EqualTo(2));
        Assert.That(cpu.X, Is.EqualTo(result));
        Assert.That(cpu.A, Is.EqualTo(a), "A unchanged");
        Assert.That(cpu.FlagCarry, Is.EqualTo(c), "C");
        Assert.That(cpu.FlagNegative, Is.EqualTo(n), "N");
        Assert.That(cpu.FlagZero, Is.EqualTo(z), "Z");
    }

    // -----------------------------------------------------------------------------------------------
    // LAS: A = X = S = M & S (abs,Y; extra cycle on page crossing like a read instruction).
    // -----------------------------------------------------------------------------------------------

    [Test]
    public void Las_AndsMemoryWithStackPointer()
    {
        var (cpu, bus) = Boot(0xBB, 0x34, 0x12, 0x77);
        cpu.S = 0xFD;
        cpu.Y = 0x05;
        bus.Ram[0x1239] = 0x0F;
        Assert.That(cpu.RunInstruction(), Is.EqualTo(4));
        bus.AssertTrace("R 1000 BB", "R 1001 34", "R 1002 12", "R 1239 0F");
        Assert.That((cpu.A, cpu.X, cpu.S), Is.EqualTo(((byte)0x0D, (byte)0x0D, (byte)0x0D)));
        Assert.That(cpu.FlagZero, Is.False);
        Assert.That(cpu.FlagNegative, Is.False);
    }

    [Test]
    public void Las_PageCross_FiveCycles()
    {
        var (cpu, bus) = Boot(0xBB, 0x34, 0x12, 0x77);
        cpu.S = 0xFF;
        cpu.Y = 0xF0;
        bus.Ram[0x1224] = 0x22;
        bus.Ram[0x1324] = 0x80;
        Assert.That(cpu.RunInstruction(), Is.EqualTo(5));
        bus.AssertTrace("R 1000 BB", "R 1001 34", "R 1002 12", "R 1224 22", "R 1324 80");
        Assert.That((cpu.A, cpu.X, cpu.S), Is.EqualTo(((byte)0x80, (byte)0x80, (byte)0x80)));
        Assert.That(cpu.FlagNegative, Is.True);
    }

    // -----------------------------------------------------------------------------------------------
    // SHA / SHX / SHY / TAS: value = reg & (high byte of the base address + 1). When the index crosses a
    // page, the high byte of the target address is replaced by the stored value (unintended opcodes doc,
    // "SHA/SHX/SHY/TAS: ... the value is also used as the high byte of the address if page boundary crossed").
    // Base $1234 -> H+1 = $13; with A & X = $F0 the value is $10, so the crossed address becomes $1024.
    // -----------------------------------------------------------------------------------------------

    [TestCase(0x9F, "SHA abs,Y", 'Y')]
    [TestCase(0x9E, "SHX abs,Y", 'Y')]
    [TestCase(0x9C, "SHY abs,X", 'X')]
    [TestCase(0x9B, "TAS abs,Y", 'Y')]
    public void HighByteStores_NoPageCross_FiveCycles(int opcode, string mnemonic, char index)
    {
        var (cpu, bus) = Boot(opcode, 0x34, 0x12, 0x77);
        cpu.A = 0xF0; cpu.X = 0xF0; cpu.Y = 0xF0;
        cpu.S = 0xFD;
        if (index == 'X') cpu.X = 0x05; else cpu.Y = 0x05;
        if (opcode == 0x9C) { cpu.Y = 0xF0; }   // SHY stores Y, indexes with X
        bus.Ram[0x1239] = 0x11;
        Assert.That(cpu.RunInstruction(), Is.EqualTo(5), mnemonic);
        bus.AssertTrace(
            $"R 1000 {Hex2(opcode)}",
            "R 1001 34",
            "R 1002 12",
            "R 1239 11",
            "W 1239 10");
        if (opcode == 0x9B) Assert.That(cpu.S, Is.EqualTo(0xF0), "TAS: S = A & X");
        else Assert.That(cpu.S, Is.EqualTo(0xFD), "S untouched");
    }

    [TestCase(0x9F, "SHA abs,Y", 'Y')]
    [TestCase(0x9E, "SHX abs,Y", 'Y')]
    [TestCase(0x9C, "SHY abs,X", 'X')]
    [TestCase(0x9B, "TAS abs,Y", 'Y')]
    public void HighByteStores_PageCross_AddressHighByteBecomesStoredValue(int opcode, string mnemonic, char index)
    {
        var (cpu, bus) = Boot(opcode, 0x34, 0x12, 0x77);
        cpu.A = 0xF0; cpu.X = 0xF0; cpu.Y = 0xF0;   // index register = $F0 too: $1234 + $F0 crosses to $1324
        bus.Ram[0x1224] = 0x22;
        Assert.That(cpu.RunInstruction(), Is.EqualTo(5), mnemonic);
        bus.AssertTrace(
            $"R 1000 {Hex2(opcode)}",
            "R 1001 34",
            "R 1002 12",
            "R 1224 22",
            "W 1024 10");
        Assert.That(bus.Ram[0x1324], Is.EqualTo(0), "nothing written at the arithmetically correct address");
        if (opcode == 0x9B) Assert.That(cpu.S, Is.EqualTo(0xF0), "TAS: S = A & X");
    }

    [Test]
    public void Sha_IndirectY_NoPageCross_SixCycles()
    {
        var (cpu, bus) = Boot(0x93, 0x20, 0x77);
        cpu.A = 0xF0; cpu.X = 0xF0; cpu.Y = 0x05;
        bus.Ram[0x20] = 0x34;
        bus.Ram[0x21] = 0x12;
        bus.Ram[0x1239] = 0x11;
        Assert.That(cpu.RunInstruction(), Is.EqualTo(6));
        bus.AssertTrace("R 1000 93", "R 1001 20", "R 0020 34", "R 0021 12", "R 1239 11", "W 1239 10");
    }

    [Test]
    public void Sha_IndirectY_PageCross_AddressHighByteBecomesStoredValue()
    {
        var (cpu, bus) = Boot(0x93, 0x20, 0x77);
        cpu.A = 0xF0; cpu.X = 0xF0; cpu.Y = 0xF0;
        bus.Ram[0x20] = 0x34;
        bus.Ram[0x21] = 0x12;
        bus.Ram[0x1224] = 0x22;
        Assert.That(cpu.RunInstruction(), Is.EqualTo(6));
        bus.AssertTrace("R 1000 93", "R 1001 20", "R 0020 34", "R 0021 12", "R 1224 22", "W 1024 10");
    }

    // -----------------------------------------------------------------------------------------------
    // ANE (XAA) and LAX #imm with the "magic" constant $EE: A = (A | $EE) & X & imm  /  A = X = (A | $EE) & imm.
    // -----------------------------------------------------------------------------------------------

    [TestCase(0xFF, 0x0F, 0x0F, 0x0F)]
    [TestCase(0x00, 0xFF, 0xFF, 0xEE)]   // magic constant visible
    [TestCase(0x00, 0xFF, 0x11, 0x00)]   // $EE & $11 = 0
    [TestCase(0x11, 0xFF, 0x11, 0x11)]   // ($11 | $EE) = $FF
    public void Ane_UsesMagicConstantEE(int a, int x, int imm, int result)
    {
        var (cpu, _) = Boot(0x8B, imm, 0x77);
        cpu.A = (byte)a;
        cpu.X = (byte)x;
        Assert.That(cpu.RunInstruction(), Is.EqualTo(2));
        Assert.That(cpu.A, Is.EqualTo(result));
        Assert.That(cpu.X, Is.EqualTo(x), "X unchanged");
        Assert.That(cpu.FlagZero, Is.EqualTo(result == 0));
        Assert.That(cpu.FlagNegative, Is.EqualTo(result >= 0x80));
    }

    [TestCase(0x00, 0xFF, 0xEE)]
    [TestCase(0x11, 0xFF, 0xFF)]
    [TestCase(0x00, 0x11, 0x00)]
    public void LaxImmediate_UsesMagicConstantEE(int a, int imm, int result)
    {
        var (cpu, _) = Boot(0xAB, imm, 0x77);
        cpu.A = (byte)a;
        Assert.That(cpu.RunInstruction(), Is.EqualTo(2));
        Assert.That((cpu.A, cpu.X), Is.EqualTo(((byte)result, (byte)result)));
        Assert.That(cpu.FlagZero, Is.EqualTo(result == 0));
        Assert.That(cpu.FlagNegative, Is.EqualTo(result >= 0x80));
    }

    // -----------------------------------------------------------------------------------------------
    // NOP variants. Registers and flags are never touched, the bus activity follows the addressing mode.
    // -----------------------------------------------------------------------------------------------

    [TestCase(0x1A)] [TestCase(0x3A)] [TestCase(0x5A)] [TestCase(0x7A)] [TestCase(0xDA)] [TestCase(0xFA)]
    public void Nop_Implied_TwoCycles(int opcode)
    {
        var (cpu, bus) = Boot(opcode, 0x77);
        cpu.A = 0x12; cpu.X = 0x34; cpu.Y = 0x56; cpu.P = 0xFF;
        Assert.That(cpu.RunInstruction(), Is.EqualTo(2));
        bus.AssertTrace($"R 1000 {Hex2(opcode)}", "R 1001 77");
        Assert.That(cpu.PC, Is.EqualTo(0x1001));
        Assert.That((cpu.A, cpu.X, cpu.Y, cpu.P), Is.EqualTo(((byte)0x12, (byte)0x34, (byte)0x56, (byte)0xFF)));
    }

    [TestCase(0x80)] [TestCase(0x82)] [TestCase(0x89)] [TestCase(0xC2)] [TestCase(0xE2)]
    public void Nop_Immediate_TwoCycles(int opcode)
    {
        var (cpu, bus) = Boot(opcode, 0x42, 0x77);
        cpu.P = 0xFF;
        Assert.That(cpu.RunInstruction(), Is.EqualTo(2));
        bus.AssertTrace($"R 1000 {Hex2(opcode)}", "R 1001 42");
        Assert.That(cpu.PC, Is.EqualTo(0x1002));
        Assert.That(cpu.P, Is.EqualTo(0xFF));
    }

    [TestCase(0x04)] [TestCase(0x44)] [TestCase(0x64)]
    public void Nop_ZeroPage_ThreeCycles(int opcode)
    {
        var (cpu, bus) = Boot(opcode, 0x80, 0x77);
        bus.Ram[0x80] = 0x37;
        Assert.That(cpu.RunInstruction(), Is.EqualTo(3));
        bus.AssertTrace($"R 1000 {Hex2(opcode)}", "R 1001 80", "R 0080 37");
        Assert.That(cpu.PC, Is.EqualTo(0x1002));
    }

    [TestCase(0x14)] [TestCase(0x34)] [TestCase(0x54)] [TestCase(0x74)] [TestCase(0xD4)] [TestCase(0xF4)]
    public void Nop_ZeroPageX_FourCycles(int opcode)
    {
        var (cpu, bus) = Boot(opcode, 0x80, 0x77);
        cpu.X = 0x05;
        bus.Ram[0x80] = 0x11;
        bus.Ram[0x85] = 0x37;
        Assert.That(cpu.RunInstruction(), Is.EqualTo(4));
        bus.AssertTrace($"R 1000 {Hex2(opcode)}", "R 1001 80", "R 0080 11", "R 0085 37");
        Assert.That(cpu.PC, Is.EqualTo(0x1002));
    }

    [Test]
    public void Nop_Absolute_FourCycles()
    {
        var (cpu, bus) = Boot(0x0C, 0x34, 0x12, 0x77);
        bus.Ram[0x1234] = 0x37;
        Assert.That(cpu.RunInstruction(), Is.EqualTo(4));
        bus.AssertTrace("R 1000 0C", "R 1001 34", "R 1002 12", "R 1234 37");
        Assert.That(cpu.PC, Is.EqualTo(0x1003));
    }

    [TestCase(0x1C)] [TestCase(0x3C)] [TestCase(0x5C)] [TestCase(0x7C)] [TestCase(0xDC)] [TestCase(0xFC)]
    public void Nop_AbsoluteX_FourCycles_NoPageCross(int opcode)
    {
        var (cpu, bus) = Boot(opcode, 0x34, 0x12, 0x77);
        cpu.X = 0x05;
        bus.Ram[0x1239] = 0x37;
        Assert.That(cpu.RunInstruction(), Is.EqualTo(4));
        bus.AssertTrace($"R 1000 {Hex2(opcode)}", "R 1001 34", "R 1002 12", "R 1239 37");
        Assert.That(cpu.PC, Is.EqualTo(0x1003));
    }

    [TestCase(0x1C)] [TestCase(0x3C)] [TestCase(0x5C)] [TestCase(0x7C)] [TestCase(0xDC)] [TestCase(0xFC)]
    public void Nop_AbsoluteX_FiveCycles_PageCross(int opcode)
    {
        var (cpu, bus) = Boot(opcode, 0x34, 0x12, 0x77);
        cpu.X = 0xF0;
        bus.Ram[0x1224] = 0x22;
        bus.Ram[0x1324] = 0x37;
        Assert.That(cpu.RunInstruction(), Is.EqualTo(5));
        bus.AssertTrace($"R 1000 {Hex2(opcode)}", "R 1001 34", "R 1002 12", "R 1224 22", "R 1324 37");
        Assert.That(cpu.PC, Is.EqualTo(0x1003));
    }

    // -----------------------------------------------------------------------------------------------
    // JAM / KIL: the CPU halts. Unintended opcodes doc ("JAM"): after the opcode fetch the CPU reads PC+1,
    // then $FFFF, $FFFE, $FFFE, and then $FFFF forever; only RESET recovers.
    // -----------------------------------------------------------------------------------------------

    [TestCase(0x02)] [TestCase(0x12)] [TestCase(0x22)] [TestCase(0x32)] [TestCase(0x42)] [TestCase(0x52)]
    [TestCase(0x62)] [TestCase(0x72)] [TestCase(0x92)] [TestCase(0xB2)] [TestCase(0xD2)] [TestCase(0xF2)]
    public void Jam_HaltsWithDocumentedDummyReads(int opcode)
    {
        var (cpu, bus) = Boot(opcode, 0x77);
        bus.Ram[0xFFFE] = 0xAA;
        bus.Ram[0xFFFF] = 0xBB;
        cpu.Clock(7);
        Assert.That(cpu.Jammed, Is.True);
        Assert.That(cpu.AtInstructionBoundary, Is.False);
        Assert.That(cpu.PC, Is.EqualTo(0x1001), "PC stops after the opcode");
        bus.AssertTrace(
            $"R 1000 {Hex2(opcode)}",
            "R 1001 77",
            "R FFFF BB",
            "R FFFE AA",
            "R FFFE AA",
            "R FFFF BB",
            "R FFFF BB");

        bus.Clear();
        cpu.Clock(3);
        bus.AssertTrace("R FFFF BB", "R FFFF BB", "R FFFF BB");
        Assert.That(cpu.Jammed, Is.True);
        Assert.That(cpu.PC, Is.EqualTo(0x1001));
    }

    [Test]
    public void Jam_ResetRecovers()
    {
        var (cpu, bus) = Boot(0x02, 0x77);
        bus.Ram[0xFFFC] = 0x00;
        bus.Ram[0xFFFD] = 0xC0;
        cpu.Clock(10);
        Assert.That(cpu.Jammed, Is.True);
        cpu.Reset();
        Assert.That(cpu.Jammed, Is.False);
        Assert.That(cpu.AtInstructionBoundary, Is.True);
        Assert.That(cpu.PC, Is.EqualTo(0xC000));
        bus.Ram[0xC000] = 0xEA;
        bus.Clear();
        Assert.That(cpu.RunInstruction(), Is.EqualTo(2));
        bus.AssertTrace("R C000 EA", "R C001 00");
    }

    // -----------------------------------------------------------------------------------------------
    // SBC #imm $EB behaves exactly like $E9.
    // -----------------------------------------------------------------------------------------------

    [TestCase(0x50, 0x10, true, 0x40, true, false)]
    [TestCase(0x00, 0x01, true, 0xFF, false, false)]
    [TestCase(0x50, 0xB0, true, 0xA0, false, true)]
    public void SbcEB_IsSbcImmediate(int a, int imm, bool carryIn, int result, bool c, bool v)
    {
        var (cpu, bus) = Boot(0xEB, imm, 0x77);
        cpu.A = (byte)a;
        cpu.FlagCarry = carryIn;
        Assert.That(cpu.RunInstruction(), Is.EqualTo(2));
        bus.AssertTrace("R 1000 EB", $"R 1001 {Hex2(imm)}");
        Assert.That(cpu.A, Is.EqualTo(result));
        Assert.That(cpu.FlagCarry, Is.EqualTo(c), "C");
        Assert.That(cpu.FlagOverflow, Is.EqualTo(v), "V");
        Assert.That(cpu.PC, Is.EqualTo(0x1002));
    }

    [Test]
    public void SbcEB_DecimalMode()
    {
        var (cpu, _) = Boot(0xEB, 0x01, 0x77);
        cpu.A = 0x00;
        cpu.FlagDecimal = true;
        cpu.FlagCarry = true;
        cpu.RunInstruction();
        Assert.That(cpu.A, Is.EqualTo(0x99));
        Assert.That(cpu.FlagCarry, Is.False);
    }
}
