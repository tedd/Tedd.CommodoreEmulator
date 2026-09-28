using System;
using System.Text;

namespace Tedd.Commodore.Emulator.Cpu;

/// <summary>
/// Disassembles 6502 machine code (documented and undocumented opcodes).
/// </summary>
public static class Disassembler
{
    /// <summary>Disassembles one instruction. Returns the text and the instruction length in bytes.</summary>
    public static (string Text, int Length) Disassemble(Func<ushort, byte> read, ushort address)
    {
        byte opcode = read(address);
        var info = Cpu6502.GetOpcodeInfo(opcode);
        byte b1 = read((ushort)(address + 1));
        byte b2 = read((ushort)(address + 2));
        ushort w = (ushort)(b1 | (b2 << 8));
        string operand = info.Mode switch
        {
            AddressingMode.Implied => "",
            AddressingMode.Accumulator => "A",
            AddressingMode.Immediate => $"#${b1:X2}",
            AddressingMode.ZeroPage => $"${b1:X2}",
            AddressingMode.ZeroPageX => $"${b1:X2},X",
            AddressingMode.ZeroPageY => $"${b1:X2},Y",
            AddressingMode.Absolute => $"${w:X4}",
            AddressingMode.AbsoluteX => $"${w:X4},X",
            AddressingMode.AbsoluteY => $"${w:X4},Y",
            AddressingMode.Indirect => $"(${w:X4})",
            AddressingMode.IndirectX => $"(${b1:X2},X)",
            AddressingMode.IndirectY => $"(${b1:X2}),Y",
            AddressingMode.Relative => $"${(ushort)(address + 2 + (sbyte)b1):X4}",
            _ => "?",
        };
        string text = operand.Length == 0 ? info.Mnemonic : info.Mnemonic + " " + operand;
        return (text, info.Length);
    }

    /// <summary>Formats one line: address, raw bytes and mnemonic, e.g. "FCE2  A2 FF     LDX #$FF".</summary>
    public static (string Line, int Length) FormatLine(Func<ushort, byte> read, ushort address)
    {
        var (text, length) = Disassemble(read, address);
        var sb = new StringBuilder();
        sb.Append(address.ToString("X4")).Append("  ");
        for (int i = 0; i < 3; i++)
        {
            if (i < length)
                sb.Append(read((ushort)(address + i)).ToString("X2")).Append(' ');
            else
                sb.Append("   ");
        }
        sb.Append(' ').Append(text);
        return (sb.ToString(), length);
    }
}
