using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using Tedd.Commodore.Emulator.C64;
using Tedd.Commodore.Emulator.Cpu;

namespace Tedd.Commodore.Tests.Machine;

[TestFixture, Explicit("debug")]
public class LorenzDebug
{
    [Test]
    public void Trace_Test()
    {
        var dir = Environment.GetEnvironmentVariable("LORENZ_TESTS")!;
        var name = Environment.GetEnvironmentVariable("LORENZ_ONLY") ?? "irq";
        var roms = RomSet.TryLoadDefault()!;
        var c64 = new C64(roms);
        Assert.That(c64.WaitForBasicReady(400), Is.True);
        var prg = File.ReadAllBytes(Path.Combine(dir, name + ".prg"));
        int load = prg[0] | (prg[1] << 8);
        for (int i = 2; i < prg.Length; i++) c64.Memory.Ram[(load + i - 2) & 0xFFFF] = prg[i];
        while (!c64.Cpu.AtInstructionBoundary) c64.Clock();
        c64.Cpu.S = 0xFD; c64.Cpu.P = 0x04; c64.Cpu.PC = 0x080E;
        var hist = new Dictionary<ushort, int>();
        long start = c64.Cycles;
        int nmis = 0, irqs = 0;
        ushort lastPc = 0;
        var firstPcs = new StringBuilder();
        int printed = 0;
        while (c64.Cycles - start < 3_000_000)
        {
            bool wasPending = c64.Cpu.InterruptPending;
            c64.Clock();
            if (!c64.Cpu.AtInstructionBoundary) continue;
            var pc = c64.Cpu.PC;
            if (pc == lastPc) continue;
            lastPc = pc;
            if (c64.Cpu.Nmi) nmis++;
            if (c64.Cpu.Irq) irqs++;
            hist[pc] = hist.GetValueOrDefault(pc) + 1;
            if ((c64.Cycles - start) % 200_000 < 8)
                firstPcs.AppendLine($"@{c64.Cycles - start,8}: raster={c64.Vic.RasterLine} D011={c64.Memory.Peek(0xD011):X2} D012={c64.Memory.Peek(0xD012):X2} port01={c64.Memory.Peek(1):X2} pc={pc:X4} A={c64.Cpu.A:X2} P={c64.Cpu.P:X2}");
            if (printed < 40 && c64.Cycles - start > 1000 && c64.Cycles - start < 1400)
            {
                var (line, _) = Disassembler.FormatLine(a => c64.Memory.Peek(a), pc);
                firstPcs.AppendLine($"{c64.Cycles - start,7} {line}  P={c64.Cpu.P:X2} S={c64.Cpu.S:X2} nmi={c64.Cpu.Nmi} irq={c64.Cpu.Irq} pend={wasPending}");
                printed++;
            }
        }
        var top = hist.OrderByDescending(k => k.Value).Take(15);
        var sb = new StringBuilder();
        foreach (var (pc, n) in top)
        {
            var (line, _) = Disassembler.FormatLine(a => c64.Memory.Peek(a), pc);
            sb.AppendLine($"{n,8}  {line}");
        }
        TestContext.Out.WriteLine($"cycles with NMI line asserted at boundaries: {nmis}, IRQ: {irqs}");
        TestContext.Out.WriteLine(firstPcs.ToString());
        TestContext.Out.WriteLine("Hot PCs:\n" + sb);
        TestContext.Out.WriteLine(c64.GetScreenText());
        TestContext.Out.WriteLine($"CIA2 ICR peek={c64.Cia2.Peek(13):X2} IER? CRA={c64.Cia2.Peek(14):X2} CRB={c64.Cia2.Peek(15):X2}  CIA1 ICR={c64.Cia1.Peek(13):X2}");
    }
}
