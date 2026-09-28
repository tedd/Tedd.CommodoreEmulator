using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using NUnit.Framework;
using Tedd.Commodore.Emulator.Cpu;

namespace Tedd.Commodore.Tests.Support;

/// <summary>
/// Helpers shared by the hand-written CPU tests (<c>Tedd.Commodore.Tests.Cpu.*</c>):
/// CPU construction without a reset-vector read (so the recorded trace starts empty), cycle and instruction
/// stepping, and comparison of the recorded bus trace against an expected trace written as data.
///
/// Expected traces are written as strings of the form <c>"R 1000 A9"</c> / <c>"W 01FD 10"</c>
/// (kind, 16-bit address in hex, 8-bit value in hex), one bus cycle per entry, which is the same shape as the
/// cycle-by-cycle tables in 64doc.txt ("6510 Instruction Timing").
/// </summary>
public static class CpuTestExtensions
{
    /// <summary>Address at which most tests place their code.</summary>
    public const ushort Origin = 0x1000;

    /// <summary>
    /// Creates a CPU on <paramref name="bus"/> with PC, P and S set directly. No reset sequence is run, so
    /// the bus trace is empty afterwards. The default P ($24) is what the core has after construction:
    /// bit 5 set, I set.
    /// </summary>
    public static Cpu6502 CreateCpu(this RecordingBus bus, ushort pc = Origin, byte p = 0x24, byte s = 0xFD)
        => new(bus) { PC = pc, P = p, S = s };

    /// <summary>Runs exactly <paramref name="cycles"/> calls of <see cref="Cpu6502.Clock"/>.</summary>
    public static void Clock(this Cpu6502 cpu, int cycles)
    {
        for (int i = 0; i < cycles; i++)
            cpu.Clock();
    }

    /// <summary>
    /// Clocks until the CPU reaches the next instruction boundary and returns the number of clocks used
    /// (stalled clocks included). A hardware interrupt sequence counts as one "instruction".
    /// </summary>
    public static int RunInstruction(this Cpu6502 cpu, int maxCycles = 64)
    {
        int n = 0;
        do
        {
            cpu.Clock();
            n++;
        } while (!cpu.AtInstructionBoundary && n < maxCycles);
        Assert.That(cpu.AtInstructionBoundary, Is.True, $"instruction did not complete within {maxCycles} cycles");
        return n;
    }

    /// <summary>Runs <paramref name="count"/> instructions (see <see cref="RunInstruction"/>).</summary>
    public static void RunInstructions(this Cpu6502 cpu, int count)
    {
        for (int i = 0; i < count; i++)
            cpu.RunInstruction();
    }

    /// <summary>Read access literal.</summary>
    public static BusAccess R(int address, int value) => new((ushort)address, (byte)value, false);

    /// <summary>Write access literal.</summary>
    public static BusAccess W(int address, int value) => new((ushort)address, (byte)value, true);

    /// <summary>Parses one access written as <c>"R aaaa vv"</c> or <c>"W aaaa vv"</c> (hex).</summary>
    public static BusAccess ParseAccess(string text)
    {
        var parts = text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3 || (parts[0] != "R" && parts[0] != "W"))
            throw new FormatException($"Bad bus access '{text}', expected 'R aaaa vv' or 'W aaaa vv'.");
        return new BusAccess(
            ushort.Parse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(parts[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            parts[0] == "W");
    }

    /// <summary>
    /// Parses a trace. Each string may hold several accesses separated by <c>,</c>, <c>|</c>, <c>;</c> or
    /// newlines, so a trace can be written either one access per argument or as a single block.
    /// </summary>
    public static BusAccess[] ParseTrace(params string[] lines)
    {
        var list = new List<BusAccess>();
        foreach (var line in lines)
        {
            foreach (var item in line.Split(new[] { ',', '|', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!string.IsNullOrWhiteSpace(item))
                    list.Add(ParseAccess(item));
            }
        }
        return list.ToArray();
    }

    /// <summary>Asserts that the recorded trace equals <paramref name="expected"/> exactly (order, address, value, R/W).</summary>
    public static void AssertTrace(this RecordingBus bus, params string[] expected)
        => AssertTrace(bus, ParseTrace(expected));

    /// <summary>Asserts that the recorded trace equals <paramref name="expected"/> exactly (order, address, value, R/W).</summary>
    public static void AssertTrace(this RecordingBus bus, params BusAccess[] expected)
    {
        var actual = bus.Trace;
        int n = Math.Min(actual.Count, expected.Length);
        for (int i = 0; i < n; i++)
        {
            if (actual[i] != expected[i])
            {
                Assert.Fail($"Cycle {i + 1}: expected {expected[i]} but got {actual[i]}.\nExpected trace:\n{Format(expected)}\nActual trace:\n{Format(actual)}");
            }
        }
        if (actual.Count != expected.Length)
        {
            Assert.Fail($"Trace length: expected {expected.Length} cycles but got {actual.Count}.\nExpected trace:\n{Format(expected)}\nActual trace:\n{Format(actual)}");
        }
    }

    /// <summary>Asserts that the recorded trace is empty (no bus activity).</summary>
    public static void AssertNoBusActivity(this RecordingBus bus)
    {
        Assert.That(bus.Trace, Is.Empty, "expected no bus activity but got:\n" + Format(bus.Trace));
    }

    /// <summary>Formats a trace one access per line, numbered from 1.</summary>
    public static string Format(IEnumerable<BusAccess> trace)
    {
        var sb = new StringBuilder();
        int i = 1;
        foreach (var a in trace)
            sb.Append("  ").Append(i++).Append(": ").Append(a).AppendLine();
        return sb.ToString();
    }

    /// <summary>Two-digit upper-case hex, used to splice opcodes into expected traces.</summary>
    public static string Hex2(int value) => value.ToString("X2", CultureInfo.InvariantCulture);
}
