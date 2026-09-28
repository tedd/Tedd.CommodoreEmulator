using System.Text;
using NUnit.Framework;
using Tedd.Commodore.Emulator.C128;

namespace Tedd.Commodore.Tests.Machine;

/// <summary>Diagnostics for bringing up the C128: traces the Z80 BIOS, the MMU and mode switches. Run explicitly.</summary>
[TestFixture]
[Explicit]
public class C128Debug
{
    [Test]
    public void Trace_Boot()
    {
        var c128 = new C128(C128SystemTests.Roms());
        var sb = new StringBuilder();
        long lastInstr = -1;
        byte lastCr = c128.Mmu.Cr, lastMcr = c128.Mmu.Mcr;
        int lines = 0;
        for (int cyc = 0; cyc < 400_000 && lines < 400; cyc++)
        {
            bool z80 = c128.Z80Active;
            if (z80 && c128.Z80.Instructions != lastInstr)
            {
                lastInstr = c128.Z80.Instructions;
                var z = c128.Z80;
                sb.Append($"{cyc,7} Z80 PC={z.PC:X4} {c128.Memory.ReadZ80(z.PC):X2} {c128.Memory.ReadZ80((ushort)(z.PC + 1)):X2} {c128.Memory.ReadZ80((ushort)(z.PC + 2)):X2} {c128.Memory.ReadZ80((ushort)(z.PC + 3)):X2}");
                sb.Append($"  AF={z.AF:X4} BC={z.BC:X4} DE={z.DE:X4} HL={z.HL:X4} SP={z.SP:X4} IX={z.IX:X4}").AppendLine();
                lines++;
            }
            else if (!z80 && c128.Cpu.AtInstructionBoundary)
            {
                sb.Append($"{cyc,7} 8502 PC={c128.Cpu.PC:X4} {c128.Memory.Peek(c128.Cpu.PC):X2} {c128.Memory.Peek((ushort)(c128.Cpu.PC + 1)):X2} {c128.Memory.Peek((ushort)(c128.Cpu.PC + 2)):X2} A={c128.Cpu.A:X2} X={c128.Cpu.X:X2} Y={c128.Cpu.Y:X2} S={c128.Cpu.S:X2}").AppendLine();
                lines++;
            }
            c128.Clock();
            if (c128.Mmu.Cr != lastCr || c128.Mmu.Mcr != lastMcr)
            {
                lastCr = c128.Mmu.Cr;
                lastMcr = c128.Mmu.Mcr;
                sb.Append($"{cyc,7} MMU CR={lastCr:X2} MCR={lastMcr:X2} z80={c128.Z80Active}").AppendLine();
            }
        }
        TestContext.Out.WriteLine(sb.ToString());
    }

    [Test]
    public void Trace_Go64()
    {
        var c128 = C128SystemTests.Boot();
        c128.TypeText("GO64\n");
        for (int i = 0; i < 100 && c128.PendingTypeAhead > 0; i++) c128.RunFrame();
        for (int i = 0; i < 30; i++) c128.RunFrame();
        c128.TypeText("Y\n");
        var sb = new StringBuilder();
        int lines = 0;
        long cyc = 0;
        while (!c128.C64Mode && cyc < 200) { c128.RunFrame(); cyc++; }
        // Now catch the switch instruction by instruction: rewind is impossible, so trace from here.
        sb.Append($"C64 mode after {cyc} cycles; PC={c128.Cpu.PC:X4} $01={c128.Memory.PortData:X2} DDR={c128.Memory.PortDdr:X2} CR={c128.Mmu.Cr:X2} RCR={c128.Mmu.Rcr:X2}").AppendLine();
        for (int n = 0; n < 4000 && lines < 300; n++)
        {
            if (c128.Cpu.AtInstructionBoundary)
            {
                var pc = c128.Cpu.PC;
                sb.Append($"{n,6} PC={pc:X4} {c128.Memory.Peek(pc):X2} {c128.Memory.Peek((ushort)(pc + 1)):X2} {c128.Memory.Peek((ushort)(pc + 2)):X2}  A={c128.Cpu.A:X2} X={c128.Cpu.X:X2} Y={c128.Cpu.Y:X2} S={c128.Cpu.S:X2} $01={c128.Memory.PortData:X2}").AppendLine();
                lines++;
            }
            c128.Clock();
        }
        for (int f = 0; f < 200; f++) c128.RunFrame();
        sb.Append($"later: PC={c128.Cpu.PC:X4} $01={c128.Memory.PortData:X2} CR={c128.Mmu.Cr:X2} jammed={c128.Cpu.Jammed}").AppendLine();
        sb.Append(c128.GetScreenText());
        TestContext.Out.WriteLine(sb.ToString());
    }
}
