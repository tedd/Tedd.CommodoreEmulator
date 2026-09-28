using NUnit.Framework;
using Tedd.Commodore.Emulator.Cpu;
using Tedd.Commodore.Tests.Support;

namespace Tedd.Commodore.Tests.Cpu;

/// <summary>
/// RDY pin (BA from the VIC-II). MOS 6500 hardware manual / 64doc.txt: "the 6510 halts on the next read
/// cycle when RDY is low; write cycles are always executed" - the CPU keeps running through the up to three
/// consecutive write cycles of an instruction and stops at the first read.
/// </summary>
[TestFixture]
public class RdyTests
{
    private const byte U = Cpu6502.FlagU;

    private static (Cpu6502 Cpu, RecordingBus Bus) Boot(params int[] code)
    {
        var bus = new RecordingBus();
        for (int i = 0; i < code.Length; i++)
            bus.Ram[0x1000 + i] = (byte)code[i];
        return (bus.CreateCpu(p: U), bus);
    }

    [Test]
    public void RdyLow_DuringReadCycle_StallsWithoutBusAccess()
    {
        var (cpu, bus) = Boot(0xAD, 0x34, 0x12, 0xEA);   // LDA $1234
        bus.Ram[0x1234] = 0x37;
        cpu.Clock(1);
        Assert.That(cpu.Cycles, Is.EqualTo(1));
        bus.Clear();

        cpu.Rdy = false;
        cpu.Clock(3);
        bus.AssertNoBusActivity();
        Assert.That(cpu.Cycles, Is.EqualTo(1), "stalled cycles are not counted as executed");
        Assert.That(cpu.StallCycles, Is.EqualTo(3));
        Assert.That(cpu.AtInstructionBoundary, Is.False);

        cpu.Rdy = true;
        cpu.Clock(3);
        bus.AssertTrace("R 1001 34", "R 1002 12", "R 1234 37");
        Assert.That(cpu.A, Is.EqualTo(0x37));
        Assert.That(cpu.Cycles, Is.EqualTo(4));
        Assert.That(cpu.StallCycles, Is.EqualTo(3));
        Assert.That(cpu.AtInstructionBoundary, Is.True);
    }

    [Test]
    public void RdyLow_OpcodeFetchStalls()
    {
        var (cpu, bus) = Boot(0xEA, 0xEA);
        cpu.Rdy = false;
        cpu.Clock(5);
        bus.AssertNoBusActivity();
        Assert.That(cpu.StallCycles, Is.EqualTo(5));
        Assert.That(cpu.Cycles, Is.EqualTo(0));
        Assert.That(cpu.AtInstructionBoundary, Is.True);
        Assert.That(cpu.PC, Is.EqualTo(0x1000));

        cpu.Rdy = true;
        cpu.Clock(2);
        bus.AssertTrace("R 1000 EA", "R 1001 EA");
        Assert.That(cpu.PC, Is.EqualTo(0x1001));
    }

    [Test]
    public void RdyLow_BeforeWriteCycle_WriteCompletes_ThenNextFetchStalls()
    {
        var (cpu, bus) = Boot(0x8D, 0x34, 0x12, 0xEA);   // STA $1234
        cpu.A = 0x5A;
        cpu.Clock(3);
        bus.Clear();

        cpu.Rdy = false;
        cpu.Clock(1);                                  // the write cycle runs
        bus.AssertTrace("W 1234 5A");
        Assert.That(cpu.Cycles, Is.EqualTo(4));
        Assert.That(cpu.StallCycles, Is.EqualTo(0));
        Assert.That(cpu.AtInstructionBoundary, Is.True);

        bus.Clear();
        cpu.Clock(2);                                  // opcode fetch of the NOP stalls
        bus.AssertNoBusActivity();
        Assert.That(cpu.StallCycles, Is.EqualTo(2));
        Assert.That(cpu.Cycles, Is.EqualTo(4));

        cpu.Rdy = true;
        cpu.Clock(1);
        bus.AssertTrace("R 1003 EA");
    }

    [Test]
    public void RdyLow_DuringRmw_BothWritesProceed()
    {
        var (cpu, bus) = Boot(0xEE, 0x34, 0x12, 0xEA);   // INC $1234
        bus.Ram[0x1234] = 0x41;
        cpu.Clock(4);                                  // up to and including the read of the operand
        bus.Clear();

        cpu.Rdy = false;
        cpu.Clock(1);                                  // dummy write of the old value
        bus.AssertTrace("W 1234 41");
        cpu.Clock(1);                                  // write of the new value
        bus.AssertTrace("W 1234 41", "W 1234 42");
        Assert.That(cpu.AtInstructionBoundary, Is.True);
        Assert.That(cpu.StallCycles, Is.EqualTo(0));
        Assert.That(cpu.Cycles, Is.EqualTo(6));

        cpu.Clock(1);                                  // next opcode fetch stalls
        Assert.That(cpu.StallCycles, Is.EqualTo(1));
        Assert.That(bus.Trace, Has.Count.EqualTo(2));
    }

    [Test]
    public void RdyLow_BetweenTheTwoRmwWrites_SecondWriteProceeds()
    {
        var (cpu, bus) = Boot(0xE6, 0x80, 0xEA);   // INC $80
        bus.Ram[0x80] = 0xFF;
        cpu.Clock(4);                                  // fetch, zp address, read, first (dummy) write
        bus.AssertTrace("R 1000 E6", "R 1001 80", "R 0080 FF", "W 0080 FF");
        cpu.Rdy = false;
        cpu.Clock(1);
        bus.AssertTrace("R 1000 E6", "R 1001 80", "R 0080 FF", "W 0080 FF", "W 0080 00");
        Assert.That(cpu.StallCycles, Is.EqualTo(0));
        Assert.That(cpu.FlagZero, Is.True);
    }

    [Test]
    public void RdyLow_BeforeRmwRead_StallsThenCompletesIdentically()
    {
        var (cpu, bus) = Boot(0xEE, 0x34, 0x12, 0xEA);   // INC $1234
        bus.Ram[0x1234] = 0x41;
        cpu.Clock(3);
        cpu.Rdy = false;
        cpu.Clock(4);                                  // stalled at the operand read
        Assert.That(cpu.StallCycles, Is.EqualTo(4));
        Assert.That(bus.Trace, Has.Count.EqualTo(3));
        cpu.Rdy = true;
        cpu.Clock(3);
        bus.AssertTrace("R 1000 EE", "R 1001 34", "R 1002 12", "R 1234 41", "W 1234 41", "W 1234 42");
        Assert.That(bus.Ram[0x1234], Is.EqualTo(0x42));
        Assert.That(cpu.Cycles, Is.EqualTo(6));
    }

    [Test]
    public void RdyLow_DuringJsr_PushesProceedThenVectorFetchStalls()
    {
        var (cpu, bus) = Boot(0x20, 0x00, 0x20, 0xEA);   // JSR $2000
        cpu.Clock(3);                                  // opcode, ADL, dummy stack read
        cpu.Rdy = false;
        cpu.Clock(2);                                  // two pushes run
        Assert.That(cpu.StallCycles, Is.EqualTo(0));
        cpu.Clock(3);                                  // ADH fetch (a read) stalls
        Assert.That(cpu.StallCycles, Is.EqualTo(3));
        cpu.Rdy = true;
        cpu.Clock(1);
        bus.AssertTrace("R 1000 20", "R 1001 00", "R 01FD 00", "W 01FD 10", "W 01FC 02", "R 1002 20");
        Assert.That(cpu.PC, Is.EqualTo(0x2000));
        Assert.That(cpu.S, Is.EqualTo(0xFB));
    }

    [Test]
    public void RdyLow_DuringBrk_ThreePushesProceedThenVectorFetchStalls()
    {
        var (cpu, bus) = Boot(0x00, 0x77);
        bus.Ram[0xFFFE] = 0x00; bus.Ram[0xFFFF] = 0x20;
        cpu.Clock(2);
        cpu.Rdy = false;
        cpu.Clock(3);                                  // PCH, PCL, P pushes
        Assert.That(cpu.StallCycles, Is.EqualTo(0));
        cpu.Clock(1);
        Assert.That(cpu.StallCycles, Is.EqualTo(1));
        cpu.Rdy = true;
        cpu.Clock(2);
        bus.AssertTrace("R 1000 00", "R 1001 77", "W 01FD 10", "W 01FC 02", "W 01FB 30", "R FFFE 00", "R FFFF 20");
        Assert.That(cpu.PC, Is.EqualTo(0x2000));
    }

    [Test]
    public void ResultsAreIdenticalWithAndWithoutStalls()
    {
        // A short program: LDX #$03; loop: STA $0200,X; DEX; BPL loop; INC $80; JMP done
        int[] program =
        {
            0xA9, 0x5A,             // LDA #$5A
            0xA2, 0x03,             // LDX #$03
            0x9D, 0x00, 0x02,       // STA $0200,X
            0xCA,                   // DEX
            0x10, 0xFA,             // BPL -6
            0xE6, 0x80,             // INC $80
            0x4C, 0x0C, 0x10,       // JMP *
        };
        var (reference, refBus) = Boot(program);
        var (stalled, stalledBus) = Boot(program);
        refBus.Ram[0x80] = 0x41;
        stalledBus.Ram[0x80] = 0x41;

        for (int i = 0; i < 60; i++)
            reference.Clock();

        // Toggle RDY in a fixed pseudo-random pattern; keep going until the same number of *executed* cycles.
        uint lfsr = 0xACE1;
        while (stalled.Cycles < reference.Cycles)
        {
            lfsr = (lfsr >> 1) ^ (uint)(-(int)(lfsr & 1) & 0xB400);
            stalled.Rdy = (lfsr & 3) != 0;
            stalled.Clock();
        }
        stalled.Rdy = true;

        Assert.That(stalled.StallCycles, Is.GreaterThan(0), "the pattern did stall the CPU");
        Assert.That(stalledBus.Trace, Is.EqualTo(refBus.Trace), "bus traces must match cycle for cycle");
        Assert.That((stalled.A, stalled.X, stalled.PC, stalled.P, stalled.S),
            Is.EqualTo((reference.A, reference.X, reference.PC, reference.P, reference.S)));
        Assert.That(stalledBus.Ram, Is.EqualTo(refBus.Ram));
    }

    [Test]
    public void StepCountsStalledCycles()
    {
        var (cpu, _) = Boot(0xEA, 0xEA);
        cpu.Rdy = true;
        Assert.That(cpu.Step(), Is.EqualTo(2));
        cpu.Rdy = false;
        cpu.Clock(3);
        cpu.Rdy = true;
        Assert.That(cpu.Step(), Is.EqualTo(2));
        Assert.That(cpu.StallCycles, Is.EqualTo(3));
        Assert.That(cpu.Cycles, Is.EqualTo(4));
    }
}
