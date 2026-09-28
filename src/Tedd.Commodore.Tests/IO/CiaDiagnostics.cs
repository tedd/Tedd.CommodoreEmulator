using System.Text;
using NUnit.Framework;
using Tedd.Commodore.Emulator.IO;

namespace Tedd.Commodore.Tests.IO;

/// <summary>Cycle-by-cycle traces of the CIA timer pipeline for the Lorenz "cia1ta" and "flipos" sequences.</summary>
[TestFixture, Explicit("diagnostics")]
public class CiaDiagnostics
{
    private static string Trace(Cia6526 cia, int cycles, params (int cycle, int reg, byte value)[] writes)
    {
        var sb = new StringBuilder();
        for (int c = 0; c < cycles; c++)
        {
            // CPU-then-CIA convention: a write in cycle c is applied before the CIA's Clock() for cycle c.
            foreach (var w in writes)
                if (w.cycle == c) { cia.Write(w.reg, w.value); sb.Append($"[w {w.reg:X}={w.value:X2}] "); }
            cia.Clock();
            sb.AppendLine($"c{c,3}: TA={cia.Peek(4) | (cia.Peek(5) << 8):X4} CRA={cia.Peek(14):X2} ICR={cia.Peek(13):X2} IRQ={(cia.IrqLine ? 1 : 0)}");
        }
        return sb.ToString();
    }

    [Test]
    public void Cia1ta_Test19()
    {
        foreach (int i4 in new[] { 10, 11 })
        {
            var cia = new Cia6526("t");
            cia.Write(13, 0x7F); cia.Read(13); cia.Write(13, 0x81);
            cia.Write(14, 0); cia.Write(5, 0);
            cia.Write(4, (byte)i4);          // i4
            cia.Write(14, 0x10);              // force load
            cia.Clock(); cia.Clock(); cia.Clock();
            cia.Read(13);                     // ack
            // cycle 0: CRA = $19 (one-shot + force load + start); cycle 8: latch lo = 20; cycle 12: CRA = $01
            TestContext.Out.WriteLine($"=== i4 = {i4}");
            TestContext.Out.WriteLine(Trace(cia, 18, (0, 14, 0x19), (8, 4, 20), (12, 14, 0x01)));
        }
    }

    [Test]
    public void Flipos_Sequences()
    {
        foreach (var (latch, first, second, label) in new[] { (3, 0x01, 0x09, "set oneshot at t-1 -> expect stop (FF)"), (2, 0x01, 0x09, "set oneshot at t -> expect no stop (FC)"), (3, 0x09, 0x01, "clr oneshot at t-1 -> expect stop (FF)"), (4, 0x09, 0x01, "clr oneshot at t-2") })
        {
            var cia = new Cia6526("t");
            cia.Write(14, 0); cia.Write(15, 0); cia.Write(13, 0x7F); cia.Read(13);
            cia.Write(4, (byte)latch); cia.Write(5, 0);       // counter = latch (loaded because stopped)
            cia.Write(14, 0x21);                              // start in CNT mode (no counting)
            cia.Write(4, 255); cia.Write(5, 255);             // latch = $FFFF
            cia.Write(14, 0x00);                              // stop
            for (int i = 0; i < 20; i++) cia.Clock();
            TestContext.Out.WriteLine($"=== {label}: counter {cia.Peek(4)}");
            // cycle 0: first CRA write; cycle 4: second CRA write; read at cycle 8 (lda $dc04)
            TestContext.Out.WriteLine(Trace(cia, 10, (0, 14, (byte)first), (4, 14, (byte)second)));
        }
    }
}
