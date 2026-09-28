using System;
using System.Collections.Generic;
using System.Text;
using Tedd.Commodore.Emulator.Cpu;
using Tedd.Commodore.Emulator.Tools;

namespace Tedd.Commodore.Tests.Support;

/// <summary>
/// A <see cref="Cpu6502"/> wired to a <see cref="RecordingBus"/> with helpers to load assembled programs and run
/// them until a condition holds. All run helpers stop at instruction boundaries (after the last cycle of an
/// instruction), so <c>Cpu.PC</c> is the address of the next instruction when they return.
/// </summary>
public sealed class CpuTestHost
{
    public readonly RecordingBus Bus = new();
    public readonly Cpu6502 Cpu;

    /// <summary>The assembled program, when the host was created with <see cref="FromAssembly"/>.</summary>
    public AssemblyResult? Assembly { get; private set; }

    /// <summary>PC of the instruction the last <see cref="RunUntilJamOrTrap"/> found looping on itself (or -1).</summary>
    public int TrappedPc { get; private set; } = -1;

    public byte[] Ram => Bus.Ram;
    public List<BusAccess> Trace => Bus.Trace;

    public CpuTestHost()
    {
        Cpu = new Cpu6502(Bus);
    }

    /// <summary>
    /// Assembles <paramref name="source"/> (default origin <paramref name="origin"/>), copies it into RAM, points the
    /// reset vector ($FFFC) at the entry and sets PC to it. The entry is the label <c>start</c> if the program
    /// defines one, otherwise the address of the first emitted byte in source order. Vectors defined by the
    /// program itself (e.g. <c>* = $FFFA</c>) take precedence over the default reset vector.
    /// </summary>
    public static CpuTestHost FromAssembly(string source, ushort origin = 0x1000)
    {
        var result = Assembler.Assemble(source, origin);
        var host = new CpuTestHost { Assembly = result };
        ushort entry = result.Labels.TryGetValue("start", out var start) ? start : result.StartAddress;
        host.Ram[0xFFFC] = (byte)entry;
        host.Ram[0xFFFD] = (byte)(entry >> 8);
        result.CopyTo(host.Ram);
        host.Cpu.PC = entry;
        return host;
    }

    /// <summary>Loads a raw image at <paramref name="loadAddress"/> and sets PC to <paramref name="startPc"/>.</summary>
    public static CpuTestHost FromBinary(byte[] image, ushort loadAddress, ushort startPc)
    {
        var host = new CpuTestHost();
        Array.Copy(image, 0, host.Ram, loadAddress, Math.Min(image.Length, 0x10000 - loadAddress));
        host.Cpu.PC = startPc;
        return host;
    }

    /// <summary>Address of a label from the assembled program.</summary>
    public ushort Label(string name)
    {
        if (Assembly is null || !Assembly.Labels.TryGetValue(name, out var address))
            throw new ArgumentException($"Label '{name}' is not defined.", nameof(name));
        return address;
    }

    /// <summary>
    /// Clocks the CPU until <paramref name="stop"/> returns true at an instruction boundary. Returns the number of
    /// <see cref="Cpu6502.Clock"/> calls made (stalled cycles included). Throws <see cref="TimeoutException"/> if the
    /// condition is not met within <paramref name="maxCycles"/> cycles.
    /// </summary>
    public int RunUntil(Func<Cpu6502, bool> stop, int maxCycles)
    {
        int n = 0;
        while (n < maxCycles)
        {
            Cpu.Clock();
            n++;
            if (Cpu.AtInstructionBoundary && stop(Cpu))
                return n;
            if (Cpu.Jammed)
                throw new InvalidOperationException($"CPU jammed at ${Cpu.PC:X4} after {n} cycles.");
        }
        throw new TimeoutException($"Stop condition not met within {maxCycles} cycles (PC=${Cpu.PC:X4}).");
    }

    /// <summary>Runs until PC equals <paramref name="address"/> at an instruction boundary.</summary>
    public int RunUntilPc(ushort address, int maxCycles = 1_000_000) => RunUntil(c => c.PC == address, maxCycles);

    /// <summary>Runs until PC equals the given label.</summary>
    public int RunUntilLabel(string name, int maxCycles = 1_000_000) => RunUntilPc(Label(name), maxCycles);

    /// <summary>Executes exactly <paramref name="n"/> instructions (an interrupt sequence counts as one). Returns the cycle count.</summary>
    public int RunInstructions(int n)
    {
        int cycles = 0;
        for (int i = 0; i < n; i++)
        {
            do
            {
                Cpu.Clock();
                cycles++;
                if (cycles > 10_000_000)
                    throw new TimeoutException("Instruction did not complete (RDY held low?).");
            } while (!Cpu.AtInstructionBoundary);
        }
        return cycles;
    }

    /// <summary>Calls <see cref="Cpu6502.Clock"/> exactly <paramref name="n"/> times regardless of instruction boundaries.</summary>
    public int RunCycles(int n)
    {
        for (int i = 0; i < n; i++)
            Cpu.Clock();
        return n;
    }

    /// <summary>
    /// Runs until the CPU jams (JAM/KIL opcode) or until an instruction boundary is reached with the same PC as
    /// the previous boundary, i.e. an instruction that jumps/branches to itself ("JMP *" trap). Returns the number
    /// of cycles; <see cref="TrappedPc"/> holds the trap address (-1 when the CPU jammed instead).
    /// </summary>
    public int RunUntilJamOrTrap(int maxCycles = 200_000_000)
    {
        TrappedPc = -1;
        int n = 0;
        int lastPc = -1;
        while (n < maxCycles)
        {
            Cpu.Clock();
            n++;
            if (Cpu.Jammed)
                return n;
            if (Cpu.AtInstructionBoundary)
            {
                if (Cpu.PC == lastPc)
                {
                    TrappedPc = Cpu.PC;
                    return n;
                }
                lastPc = Cpu.PC;
            }
        }
        throw new TimeoutException($"No trap or jam within {maxCycles} cycles (PC=${Cpu.PC:X4}).");
    }

    /// <summary>Disassembles <paramref name="count"/> instructions starting at <paramref name="address"/>.</summary>
    public string Disassemble(ushort address, int count)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < count; i++)
        {
            var (line, length) = Disassembler.FormatLine(a => Ram[a], address);
            sb.AppendLine(line);
            address = (ushort)(address + length);
        }
        return sb.ToString();
    }

    /// <summary>A one-line register dump for assertion messages.</summary>
    public string Registers() =>
        $"PC=${Cpu.PC:X4} A=${Cpu.A:X2} X=${Cpu.X:X2} Y=${Cpu.Y:X2} S=${Cpu.S:X2} P=${Cpu.P:X2} " +
        $"[{(Cpu.FlagNegative ? 'N' : '.')}{(Cpu.FlagOverflow ? 'V' : '.')}-{((Cpu.P & Cpu6502.FlagB) != 0 ? 'B' : '.')}" +
        $"{(Cpu.FlagDecimal ? 'D' : '.')}{(Cpu.FlagInterrupt ? 'I' : '.')}{(Cpu.FlagZero ? 'Z' : '.')}{(Cpu.FlagCarry ? 'C' : '.')}] " +
        $"Cycles={Cpu.Cycles}";
}
