namespace Tedd.Commodore.Emulator.Cpu;

public enum AddressingMode
{
    Implied,
    Accumulator,
    Immediate,
    ZeroPage,
    ZeroPageX,
    ZeroPageY,
    Absolute,
    AbsoluteX,
    AbsoluteY,
    Indirect,
    IndirectX,
    IndirectY,
    Relative,
}

/// <summary>
/// Static description of one opcode: mnemonic, addressing mode, byte length and whether it is an
/// undocumented ("illegal") NMOS opcode.
/// </summary>
public readonly record struct OpcodeInfo(byte Opcode, string Mnemonic, AddressingMode Mode, bool Illegal)
{
    public int Length => Mode switch
    {
        AddressingMode.Implied or AddressingMode.Accumulator => 1,
        AddressingMode.Absolute or AddressingMode.AbsoluteX or AddressingMode.AbsoluteY or AddressingMode.Indirect => 3,
        _ => 2,
    };
}
