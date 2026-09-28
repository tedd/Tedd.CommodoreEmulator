using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Tedd.Commodore.Emulator.C64;
using Tedd.Commodore.Emulator.Cpu;
using Tedd.Commodore.Emulator.Drive;

namespace Tedd.Commodore.Tests.Machine;

[TestFixture]
[Explicit("diagnostics")]
public class DriveDiagnostics
{
    [Test]
    public void Trace_Iec_During_Load()
    {
        var roms = RomSet.TryLoadDefault();
        if (roms?.Drive1541 is null) Assert.Ignore();
        var c64 = new C64(roms!);
        var drive = c64.AttachDrive(8);
        var diskPath = Path.Combine(roms!.Directory, "Frogger '93 (Europe).D64");
        drive.InsertDisk(GcrDisk.FromD64(D64Image.Load(diskPath)));

        // Sample drive PC while booting
        var pcs = new Dictionary<ushort, int>();
        for (int f = 0; f < 400 && !c64.IsBasicReady(); f++)
        {
            c64.RunFrame();
            if (f % 20 == 0)
                TestContext.Out.WriteLine($"frame {f}: drivePC={drive.Cpu.PC:X4} cycles={drive.Cycles} VIA1 PB={drive.Via1.Peek(0):X2} DDRB={drive.Via1.Peek(2):X2} PCR={drive.Via1.Peek(12):X2} IER={drive.Via1.Peek(14):X2} VIA2 PB={drive.Via2.Peek(0):X2} DDRB={drive.Via2.Peek(2):X2} PCR={drive.Via2.Peek(12):X2} ATN={c64.Iec.AtnLow} CLK={c64.Iec.ClkLow} DATA={c64.Iec.DataLow} motor={drive.MotorOn} led={drive.Led} track={drive.Disk.Track}");
        }
        Assert.That(c64.IsBasicReady(), Is.True);

        // Histogram of drive PC over one frame (idle loop)
        for (int i = 0; i < 20000; i++)
        {
            c64.Clock();
            var pc = drive.Cpu.PC;
            pcs[pc] = pcs.GetValueOrDefault(pc) + 1;
        }
        var top = new List<KeyValuePair<ushort, int>>(pcs);
        top.Sort((a, b) => b.Value.CompareTo(a.Value));
        TestContext.Out.WriteLine("Drive idle loop PCs:");
        for (int i = 0; i < Math.Min(12, top.Count); i++)
        {
            var (line, _) = Disassembler.FormatLine(a => drive.Memory.Peek(a), top[i].Key);
            TestContext.Out.WriteLine($"  {top[i].Value,6}  {line}");
        }

        c64.TypeText("LOAD\"$\",8\n");
        bool lastAtn = c64.Iec.AtnLow, lastClk = c64.Iec.ClkLow, lastData = c64.Iec.DataLow;
        int events = 0;
        long start = c64.Cycles;
        for (int i = 0; i < 985248 * 3 && events < 200; i++)
        {
            c64.Clock();
            if (c64.Iec.AtnLow != lastAtn || c64.Iec.ClkLow != lastClk || c64.Iec.DataLow != lastData)
            {
                lastAtn = c64.Iec.AtnLow; lastClk = c64.Iec.ClkLow; lastData = c64.Iec.DataLow;
                events++;
                TestContext.Out.WriteLine($"+{c64.Cycles - start,8}: ATN={(lastAtn ? 'L' : 'H')} CLK={(lastClk ? 'L' : 'H')} DATA={(lastData ? 'L' : 'H')}  c64PC={c64.Cpu.PC:X4} drivePC={drive.Cpu.PC:X4} VIA1PB={drive.Via1.Peek(0):X2} CA1={drive.Via1.Ca1} IFR={drive.Via1.Peek(13):X2} IER={drive.Via1.Peek(14):X2} driveI={drive.Cpu.FlagInterrupt}");
            }
        }
        TestContext.Out.WriteLine(c64.GetScreenText());
        TestContext.Out.WriteLine($"drivePC={drive.Cpu.PC:X4} jammed={drive.Cpu.Jammed}");
    }
}
