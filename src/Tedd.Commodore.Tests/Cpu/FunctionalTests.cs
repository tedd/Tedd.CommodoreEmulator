using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using NUnit.Framework;
using Tedd.Commodore.Tests.Support;

namespace Tedd.Commodore.Tests.Cpu;

/// <summary>
/// Runs Klaus Dormann's 6502 functional test (https://github.com/Klaus2m5/6502_65C02_functional_tests,
/// GPL-3.0). The 64 KiB image in TestData is loaded at $0000 and started at $0400; it exercises every documented
/// instruction, addressing mode and flag combination (including decimal mode and BRK) and signals the result
/// through the program counter: success is the "jmp *" at $3469, any other self-loop is a failed test.
/// See TestData/README.md.
/// </summary>
[TestFixture]
[Category("Slow")]
public class FunctionalTests
{
    private const ushort StartPc = 0x0400;

    /// <summary>Address of the final "jmp *" (label "success" in 6502_functional_test.lst).</summary>
    private const ushort SuccessAddress = 0x3469;

    private static string ImagePath => Path.Combine(TestContext.CurrentContext.TestDirectory, "TestData", "6502_functional_test.bin");

    [Test]
    public void KlausDormann_FunctionalTest_ReachesSuccessTrap()
    {
        if (!File.Exists(ImagePath))
            Assert.Ignore($"Missing {ImagePath}");
        var image = File.ReadAllBytes(ImagePath);
        Assert.That(image, Has.Length.EqualTo(0x10000), "the functional test image is a full 64 KiB memory dump");

        var host = CpuTestHost.FromBinary(image, 0x0000, StartPc);
        host.Bus.Recording = false; // ~100 million bus accesses; do not keep a trace

        var stopwatch = Stopwatch.StartNew();
        int clocks = host.RunUntilJamOrTrap(maxCycles: 500_000_000);
        stopwatch.Stop();

        TestContext.Out.WriteLine($"Ran {host.Cpu.Cycles:N0} cycles ({clocks:N0} clocks) in {stopwatch.Elapsed.TotalSeconds:F2} s " +
                                  $"({host.Cpu.Cycles / Math.Max(stopwatch.Elapsed.TotalSeconds, 1e-9) / 1e6:F1} MHz). {host.Registers()}");

        if (host.Cpu.Jammed)
            Assert.Fail($"CPU jammed after {host.Cpu.Cycles} cycles. {host.Registers()}\n{Context(host, host.Cpu.PC)}");

        if (host.TrappedPc != SuccessAddress)
        {
            Assert.Fail($"Trapped at ${host.TrappedPc:X4} after {host.Cpu.Cycles} cycles (expected the success trap at ${SuccessAddress:X4}). " +
                        $"Look the address up in 6502_functional_test.lst to see which test failed.\n{host.Registers()}\n{Context(host, (ushort)host.TrappedPc)}");
        }

        // "Stays there": the success trap is a jmp * and must not move on.
        host.RunCycles(300);
        Assert.That(host.Cpu.PC, Is.EqualTo(SuccessAddress));
        Assert.That(host.Cpu.Jammed, Is.False);
    }

    /// <summary>Disassembly around <paramref name="pc"/>; the trapped instruction is marked with an arrow.</summary>
    private static string Context(CpuTestHost host, ushort pc)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Code around the trap (disassembled forward from PC - 24, so the first lines may be misaligned):");
        ushort address = (ushort)Math.Max(0, pc - 24);
        for (int i = 0; i < 24 && address <= pc + 16; i++)
        {
            var (line, length) = Emulator.Cpu.Disassembler.FormatLine(a => host.Ram[a], address);
            sb.Append(address == pc ? "--> " : "    ").AppendLine(line);
            address = (ushort)(address + length);
        }
        sb.AppendLine("Zero page $00-$5F:");
        for (int row = 0; row < 0x60; row += 16)
        {
            sb.Append($"  {row:X2}:");
            for (int i = 0; i < 16; i++)
                sb.Append(' ').Append(host.Ram[row + i].ToString("X2"));
            sb.AppendLine();
        }
        sb.AppendLine("Stack $01F0-$01FF:");
        sb.Append("  ");
        for (int i = 0x1F0; i <= 0x1FF; i++)
            sb.Append(' ').Append(host.Ram[i].ToString("X2"));
        sb.AppendLine();
        return sb.ToString();
    }
}
