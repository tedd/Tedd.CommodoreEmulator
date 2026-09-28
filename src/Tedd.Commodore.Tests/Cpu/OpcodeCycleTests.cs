using System.Collections.Generic;
using NUnit.Framework;
using Tedd.Commodore.Emulator.Cpu;
using Tedd.Commodore.Tests.Support;
using static Tedd.Commodore.Tests.Support.CpuTestExtensions;

namespace Tedd.Commodore.Tests.Cpu;

/// <summary>
/// Exact bus trace (address, value, read/write for every cycle) of every documented opcode, written out as
/// data from the cycle-by-cycle tables in 64doc.txt ("6510 Instruction Timing", John West / Marko Mäkelä).
///
/// Conventions: code is placed at $1000, the byte after the instruction is $77 (so the "read next instruction
/// byte and throw it away" cycles are visible), A = X = Y = 0 unless the test sets them, S = $FD, P = $24.
/// The cycle count is asserted through both the trace length and <see cref="Cpu6502.Cycles"/>.
/// </summary>
[TestFixture]
public class OpcodeCycleTests
{
    // -----------------------------------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------------------------------

    private static (Cpu6502 Cpu, RecordingBus Bus) BootAt(ushort at, params int[] code)
    {
        var bus = new RecordingBus();
        for (int i = 0; i < code.Length; i++)
            bus.Ram[(at + i) & 0xFFFF] = (byte)code[i];
        return (bus.CreateCpu(at), bus);
    }

    private static (Cpu6502 Cpu, RecordingBus Bus) Boot(params int[] code) => BootAt(Origin, code);

    private static void Run(Cpu6502 cpu, RecordingBus bus, int expectedCycles, params string[] expectedTrace)
    {
        int n = cpu.RunInstruction();
        bus.AssertTrace(expectedTrace);
        Assert.That(n, Is.EqualTo(expectedCycles), "clocks used");
        Assert.That(cpu.Cycles, Is.EqualTo(expectedCycles), "Cycles counter");
    }

    // -----------------------------------------------------------------------------------------------
    // The documented instruction set: exactly these 151 opcodes are legal, everything else is undocumented.
    // (MOS MCS6500 Microcomputer Family Programming Manual, Appendix "Instruction list".)
    // -----------------------------------------------------------------------------------------------

    private static readonly byte[] DocumentedOpcodes =
    {
        // ADC   AND   ASL   BCC   BCS   BEQ   BIT   BMI   BNE   BPL   BRK   BVC   BVS   CLC   CLD   CLI   CLV
        0x69, 0x65, 0x75, 0x6D, 0x7D, 0x79, 0x61, 0x71,
        0x29, 0x25, 0x35, 0x2D, 0x3D, 0x39, 0x21, 0x31,
        0x0A, 0x06, 0x16, 0x0E, 0x1E,
        0x90, 0xB0, 0xF0, 0x24, 0x2C, 0x30, 0xD0, 0x10, 0x00, 0x50, 0x70,
        0x18, 0xD8, 0x58, 0xB8,
        // CMP   CPX   CPY   DEC   DEX   DEY   EOR   INC   INX   INY   JMP   JSR
        0xC9, 0xC5, 0xD5, 0xCD, 0xDD, 0xD9, 0xC1, 0xD1,
        0xE0, 0xE4, 0xEC, 0xC0, 0xC4, 0xCC,
        0xC6, 0xD6, 0xCE, 0xDE, 0xCA, 0x88,
        0x49, 0x45, 0x55, 0x4D, 0x5D, 0x59, 0x41, 0x51,
        0xE6, 0xF6, 0xEE, 0xFE, 0xE8, 0xC8,
        0x4C, 0x6C, 0x20,
        // LDA   LDX   LDY   LSR   NOP   ORA   PHA   PHP   PLA   PLP   ROL   ROR   RTI   RTS
        0xA9, 0xA5, 0xB5, 0xAD, 0xBD, 0xB9, 0xA1, 0xB1,
        0xA2, 0xA6, 0xB6, 0xAE, 0xBE,
        0xA0, 0xA4, 0xB4, 0xAC, 0xBC,
        0x4A, 0x46, 0x56, 0x4E, 0x5E,
        0xEA,
        0x09, 0x05, 0x15, 0x0D, 0x1D, 0x19, 0x01, 0x11,
        0x48, 0x08, 0x68, 0x28,
        0x2A, 0x26, 0x36, 0x2E, 0x3E,
        0x6A, 0x66, 0x76, 0x6E, 0x7E,
        0x40, 0x60,
        // SBC   SEC   SED   SEI   STA   STX   STY   TAX   TAY   TSX   TXA   TXS   TYA
        0xE9, 0xE5, 0xF5, 0xED, 0xFD, 0xF9, 0xE1, 0xF1,
        0x38, 0xF8, 0x78,
        0x85, 0x95, 0x8D, 0x9D, 0x99, 0x81, 0x91,
        0x86, 0x96, 0x8E,
        0x84, 0x94, 0x8C,
        0xAA, 0xA8, 0xBA, 0x8A, 0x9A, 0x98,
    };

    [Test]
    public void ThereAre151DocumentedOpcodesAndTheCoreAgrees()
    {
        var set = new HashSet<byte>(DocumentedOpcodes);
        Assert.That(set, Has.Count.EqualTo(151));
        for (int op = 0; op < 256; op++)
        {
            var info = Cpu6502.GetOpcodeInfo((byte)op);
            Assert.That(info.Illegal, Is.EqualTo(!set.Contains((byte)op)), $"opcode ${op:X2} ({info.Mnemonic}) legality");
        }
    }

    // -----------------------------------------------------------------------------------------------
    // Implied / accumulator: 2 cycles.
    // 64doc: "1  PC  R  fetch opcode, increment PC; 2  PC  R  read next instruction byte (and throw it away)"
    // -----------------------------------------------------------------------------------------------

    [TestCase(0x18, "CLC")] [TestCase(0x38, "SEC")] [TestCase(0x58, "CLI")] [TestCase(0x78, "SEI")]
    [TestCase(0xB8, "CLV")] [TestCase(0xD8, "CLD")] [TestCase(0xF8, "SED")]
    [TestCase(0xCA, "DEX")] [TestCase(0x88, "DEY")] [TestCase(0xE8, "INX")] [TestCase(0xC8, "INY")]
    [TestCase(0xAA, "TAX")] [TestCase(0xA8, "TAY")] [TestCase(0x8A, "TXA")] [TestCase(0x98, "TYA")]
    [TestCase(0xBA, "TSX")] [TestCase(0x9A, "TXS")] [TestCase(0xEA, "NOP")]
    [TestCase(0x0A, "ASL A")] [TestCase(0x4A, "LSR A")] [TestCase(0x2A, "ROL A")] [TestCase(0x6A, "ROR A")]
    public void Implied_ReadsNextByteWithoutIncrementingPc(int opcode, string mnemonic)
    {
        var (cpu, bus) = Boot(opcode, 0x77);
        Run(cpu, bus, 2,
            $"R 1000 {Hex2(opcode)}",
            "R 1001 77");
        Assert.That(cpu.PC, Is.EqualTo(0x1001), mnemonic);
    }

    // -----------------------------------------------------------------------------------------------
    // Immediate: 2 cycles.
    // 64doc: "1  PC  R  fetch opcode, increment PC; 2  PC  R  fetch value, increment PC"
    // -----------------------------------------------------------------------------------------------

    [TestCase(0x09, "ORA")] [TestCase(0x29, "AND")] [TestCase(0x49, "EOR")] [TestCase(0x69, "ADC")]
    [TestCase(0xE9, "SBC")] [TestCase(0xC9, "CMP")] [TestCase(0xA9, "LDA")] [TestCase(0xA2, "LDX")]
    [TestCase(0xA0, "LDY")] [TestCase(0xE0, "CPX")] [TestCase(0xC0, "CPY")]
    public void Immediate_TwoCycles(int opcode, string mnemonic)
    {
        var (cpu, bus) = Boot(opcode, 0x42, 0x77);
        Run(cpu, bus, 2,
            $"R 1000 {Hex2(opcode)}",
            "R 1001 42");
        Assert.That(cpu.PC, Is.EqualTo(0x1002), mnemonic);
    }

    // -----------------------------------------------------------------------------------------------
    // Zero page: read 3, write 3, RMW 5.
    // 64doc read: "2  PC  R  fetch address, increment PC; 3  address  R  read from effective address"
    // 64doc RMW:  "3 R read; 4 W write the value back to effective address, and do the operation on it;
    //              5 W write the new value to effective address"
    // -----------------------------------------------------------------------------------------------

    [TestCase(0x05, "ORA")] [TestCase(0x25, "AND")] [TestCase(0x45, "EOR")] [TestCase(0x65, "ADC")]
    [TestCase(0xE5, "SBC")] [TestCase(0xC5, "CMP")] [TestCase(0xA5, "LDA")] [TestCase(0xA6, "LDX")]
    [TestCase(0xA4, "LDY")] [TestCase(0xE4, "CPX")] [TestCase(0xC4, "CPY")] [TestCase(0x24, "BIT")]
    public void ZeroPage_Read_ThreeCycles(int opcode, string mnemonic)
    {
        var (cpu, bus) = Boot(opcode, 0x80, 0x77);
        bus.Ram[0x80] = 0x37;
        Run(cpu, bus, 3,
            $"R 1000 {Hex2(opcode)}",
            "R 1001 80",
            "R 0080 37");
        Assert.That(cpu.PC, Is.EqualTo(0x1002), mnemonic);
    }

    [TestCase(0x85, "STA")] [TestCase(0x86, "STX")] [TestCase(0x84, "STY")]
    public void ZeroPage_Write_ThreeCycles(int opcode, string mnemonic)
    {
        var (cpu, bus) = Boot(opcode, 0x80, 0x77);
        cpu.A = cpu.X = cpu.Y = 0x5A;
        bus.Ram[0x80] = 0x37;
        Run(cpu, bus, 3,
            $"R 1000 {Hex2(opcode)}",
            "R 1001 80",
            "W 0080 5A");
        Assert.That(cpu.PC, Is.EqualTo(0x1002), mnemonic);
    }

    [TestCase(0x06, "ASL", 0x82)] [TestCase(0x46, "LSR", 0x20)] [TestCase(0x26, "ROL", 0x82)]
    [TestCase(0x66, "ROR", 0x20)] [TestCase(0xE6, "INC", 0x42)] [TestCase(0xC6, "DEC", 0x40)]
    public void ZeroPage_Rmw_FiveCycles_WritesOldThenNew(int opcode, string mnemonic, int newValue)
    {
        var (cpu, bus) = Boot(opcode, 0x80, 0x77);
        bus.Ram[0x80] = 0x41;
        Run(cpu, bus, 5,
            $"R 1000 {Hex2(opcode)}",
            "R 1001 80",
            "R 0080 41",
            "W 0080 41",
            $"W 0080 {Hex2(newValue)}");
        Assert.That(cpu.PC, Is.EqualTo(0x1002), mnemonic);
        Assert.That(bus.Ram[0x80], Is.EqualTo(newValue));
    }

    // -----------------------------------------------------------------------------------------------
    // Zero page indexed: read 4, write 4, RMW 6.
    // 64doc: "2  PC  R  fetch address, increment PC; 3  address  R  read from address, add index register to it;
    //         4  address+I*  R  read from effective address"
    // "* The high byte of the effective address is always zero, i.e. page boundary crossings are not handled."
    // -----------------------------------------------------------------------------------------------

    [TestCase(0x15, "ORA", 'X')] [TestCase(0x35, "AND", 'X')] [TestCase(0x55, "EOR", 'X')] [TestCase(0x75, "ADC", 'X')]
    [TestCase(0xF5, "SBC", 'X')] [TestCase(0xD5, "CMP", 'X')] [TestCase(0xB5, "LDA", 'X')] [TestCase(0xB4, "LDY", 'X')]
    [TestCase(0xB6, "LDX", 'Y')]
    public void ZeroPageIndexed_Read_FourCycles_WithDummyReadAtUnindexedAddress(int opcode, string mnemonic, char index)
    {
        var (cpu, bus) = Boot(opcode, 0x80, 0x77);
        if (index == 'X') cpu.X = 0x05; else cpu.Y = 0x05;
        bus.Ram[0x80] = 0x11;
        bus.Ram[0x85] = 0x37;
        Run(cpu, bus, 4,
            $"R 1000 {Hex2(opcode)}",
            "R 1001 80",
            "R 0080 11",
            "R 0085 37");
        Assert.That(cpu.PC, Is.EqualTo(0x1002), mnemonic);
    }

    [TestCase(0x15, "ORA", 'X')] [TestCase(0x35, "AND", 'X')] [TestCase(0x55, "EOR", 'X')] [TestCase(0x75, "ADC", 'X')]
    [TestCase(0xF5, "SBC", 'X')] [TestCase(0xD5, "CMP", 'X')] [TestCase(0xB5, "LDA", 'X')] [TestCase(0xB4, "LDY", 'X')]
    [TestCase(0xB6, "LDX", 'Y')]
    public void ZeroPageIndexed_Read_WrapsInsideZeroPage(int opcode, string mnemonic, char index)
    {
        var (cpu, bus) = Boot(opcode, 0xFE, 0x77);
        if (index == 'X') cpu.X = 0x05; else cpu.Y = 0x05;
        bus.Ram[0xFE] = 0x11;
        bus.Ram[0x03] = 0x37;   // $FE + 5 = $103 -> $03
        bus.Ram[0x103] = 0x99;  // must NOT be read
        Run(cpu, bus, 4,
            $"R 1000 {Hex2(opcode)}",
            "R 1001 FE",
            "R 00FE 11",
            "R 0003 37");
        Assert.That(cpu.PC, Is.EqualTo(0x1002), mnemonic);
    }

    [TestCase(0x95, "STA", 'X')] [TestCase(0x94, "STY", 'X')] [TestCase(0x96, "STX", 'Y')]
    public void ZeroPageIndexed_Write_FourCycles(int opcode, string mnemonic, char index)
    {
        var (cpu, bus) = Boot(opcode, 0x80, 0x77);
        cpu.A = 0x5A;
        if (index == 'X') { cpu.X = 0x05; cpu.Y = 0x5A; } else { cpu.Y = 0x05; cpu.X = 0x5A; }
        bus.Ram[0x80] = 0x11;
        Run(cpu, bus, 4,
            $"R 1000 {Hex2(opcode)}",
            "R 1001 80",
            "R 0080 11",
            "W 0085 5A");
        Assert.That(cpu.PC, Is.EqualTo(0x1002), mnemonic);
    }

    [TestCase(0x95, "STA", 'X')] [TestCase(0x94, "STY", 'X')] [TestCase(0x96, "STX", 'Y')]
    public void ZeroPageIndexed_Write_WrapsInsideZeroPage(int opcode, string mnemonic, char index)
    {
        var (cpu, bus) = Boot(opcode, 0xFE, 0x77);
        cpu.A = 0x5A;
        if (index == 'X') { cpu.X = 0x05; cpu.Y = 0x5A; } else { cpu.Y = 0x05; cpu.X = 0x5A; }
        bus.Ram[0xFE] = 0x11;
        Run(cpu, bus, 4,
            $"R 1000 {Hex2(opcode)}",
            "R 1001 FE",
            "R 00FE 11",
            "W 0003 5A");
        Assert.That(bus.Ram[0x103], Is.EqualTo(0), mnemonic + " must not write outside zero page");
    }

    [TestCase(0x16, "ASL", 0x82)] [TestCase(0x56, "LSR", 0x20)] [TestCase(0x36, "ROL", 0x82)]
    [TestCase(0x76, "ROR", 0x20)] [TestCase(0xF6, "INC", 0x42)] [TestCase(0xD6, "DEC", 0x40)]
    public void ZeroPageX_Rmw_SixCycles(int opcode, string mnemonic, int newValue)
    {
        var (cpu, bus) = Boot(opcode, 0x80, 0x77);
        cpu.X = 0x05;
        bus.Ram[0x80] = 0x11;
        bus.Ram[0x85] = 0x41;
        Run(cpu, bus, 6,
            $"R 1000 {Hex2(opcode)}",
            "R 1001 80",
            "R 0080 11",
            "R 0085 41",
            "W 0085 41",
            $"W 0085 {Hex2(newValue)}");
        Assert.That(cpu.PC, Is.EqualTo(0x1002), mnemonic);
    }

    // -----------------------------------------------------------------------------------------------
    // Absolute: read 4, write 4, RMW 6.
    // 64doc: "2  PC  R  fetch low byte of address, increment PC; 3  PC  R  fetch high byte of address,
    //         increment PC; 4  address  R  read from effective address"
    // -----------------------------------------------------------------------------------------------

    [TestCase(0x0D, "ORA")] [TestCase(0x2D, "AND")] [TestCase(0x4D, "EOR")] [TestCase(0x6D, "ADC")]
    [TestCase(0xED, "SBC")] [TestCase(0xCD, "CMP")] [TestCase(0xAD, "LDA")] [TestCase(0xAE, "LDX")]
    [TestCase(0xAC, "LDY")] [TestCase(0xEC, "CPX")] [TestCase(0xCC, "CPY")] [TestCase(0x2C, "BIT")]
    public void Absolute_Read_FourCycles(int opcode, string mnemonic)
    {
        var (cpu, bus) = Boot(opcode, 0x34, 0x12, 0x77);
        bus.Ram[0x1234] = 0x37;
        Run(cpu, bus, 4,
            $"R 1000 {Hex2(opcode)}",
            "R 1001 34",
            "R 1002 12",
            "R 1234 37");
        Assert.That(cpu.PC, Is.EqualTo(0x1003), mnemonic);
    }

    [TestCase(0x8D, "STA")] [TestCase(0x8E, "STX")] [TestCase(0x8C, "STY")]
    public void Absolute_Write_FourCycles(int opcode, string mnemonic)
    {
        var (cpu, bus) = Boot(opcode, 0x34, 0x12, 0x77);
        cpu.A = cpu.X = cpu.Y = 0x5A;
        Run(cpu, bus, 4,
            $"R 1000 {Hex2(opcode)}",
            "R 1001 34",
            "R 1002 12",
            "W 1234 5A");
        Assert.That(cpu.PC, Is.EqualTo(0x1003), mnemonic);
    }

    [TestCase(0x0E, "ASL", 0x82)] [TestCase(0x4E, "LSR", 0x20)] [TestCase(0x2E, "ROL", 0x82)]
    [TestCase(0x6E, "ROR", 0x20)] [TestCase(0xEE, "INC", 0x42)] [TestCase(0xCE, "DEC", 0x40)]
    public void Absolute_Rmw_SixCycles(int opcode, string mnemonic, int newValue)
    {
        var (cpu, bus) = Boot(opcode, 0x34, 0x12, 0x77);
        bus.Ram[0x1234] = 0x41;
        Run(cpu, bus, 6,
            $"R 1000 {Hex2(opcode)}",
            "R 1001 34",
            "R 1002 12",
            "R 1234 41",
            "W 1234 41",
            $"W 1234 {Hex2(newValue)}");
        Assert.That(cpu.PC, Is.EqualTo(0x1003), mnemonic);
    }

    // -----------------------------------------------------------------------------------------------
    // Absolute indexed: read 4 (+1 on page crossing), write 5, RMW 7.
    // 64doc read: "3  PC  R  fetch high byte of address, add index register to low address byte, increment PC;
    //              4  address+I*  R  read from effective address, fix the high byte of effective address;
    //              5+ address+I   R  re-read from effective address"
    // "* The high byte of the effective address may be invalid at this time, i.e. it may be smaller by $100."
    // "+ This cycle will be executed only if the effective address was invalid during cycle 4."
    // -----------------------------------------------------------------------------------------------

    [TestCase(0x1D, "ORA", 'X')] [TestCase(0x3D, "AND", 'X')] [TestCase(0x5D, "EOR", 'X')] [TestCase(0x7D, "ADC", 'X')]
    [TestCase(0xFD, "SBC", 'X')] [TestCase(0xDD, "CMP", 'X')] [TestCase(0xBD, "LDA", 'X')] [TestCase(0xBC, "LDY", 'X')]
    [TestCase(0x19, "ORA", 'Y')] [TestCase(0x39, "AND", 'Y')] [TestCase(0x59, "EOR", 'Y')] [TestCase(0x79, "ADC", 'Y')]
    [TestCase(0xF9, "SBC", 'Y')] [TestCase(0xD9, "CMP", 'Y')] [TestCase(0xB9, "LDA", 'Y')] [TestCase(0xBE, "LDX", 'Y')]
    public void AbsoluteIndexed_Read_NoPageCross_FourCycles(int opcode, string mnemonic, char index)
    {
        var (cpu, bus) = Boot(opcode, 0x34, 0x12, 0x77);
        if (index == 'X') cpu.X = 0x05; else cpu.Y = 0x05;
        bus.Ram[0x1239] = 0x37;
        Run(cpu, bus, 4,
            $"R 1000 {Hex2(opcode)}",
            "R 1001 34",
            "R 1002 12",
            "R 1239 37");
        Assert.That(cpu.PC, Is.EqualTo(0x1003), mnemonic);
    }

    [TestCase(0x1D, "ORA", 'X')] [TestCase(0x3D, "AND", 'X')] [TestCase(0x5D, "EOR", 'X')] [TestCase(0x7D, "ADC", 'X')]
    [TestCase(0xFD, "SBC", 'X')] [TestCase(0xDD, "CMP", 'X')] [TestCase(0xBD, "LDA", 'X')] [TestCase(0xBC, "LDY", 'X')]
    [TestCase(0x19, "ORA", 'Y')] [TestCase(0x39, "AND", 'Y')] [TestCase(0x59, "EOR", 'Y')] [TestCase(0x79, "ADC", 'Y')]
    [TestCase(0xF9, "SBC", 'Y')] [TestCase(0xD9, "CMP", 'Y')] [TestCase(0xB9, "LDA", 'Y')] [TestCase(0xBE, "LDX", 'Y')]
    public void AbsoluteIndexed_Read_PageCross_FiveCycles_DummyReadAtUnfixedAddress(int opcode, string mnemonic, char index)
    {
        var (cpu, bus) = Boot(opcode, 0x34, 0x12, 0x77);
        if (index == 'X') cpu.X = 0xF0; else cpu.Y = 0xF0;   // $1234 + $F0 = $1324
        bus.Ram[0x1224] = 0x22;   // un-fixed address ($12 high byte, $24 low byte)
        bus.Ram[0x1324] = 0x37;
        Run(cpu, bus, 5,
            $"R 1000 {Hex2(opcode)}",
            "R 1001 34",
            "R 1002 12",
            "R 1224 22",
            "R 1324 37");
        Assert.That(cpu.PC, Is.EqualTo(0x1003), mnemonic);
    }

    // 64doc write: "4  address+I*  R  read from effective address, fix the high byte of effective address;
    //               5  address+I   W  write to effective address"
    [TestCase(0x9D, "STA abs,X", 'X')] [TestCase(0x99, "STA abs,Y", 'Y')]
    public void AbsoluteIndexed_Write_FiveCycles_AlwaysDummyReads(int opcode, string mnemonic, char index)
    {
        var (cpu, bus) = Boot(opcode, 0x34, 0x12, 0x77);
        cpu.A = 0x5A;
        if (index == 'X') cpu.X = 0x05; else cpu.Y = 0x05;
        bus.Ram[0x1239] = 0x11;
        Run(cpu, bus, 5,
            $"R 1000 {Hex2(opcode)}",
            "R 1001 34",
            "R 1002 12",
            "R 1239 11",
            "W 1239 5A");
        Assert.That(cpu.PC, Is.EqualTo(0x1003), mnemonic);
    }

    [TestCase(0x9D, "STA abs,X", 'X')] [TestCase(0x99, "STA abs,Y", 'Y')]
    public void AbsoluteIndexed_Write_PageCross_DummyReadAtUnfixedAddress(int opcode, string mnemonic, char index)
    {
        var (cpu, bus) = Boot(opcode, 0x34, 0x12, 0x77);
        cpu.A = 0x5A;
        if (index == 'X') cpu.X = 0xF0; else cpu.Y = 0xF0;
        bus.Ram[0x1224] = 0x22;
        Run(cpu, bus, 5,
            $"R 1000 {Hex2(opcode)}",
            "R 1001 34",
            "R 1002 12",
            "R 1224 22",
            "W 1324 5A");
        Assert.That(bus.Ram[0x1224], Is.EqualTo(0x22), mnemonic + " must not write to the un-fixed address");
    }

    // 64doc RMW: "4 R read from effective address (high byte may be invalid), fix; 5 R re-read from effective
    //             address; 6 W write the value back, do the operation; 7 W write the new value"
    [TestCase(0x1E, "ASL", 0x82)] [TestCase(0x5E, "LSR", 0x20)] [TestCase(0x3E, "ROL", 0x82)]
    [TestCase(0x7E, "ROR", 0x20)] [TestCase(0xFE, "INC", 0x42)] [TestCase(0xDE, "DEC", 0x40)]
    public void AbsoluteX_Rmw_SevenCycles_NoPageCross(int opcode, string mnemonic, int newValue)
    {
        var (cpu, bus) = Boot(opcode, 0x34, 0x12, 0x77);
        cpu.X = 0x05;
        bus.Ram[0x1239] = 0x41;
        Run(cpu, bus, 7,
            $"R 1000 {Hex2(opcode)}",
            "R 1001 34",
            "R 1002 12",
            "R 1239 41",
            "R 1239 41",
            "W 1239 41",
            $"W 1239 {Hex2(newValue)}");
        Assert.That(cpu.PC, Is.EqualTo(0x1003), mnemonic);
    }

    [TestCase(0x1E, "ASL", 0x82)] [TestCase(0x5E, "LSR", 0x20)] [TestCase(0x3E, "ROL", 0x82)]
    [TestCase(0x7E, "ROR", 0x20)] [TestCase(0xFE, "INC", 0x42)] [TestCase(0xDE, "DEC", 0x40)]
    public void AbsoluteX_Rmw_SevenCycles_PageCross(int opcode, string mnemonic, int newValue)
    {
        var (cpu, bus) = Boot(opcode, 0x34, 0x12, 0x77);
        cpu.X = 0xF0;
        bus.Ram[0x1224] = 0x22;
        bus.Ram[0x1324] = 0x41;
        Run(cpu, bus, 7,
            $"R 1000 {Hex2(opcode)}",
            "R 1001 34",
            "R 1002 12",
            "R 1224 22",
            "R 1324 41",
            "W 1324 41",
            $"W 1324 {Hex2(newValue)}");
        Assert.That(cpu.PC, Is.EqualTo(0x1003), mnemonic);
        Assert.That(bus.Ram[0x1224], Is.EqualTo(0x22));
    }

    // -----------------------------------------------------------------------------------------------
    // (zp,X): read 6, write 6.
    // 64doc: "2  PC  R  fetch pointer address, increment PC; 3  pointer  R  read from the address, add X to it;
    //         4  pointer+X  R  fetch effective address low; 5  pointer+X+1  R  fetch effective address high;
    //         6  address  R  read from effective address"
    // "Note: The effective address is always fetched from zero page, i.e. the zero page boundary crossing is
    //  not handled."
    // -----------------------------------------------------------------------------------------------

    [TestCase(0x01, "ORA")] [TestCase(0x21, "AND")] [TestCase(0x41, "EOR")] [TestCase(0x61, "ADC")]
    [TestCase(0xE1, "SBC")] [TestCase(0xC1, "CMP")] [TestCase(0xA1, "LDA")]
    public void IndirectX_Read_SixCycles(int opcode, string mnemonic)
    {
        var (cpu, bus) = Boot(opcode, 0x20, 0x77);
        cpu.X = 0x04;
        bus.Ram[0x20] = 0x11;   // dummy read target
        bus.Ram[0x24] = 0x34;
        bus.Ram[0x25] = 0x12;
        bus.Ram[0x1234] = 0x37;
        Run(cpu, bus, 6,
            $"R 1000 {Hex2(opcode)}",
            "R 1001 20",
            "R 0020 11",
            "R 0024 34",
            "R 0025 12",
            "R 1234 37");
        Assert.That(cpu.PC, Is.EqualTo(0x1002), mnemonic);
    }

    [TestCase(0x01, "ORA")] [TestCase(0x21, "AND")] [TestCase(0x41, "EOR")] [TestCase(0x61, "ADC")]
    [TestCase(0xE1, "SBC")] [TestCase(0xC1, "CMP")] [TestCase(0xA1, "LDA")]
    public void IndirectX_Read_PointerWrapsInsideZeroPage(int opcode, string mnemonic)
    {
        var (cpu, bus) = Boot(opcode, 0xFE, 0x77);
        cpu.X = 0x01;               // pointer $FE + 1 = $FF; high byte from $00, not $100
        bus.Ram[0xFE] = 0x11;
        bus.Ram[0xFF] = 0x34;
        bus.Ram[0x00] = 0x12;
        bus.Ram[0x100] = 0x99;      // must not be used
        bus.Ram[0x1234] = 0x37;
        Run(cpu, bus, 6,
            $"R 1000 {Hex2(opcode)}",
            "R 1001 FE",
            "R 00FE 11",
            "R 00FF 34",
            "R 0000 12",
            "R 1234 37");
        Assert.That(cpu.PC, Is.EqualTo(0x1002), mnemonic);
    }

    [Test]
    public void IndirectX_Write_SixCycles()
    {
        var (cpu, bus) = Boot(0x81, 0x20, 0x77);   // STA ($20,X)
        cpu.A = 0x5A;
        cpu.X = 0x04;
        bus.Ram[0x20] = 0x11;
        bus.Ram[0x24] = 0x34;
        bus.Ram[0x25] = 0x12;
        Run(cpu, bus, 6,
            "R 1000 81",
            "R 1001 20",
            "R 0020 11",
            "R 0024 34",
            "R 0025 12",
            "W 1234 5A");
        Assert.That(cpu.PC, Is.EqualTo(0x1002));
    }

    [Test]
    public void IndirectX_Write_PointerWrapsInsideZeroPage()
    {
        var (cpu, bus) = Boot(0x81, 0xFB, 0x77);   // STA ($FB,X) with X=4 -> pointer $FF/$00
        cpu.A = 0x5A;
        cpu.X = 0x04;
        bus.Ram[0xFB] = 0x11;
        bus.Ram[0xFF] = 0x34;
        bus.Ram[0x00] = 0x12;
        Run(cpu, bus, 6,
            "R 1000 81",
            "R 1001 FB",
            "R 00FB 11",
            "R 00FF 34",
            "R 0000 12",
            "W 1234 5A");
    }

    // -----------------------------------------------------------------------------------------------
    // (zp),Y: read 5 (+1 on page crossing), write 6.
    // 64doc read: "2  PC  R  fetch pointer address, increment PC; 3  pointer  R  fetch effective address low;
    //              4  pointer+1  R  fetch effective address high, add Y to low byte of effective address;
    //              5  address+Y*  R  read from effective address, fix high byte of effective address;
    //              6+ address+Y   R  read from effective address"
    // "Notes: The effective address is always fetched from zero page, i.e. the zero page boundary crossing
    //  is not handled. * The high byte of the effective address may be invalid at this time, i.e. it may be
    //  smaller by $100. + This cycle will be executed only if the effective address was invalid during cycle 5."
    // -----------------------------------------------------------------------------------------------

    [TestCase(0x11, "ORA")] [TestCase(0x31, "AND")] [TestCase(0x51, "EOR")] [TestCase(0x71, "ADC")]
    [TestCase(0xF1, "SBC")] [TestCase(0xD1, "CMP")] [TestCase(0xB1, "LDA")]
    public void IndirectY_Read_NoPageCross_FiveCycles(int opcode, string mnemonic)
    {
        var (cpu, bus) = Boot(opcode, 0x20, 0x77);
        cpu.Y = 0x05;
        bus.Ram[0x20] = 0x34;
        bus.Ram[0x21] = 0x12;
        bus.Ram[0x1239] = 0x37;
        Run(cpu, bus, 5,
            $"R 1000 {Hex2(opcode)}",
            "R 1001 20",
            "R 0020 34",
            "R 0021 12",
            "R 1239 37");
        Assert.That(cpu.PC, Is.EqualTo(0x1002), mnemonic);
    }

    [TestCase(0x11, "ORA")] [TestCase(0x31, "AND")] [TestCase(0x51, "EOR")] [TestCase(0x71, "ADC")]
    [TestCase(0xF1, "SBC")] [TestCase(0xD1, "CMP")] [TestCase(0xB1, "LDA")]
    public void IndirectY_Read_PageCross_SixCycles_DummyReadAtUnfixedAddress(int opcode, string mnemonic)
    {
        var (cpu, bus) = Boot(opcode, 0x20, 0x77);
        cpu.Y = 0xF0;               // $1234 + $F0 = $1324
        bus.Ram[0x20] = 0x34;
        bus.Ram[0x21] = 0x12;
        bus.Ram[0x1224] = 0x22;
        bus.Ram[0x1324] = 0x37;
        Run(cpu, bus, 6,
            $"R 1000 {Hex2(opcode)}",
            "R 1001 20",
            "R 0020 34",
            "R 0021 12",
            "R 1224 22",
            "R 1324 37");
        Assert.That(cpu.PC, Is.EqualTo(0x1002), mnemonic);
    }

    [TestCase(0x11, "ORA")] [TestCase(0x31, "AND")] [TestCase(0x51, "EOR")] [TestCase(0x71, "ADC")]
    [TestCase(0xF1, "SBC")] [TestCase(0xD1, "CMP")] [TestCase(0xB1, "LDA")]
    public void IndirectY_Read_PointerAtFF_HighByteFromZeroPage(int opcode, string mnemonic)
    {
        var (cpu, bus) = Boot(opcode, 0xFF, 0x77);
        cpu.Y = 0x05;
        bus.Ram[0xFF] = 0x34;
        bus.Ram[0x00] = 0x12;
        bus.Ram[0x100] = 0x99;      // must not be used
        bus.Ram[0x1239] = 0x37;
        Run(cpu, bus, 5,
            $"R 1000 {Hex2(opcode)}",
            "R 1001 FF",
            "R 00FF 34",
            "R 0000 12",
            "R 1239 37");
        Assert.That(cpu.PC, Is.EqualTo(0x1002), mnemonic);
    }

    // 64doc write: "5  address+Y*  R  read from effective address, fix high byte; 6  address+Y  W  write"
    [Test]
    public void IndirectY_Write_SixCycles_AlwaysDummyReads()
    {
        var (cpu, bus) = Boot(0x91, 0x20, 0x77);   // STA ($20),Y
        cpu.A = 0x5A;
        cpu.Y = 0x05;
        bus.Ram[0x20] = 0x34;
        bus.Ram[0x21] = 0x12;
        bus.Ram[0x1239] = 0x11;
        Run(cpu, bus, 6,
            "R 1000 91",
            "R 1001 20",
            "R 0020 34",
            "R 0021 12",
            "R 1239 11",
            "W 1239 5A");
        Assert.That(cpu.PC, Is.EqualTo(0x1002));
    }

    [Test]
    public void IndirectY_Write_PageCross_DummyReadAtUnfixedAddress()
    {
        var (cpu, bus) = Boot(0x91, 0x20, 0x77);
        cpu.A = 0x5A;
        cpu.Y = 0xF0;
        bus.Ram[0x20] = 0x34;
        bus.Ram[0x21] = 0x12;
        bus.Ram[0x1224] = 0x22;
        Run(cpu, bus, 6,
            "R 1000 91",
            "R 1001 20",
            "R 0020 34",
            "R 0021 12",
            "R 1224 22",
            "W 1324 5A");
        Assert.That(bus.Ram[0x1224], Is.EqualTo(0x22));
    }

    // -----------------------------------------------------------------------------------------------
    // Relative (branches): 2 (+1 if taken, +1 more if the branch crosses a page).
    // 64doc: "1  PC  R  fetch opcode, increment PC; 2  PC  R  fetch operand, increment PC;
    //         3  PC  R  Fetch opcode of next instruction, If branch is taken, add operand to PCL. Otherwise
    //            increment PC.
    //         4+ PC*  R  Fetch opcode of next instruction. Fix PCH. If it did not change, increment PC."
    // "* The high byte of Program Counter (PCH) may be invalid at this time, i.e. it may be smaller or bigger
    //  by $100."
    // -----------------------------------------------------------------------------------------------

    // opcode, mnemonic, P for "taken", P for "not taken"
    [TestCase(0x10, "BPL", 0x20, 0xA0)] [TestCase(0x30, "BMI", 0xA0, 0x20)]
    [TestCase(0x50, "BVC", 0x20, 0x60)] [TestCase(0x70, "BVS", 0x60, 0x20)]
    [TestCase(0x90, "BCC", 0x20, 0x21)] [TestCase(0xB0, "BCS", 0x21, 0x20)]
    [TestCase(0xD0, "BNE", 0x20, 0x22)] [TestCase(0xF0, "BEQ", 0x22, 0x20)]
    public void Branch_NotTaken_TwoCycles(int opcode, string mnemonic, int pTaken, int pNotTaken)
    {
        var (cpu, bus) = Boot(opcode, 0x02, 0x77);
        cpu.P = (byte)pNotTaken;
        Run(cpu, bus, 2,
            $"R 1000 {Hex2(opcode)}",
            "R 1001 02");
        Assert.That(cpu.PC, Is.EqualTo(0x1002), mnemonic);
    }

    [TestCase(0x10, "BPL", 0x20, 0xA0)] [TestCase(0x30, "BMI", 0xA0, 0x20)]
    [TestCase(0x50, "BVC", 0x20, 0x60)] [TestCase(0x70, "BVS", 0x60, 0x20)]
    [TestCase(0x90, "BCC", 0x20, 0x21)] [TestCase(0xB0, "BCS", 0x21, 0x20)]
    [TestCase(0xD0, "BNE", 0x20, 0x22)] [TestCase(0xF0, "BEQ", 0x22, 0x20)]
    public void Branch_TakenSamePage_ThreeCycles_DummyReadOfNextOpcode(int opcode, string mnemonic, int pTaken, int pNotTaken)
    {
        var (cpu, bus) = Boot(opcode, 0x02, 0x77);
        cpu.P = (byte)pTaken;
        Run(cpu, bus, 3,
            $"R 1000 {Hex2(opcode)}",
            "R 1001 02",
            "R 1002 77");
        Assert.That(cpu.PC, Is.EqualTo(0x1004), mnemonic);
    }

    [TestCase(0x10, "BPL", 0x20, 0xA0)] [TestCase(0x30, "BMI", 0xA0, 0x20)]
    [TestCase(0x50, "BVC", 0x20, 0x60)] [TestCase(0x70, "BVS", 0x60, 0x20)]
    [TestCase(0x90, "BCC", 0x20, 0x21)] [TestCase(0xB0, "BCS", 0x21, 0x20)]
    [TestCase(0xD0, "BNE", 0x20, 0x22)] [TestCase(0xF0, "BEQ", 0x22, 0x20)]
    public void Branch_TakenForwardPageCross_FourCycles_DummyReadAtUnfixedPc(int opcode, string mnemonic, int pTaken, int pNotTaken)
    {
        // Branch at $10F0 with offset +$20: PC after operand = $10F2, target = $1112, un-fixed = $1012.
        var (cpu, bus) = BootAt(0x10F0, opcode, 0x20, 0x77);
        cpu.P = (byte)pTaken;
        bus.Ram[0x1012] = 0x88;
        Run(cpu, bus, 4,
            $"R 10F0 {Hex2(opcode)}",
            "R 10F1 20",
            "R 10F2 77",
            "R 1012 88");
        Assert.That(cpu.PC, Is.EqualTo(0x1112), mnemonic);
    }

    [TestCase(0x10, "BPL", 0x20, 0xA0)] [TestCase(0x30, "BMI", 0xA0, 0x20)]
    [TestCase(0x50, "BVC", 0x20, 0x60)] [TestCase(0x70, "BVS", 0x60, 0x20)]
    [TestCase(0x90, "BCC", 0x20, 0x21)] [TestCase(0xB0, "BCS", 0x21, 0x20)]
    [TestCase(0xD0, "BNE", 0x20, 0x22)] [TestCase(0xF0, "BEQ", 0x22, 0x20)]
    public void Branch_TakenBackwardPageCross_FourCycles_DummyReadAtUnfixedPc(int opcode, string mnemonic, int pTaken, int pNotTaken)
    {
        // Branch at $1000 with offset -$10: PC after operand = $1002, target = $0FF2, un-fixed = $10F2.
        var (cpu, bus) = Boot(opcode, 0xF0, 0x77);
        cpu.P = (byte)pTaken;
        bus.Ram[0x10F2] = 0x88;
        Run(cpu, bus, 4,
            $"R 1000 {Hex2(opcode)}",
            "R 1001 F0",
            "R 1002 77",
            "R 10F2 88");
        Assert.That(cpu.PC, Is.EqualTo(0x0FF2), mnemonic);
    }

    // -----------------------------------------------------------------------------------------------
    // JMP
    // 64doc abs: "2  PC  R  fetch low address byte, increment PC; 3  PC  R  copy low address byte to PCL,
    //             fetch high address byte to PCH"
    // 64doc ind: "2 fetch pointer address low; 3 fetch pointer address high; 4  pointer  R  fetch low address
    //             to latch; 5  pointer+1*  R  fetch PCH, copy latch to PCL"
    // "* The PCH will always be fetched from the same page than PCL, i.e. page boundary crossing is not handled."
    // -----------------------------------------------------------------------------------------------

    [Test]
    public void JmpAbsolute_ThreeCycles()
    {
        var (cpu, bus) = Boot(0x4C, 0x34, 0x12, 0x77);
        Run(cpu, bus, 3,
            "R 1000 4C",
            "R 1001 34",
            "R 1002 12");
        Assert.That(cpu.PC, Is.EqualTo(0x1234));
    }

    [Test]
    public void JmpIndirect_FiveCycles()
    {
        var (cpu, bus) = Boot(0x6C, 0x20, 0x30, 0x77);
        bus.Ram[0x3020] = 0x34;
        bus.Ram[0x3021] = 0x12;
        Run(cpu, bus, 5,
            "R 1000 6C",
            "R 1001 20",
            "R 1002 30",
            "R 3020 34",
            "R 3021 12");
        Assert.That(cpu.PC, Is.EqualTo(0x1234));
    }

    [Test]
    public void JmpIndirect_PointerAtPageEnd_HighByteFromSamePage()
    {
        var (cpu, bus) = Boot(0x6C, 0xFF, 0x30, 0x77);
        bus.Ram[0x30FF] = 0x34;
        bus.Ram[0x3000] = 0x12;     // used (page wrap)
        bus.Ram[0x3100] = 0x99;     // NOT used
        Run(cpu, bus, 5,
            "R 1000 6C",
            "R 1001 FF",
            "R 1002 30",
            "R 30FF 34",
            "R 3000 12");
        Assert.That(cpu.PC, Is.EqualTo(0x1234));
    }

    // -----------------------------------------------------------------------------------------------
    // JSR / RTS / RTI
    // 64doc JSR: "2  PC  R  fetch low address byte, increment PC; 3  $0100,S  R  internal operation
    //             (predecrement S?); 4  $0100,S  W  push PCH on stack, decrement S; 5  $0100,S  W  push PCL
    //             on stack, decrement S; 6  PC  R  copy low address byte to PCL, fetch high address byte to PCH"
    // 64doc RTS: "2  PC  R  read next instruction byte (and throw it away); 3  $0100,S  R  increment S;
    //             4  $0100,S  R  pull PCL from stack, increment S; 5  $0100,S  R  pull PCH from stack;
    //             6  PC  R  increment PC"
    // 64doc RTI: "2 read next instruction byte (and throw it away); 3  $0100,S  R  increment S;
    //             4 pull P from stack, increment S; 5 pull PCL from stack, increment S; 6 pull PCH from stack"
    // -----------------------------------------------------------------------------------------------

    [Test]
    public void Jsr_SixCycles_DummyStackReadThenPushesReturnAddressMinusOne()
    {
        var (cpu, bus) = Boot(0x20, 0x34, 0x12, 0x77);
        bus.Ram[0x01FD] = 0x99;     // whatever is on the stack is read (and ignored) in cycle 3
        Run(cpu, bus, 6,
            "R 1000 20",
            "R 1001 34",
            "R 01FD 99",
            "W 01FD 10",
            "W 01FC 02",
            "R 1002 12");
        Assert.That(cpu.PC, Is.EqualTo(0x1234));
        Assert.That(cpu.S, Is.EqualTo(0xFB));
    }

    [Test]
    public void Rts_SixCycles_DummyStackReadAndDummyPcRead()
    {
        var (cpu, bus) = Boot(0x60, 0x77);
        cpu.S = 0xFB;
        bus.Ram[0x01FB] = 0x99;
        bus.Ram[0x01FC] = 0x02;
        bus.Ram[0x01FD] = 0x10;
        bus.Ram[0x1002] = 0x66;
        Run(cpu, bus, 6,
            "R 1000 60",
            "R 1001 77",
            "R 01FB 99",
            "R 01FC 02",
            "R 01FD 10",
            "R 1002 66");
        Assert.That(cpu.PC, Is.EqualTo(0x1003));
        Assert.That(cpu.S, Is.EqualTo(0xFD));
    }

    [Test]
    public void Rti_SixCycles_PullsPThenPc()
    {
        var (cpu, bus) = Boot(0x40, 0x77);
        cpu.S = 0xFA;
        bus.Ram[0x01FA] = 0x99;
        bus.Ram[0x01FB] = 0xC3;     // P
        bus.Ram[0x01FC] = 0x02;     // PCL
        bus.Ram[0x01FD] = 0x10;     // PCH
        Run(cpu, bus, 6,
            "R 1000 40",
            "R 1001 77",
            "R 01FA 99",
            "R 01FB C3",
            "R 01FC 02",
            "R 01FD 10");
        Assert.That(cpu.PC, Is.EqualTo(0x1002));
        Assert.That(cpu.S, Is.EqualTo(0xFD));
        Assert.That(cpu.P, Is.EqualTo(0xE3), "bit 5 forced on, bit 4 forced off");
    }

    // -----------------------------------------------------------------------------------------------
    // BRK
    // 64doc: "1  PC  R  fetch opcode, increment PC; 2  PC  R  read next instruction byte (and throw it away),
    //         increment PC; 3  $0100,S  W  push PCH on stack (with B flag set), decrement S; 4  $0100,S  W
    //         push PCL on stack, decrement S; 5  $0100,S  W  push P on stack, decrement S; 6  $FFFE  R  fetch
    //         PCL; 7  $FFFF  R  fetch PCH"
    // -----------------------------------------------------------------------------------------------

    [Test]
    public void Brk_SevenCycles_PushesPcPlus2AndPWithBSet()
    {
        var (cpu, bus) = Boot(0x00, 0x77);
        cpu.P = 0x20;
        bus.Ram[0xFFFE] = 0x00;
        bus.Ram[0xFFFF] = 0x20;
        Run(cpu, bus, 7,
            "R 1000 00",
            "R 1001 77",
            "W 01FD 10",
            "W 01FC 02",
            "W 01FB 30",
            "R FFFE 00",
            "R FFFF 20");
        Assert.That(cpu.PC, Is.EqualTo(0x2000));
        Assert.That(cpu.S, Is.EqualTo(0xFA));
        Assert.That(cpu.FlagInterrupt, Is.True);
    }

    // -----------------------------------------------------------------------------------------------
    // PHA / PHP: 3 cycles, PLA / PLP: 4 cycles
    // 64doc push: "2  PC  R  read next instruction byte (and throw it away); 3  $0100,S  W  push register on
    //              stack, decrement S"
    // 64doc pull: "2  PC  R  read next instruction byte (and throw it away); 3  $0100,S  R  increment S;
    //              4  $0100,S  R  pull register from stack"
    // -----------------------------------------------------------------------------------------------

    [Test]
    public void Pha_ThreeCycles()
    {
        var (cpu, bus) = Boot(0x48, 0x77);
        cpu.A = 0x5A;
        Run(cpu, bus, 3,
            "R 1000 48",
            "R 1001 77",
            "W 01FD 5A");
        Assert.That(cpu.S, Is.EqualTo(0xFC));
        Assert.That(cpu.PC, Is.EqualTo(0x1001));
    }

    [Test]
    public void Php_ThreeCycles_PushesPWithBit4And5Set()
    {
        var (cpu, bus) = Boot(0x08, 0x77);
        cpu.P = 0xC3;
        Run(cpu, bus, 3,
            "R 1000 08",
            "R 1001 77",
            "W 01FD F3");
        Assert.That(cpu.S, Is.EqualTo(0xFC));
        Assert.That(cpu.P, Is.EqualTo(0xC3), "PHP does not change P");
    }

    [Test]
    public void Pla_FourCycles_DummyReadAtOldStackPointer()
    {
        var (cpu, bus) = Boot(0x68, 0x77);
        cpu.S = 0xFC;
        bus.Ram[0x01FC] = 0x99;
        bus.Ram[0x01FD] = 0x5A;
        Run(cpu, bus, 4,
            "R 1000 68",
            "R 1001 77",
            "R 01FC 99",
            "R 01FD 5A");
        Assert.That(cpu.A, Is.EqualTo(0x5A));
        Assert.That(cpu.S, Is.EqualTo(0xFD));
        Assert.That(cpu.PC, Is.EqualTo(0x1001));
    }

    [Test]
    public void Plp_FourCycles_DummyReadAtOldStackPointer()
    {
        var (cpu, bus) = Boot(0x28, 0x77);
        cpu.S = 0xFC;
        bus.Ram[0x01FC] = 0x99;
        bus.Ram[0x01FD] = 0xC3;
        Run(cpu, bus, 4,
            "R 1000 28",
            "R 1001 77",
            "R 01FC 99",
            "R 01FD C3");
        Assert.That(cpu.P, Is.EqualTo(0xE3), "bit 5 forced on, bit 4 forced off");
        Assert.That(cpu.S, Is.EqualTo(0xFD));
    }
}
