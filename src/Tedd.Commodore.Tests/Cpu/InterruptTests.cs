using NUnit.Framework;
using Tedd.Commodore.Emulator.Cpu;
using Tedd.Commodore.Tests.Support;

namespace Tedd.Commodore.Tests.Cpu;

/// <summary>
/// IRQ / NMI / BRK behaviour: the 7-cycle interrupt sequence (64doc.txt "Interrupts"), edge/level
/// sensitivity, priority, the "interrupts are polled during the last cycle of an instruction using the state
/// of the lines one cycle earlier" rule that produces the SEI/CLI/PLP one-instruction delay, the taken-branch
/// delay, and NMI hijacking a BRK (64doc.txt: "if NMI is asserted during the BRK sequence the NMI vector is
/// used instead of the IRQ vector, and the NMI is consumed").
///
/// Memory layout used by every test: code at $1000, IRQ vector -> $2000, NMI vector -> $3000. Handlers are
/// filled by the individual test.
/// </summary>
[TestFixture]
public class InterruptTests
{
    private const byte U = Cpu6502.FlagU;
    private const byte I = Cpu6502.FlagI;
    private const byte C = Cpu6502.FlagC;

    private const byte NOP = 0xEA;
    private const byte RTI = 0x40;
    private const byte CLI = 0x58;
    private const byte SEI = 0x78;
    private const byte PLP = 0x28;
    private const byte BRK = 0x00;

    private static (Cpu6502 Cpu, RecordingBus Bus) Boot(byte p, params int[] code)
    {
        var bus = new RecordingBus();
        for (int i = 0; i < code.Length; i++)
            bus.Ram[0x1000 + i] = (byte)code[i];
        bus.Ram[0xFFFE] = 0x00; bus.Ram[0xFFFF] = 0x20;   // IRQ/BRK -> $2000
        bus.Ram[0xFFFA] = 0x00; bus.Ram[0xFFFB] = 0x30;   // NMI     -> $3000
        bus.Ram[0x2000] = RTI;
        bus.Ram[0x3000] = RTI;
        return (bus.CreateCpu(p: p), bus);
    }

    // -----------------------------------------------------------------------------------------------
    // The sequences themselves
    // -----------------------------------------------------------------------------------------------

    [Test]
    public void Irq_SevenCycleSequence()
    {
        // 64doc: "1 PC R fetch opcode (and discard it - PC is not incremented); 2 PC R read next instruction
        // byte (and throw it away); 3 push PCH; 4 push PCL; 5 push P (B flag clear); 6 $FFFE R fetch PCL;
        // 7 $FFFF R fetch PCH". I is set after P was pushed.
        var (cpu, bus) = Boot(U | C, NOP, NOP);
        cpu.Irq = true;
        cpu.RunInstruction();                       // NOP at $1000: IRQ was already asserted during its first cycle
        bus.Clear();
        int cycles = cpu.RunInstruction();
        Assert.That(cycles, Is.EqualTo(7));
        bus.AssertTrace(
            "R 1001 EA",
            "R 1001 EA",
            "W 01FD 10",
            "W 01FC 01",
            "W 01FB 21",
            "R FFFE 00",
            "R FFFF 20");
        Assert.That(cpu.PC, Is.EqualTo(0x2000));
        Assert.That(cpu.S, Is.EqualTo(0xFA));
        Assert.That(cpu.P, Is.EqualTo(U | C | I), "I is set, nothing else changes");
    }

    [Test]
    public void Irq_PushesPWithBClearAndBit5Set()
    {
        var (cpu, bus) = Boot(0x10, NOP, NOP);      // externally set bit 4, bit 5 clear (neither has storage on the chip)
        cpu.Irq = true;
        cpu.RunInstruction();
        cpu.RunInstruction();
        Assert.That(bus.Ram[0x01FB], Is.EqualTo(0x20));
    }

    [Test]
    public void Nmi_SevenCycleSequence_UsesFFFA()
    {
        var (cpu, bus) = Boot(U | C, NOP, NOP);
        cpu.Nmi = true;
        cpu.RunInstruction();
        bus.Clear();
        int cycles = cpu.RunInstruction();
        Assert.That(cycles, Is.EqualTo(7));
        bus.AssertTrace(
            "R 1001 EA",
            "R 1001 EA",
            "W 01FD 10",
            "W 01FC 01",
            "W 01FB 21",
            "R FFFA 00",
            "R FFFB 30");
        Assert.That(cpu.PC, Is.EqualTo(0x3000));
        Assert.That(cpu.FlagInterrupt, Is.True);
    }

    [Test]
    public void Nmi_IsTakenEvenWhenIIsSet()
    {
        var (cpu, _) = Boot(U | I, NOP, NOP);
        cpu.Nmi = true;
        cpu.RunInstruction();
        cpu.RunInstruction();
        Assert.That(cpu.PC, Is.EqualTo(0x3000));
    }

    [Test]
    public void Brk_PushesPcPlus2AndPWithBSet_SetsI_LeavesDAlone()
    {
        var (cpu, bus) = Boot(U | Cpu6502.FlagD, BRK, 0x77, NOP);
        int cycles = cpu.RunInstruction();
        Assert.That(cycles, Is.EqualTo(7));
        bus.AssertTrace(
            "R 1000 00",
            "R 1001 77",
            "W 01FD 10",
            "W 01FC 02",
            "W 01FB 38",     // D | B | bit5
            "R FFFE 00",
            "R FFFF 20");
        Assert.That(cpu.PC, Is.EqualTo(0x2000));
        Assert.That(cpu.FlagInterrupt, Is.True);
        Assert.That(cpu.FlagDecimal, Is.True, "NMOS BRK does not clear D");
        Assert.That(cpu.P & Cpu6502.FlagB, Is.EqualTo(0), "B has no storage in P");
    }

    // -----------------------------------------------------------------------------------------------
    // Level / edge behaviour and priority
    // -----------------------------------------------------------------------------------------------

    [Test]
    public void Irq_IgnoredWhileIIsSet()
    {
        var (cpu, _) = Boot(U | I, NOP, NOP, NOP, NOP, NOP);
        cpu.Irq = true;
        cpu.RunInstructions(5);
        Assert.That(cpu.PC, Is.EqualTo(0x1005));
        Assert.That(cpu.S, Is.EqualTo(0xFD));
    }

    [Test]
    public void Irq_IsLevelSensitive_RetakenAfterRtiWhileStillAsserted()
    {
        var (cpu, _) = Boot(U, NOP, NOP);
        cpu.Irq = true;
        cpu.RunInstruction();       // NOP
        cpu.RunInstruction();       // IRQ -> $2000
        Assert.That(cpu.PC, Is.EqualTo(0x2000));
        cpu.RunInstruction();       // RTI -> $1001, I cleared again
        Assert.That(cpu.PC, Is.EqualTo(0x1001));
        cpu.RunInstruction();       // taken again immediately (RTI has no delay)
        Assert.That(cpu.PC, Is.EqualTo(0x2000));
    }

    [Test]
    public void Nmi_IsEdgeTriggered_HoldingTheLineDoesNotRetrigger()
    {
        var (cpu, _) = Boot(U, NOP, NOP, NOP, NOP, NOP, NOP);
        cpu.Nmi = true;
        cpu.RunInstruction();       // NOP
        cpu.RunInstruction();       // NMI
        Assert.That(cpu.PC, Is.EqualTo(0x3000));
        cpu.RunInstruction();       // RTI -> $1001
        Assert.That(cpu.PC, Is.EqualTo(0x1001));
        cpu.RunInstructions(4);     // line still high: no new NMI
        Assert.That(cpu.PC, Is.EqualTo(0x1005));
        Assert.That(cpu.S, Is.EqualTo(0xFD));
    }

    [Test]
    public void Nmi_SecondEdgeTriggersAgain()
    {
        var (cpu, _) = Boot(U, NOP, NOP, NOP, NOP, NOP, NOP);
        cpu.Nmi = true;
        cpu.RunInstruction();       // NOP
        cpu.RunInstruction();       // NMI
        cpu.RunInstruction();       // RTI -> $1001
        cpu.Nmi = false;
        cpu.RunInstruction();       // NOP at $1001, line low
        cpu.Nmi = true;             // new edge
        cpu.RunInstruction();       // NOP at $1002 (edge seen in its first cycle)
        Assert.That(cpu.PC, Is.EqualTo(0x1003));
        cpu.RunInstruction();       // NMI again
        Assert.That(cpu.PC, Is.EqualTo(0x3000));
    }

    [Test]
    public void Nmi_EdgeIsRemembered_EvenIfLineDropsBeforeItIsTaken()
    {
        var (cpu, bus) = Boot(U, 0xAD, 0x34, 0x12, NOP);   // LDA abs (4 cycles)
        cpu.Clock(1);
        cpu.Nmi = true;
        cpu.Clock(1);               // edge sampled here
        cpu.Nmi = false;            // short pulse
        cpu.Clock(2);               // LDA completes
        bus.Clear();
        cpu.RunInstruction();
        Assert.That(cpu.PC, Is.EqualTo(0x3000));
        Assert.That(bus.Trace[5].Address, Is.EqualTo(0xFFFA));
    }

    [Test]
    public void Nmi_HasPriorityOverIrq_IrqTakenAfterwards()
    {
        var (cpu, bus) = Boot(U, NOP, NOP);
        cpu.Irq = true;
        cpu.Nmi = true;
        cpu.RunInstruction();       // NOP
        bus.Clear();
        cpu.RunInstruction();       // NMI wins
        Assert.That(cpu.PC, Is.EqualTo(0x3000));
        Assert.That(bus.Trace[5].Address, Is.EqualTo(0xFFFA));
        Assert.That(bus.Ram[0x01FB], Is.EqualTo(0x20), "pushed P: B clear, I clear");
        cpu.RunInstruction();       // RTI at $3000 restores I = 0 -> pending IRQ is taken right away
        Assert.That(cpu.PC, Is.EqualTo(0x1001));
        bus.Clear();
        cpu.RunInstruction();
        Assert.That(cpu.PC, Is.EqualTo(0x2000));
        Assert.That(bus.Trace[5].Address, Is.EqualTo(0xFFFE));
    }

    // -----------------------------------------------------------------------------------------------
    // The one-instruction delays (64doc.txt: "the I flag is checked one cycle before the interrupt would be
    // taken", so an instruction that changes I in its last cycle does not affect the poll of that instruction).
    // -----------------------------------------------------------------------------------------------

    [Test]
    public void Cli_Delay_InstructionAfterCliRunsBeforeTheHandler()
    {
        var (cpu, bus) = Boot(U | I, CLI, NOP, NOP, NOP);
        cpu.Irq = true;
        cpu.RunInstruction();       // CLI
        Assert.That(cpu.FlagInterrupt, Is.False);
        cpu.RunInstruction();       // the NOP after CLI still executes
        Assert.That(cpu.PC, Is.EqualTo(0x1002));
        bus.Clear();
        cpu.RunInstruction();       // now the IRQ
        Assert.That(cpu.PC, Is.EqualTo(0x2000));
        bus.AssertTrace("R 1002 EA", "R 1002 EA", "W 01FD 10", "W 01FC 02", "W 01FB 20", "R FFFE 00", "R FFFF 20");
    }

    [Test]
    public void Sei_Delay_IrqAssertedBeforeSeiIsStillTakenAfterSei()
    {
        var (cpu, bus) = Boot(U, SEI, NOP, NOP);
        cpu.Irq = true;
        cpu.RunInstruction();       // SEI (I was clear during its first cycle, when the line was sampled)
        Assert.That(cpu.FlagInterrupt, Is.True);
        bus.Clear();
        cpu.RunInstruction();       // IRQ taken anyway
        Assert.That(cpu.PC, Is.EqualTo(0x2000));
        bus.AssertTrace("R 1001 EA", "R 1001 EA", "W 01FD 10", "W 01FC 01", "W 01FB 24", "R FFFE 00", "R FFFF 20");
        Assert.That(bus.Ram[0x01FB] & I, Is.EqualTo(I), "the pushed P already has I set");
    }

    [Test]
    public void Sei_IrqAssertedDuringSeiLastCycle_IsNotTaken()
    {
        var (cpu, _) = Boot(U, SEI, NOP, NOP, NOP);
        cpu.Clock(1);               // SEI opcode fetch, line still low
        cpu.Irq = true;
        cpu.Clock(1);               // SEI sets I; the poll uses the sample from cycle 1 (line low)
        cpu.RunInstructions(3);     // I is set now: nothing happens
        Assert.That(cpu.PC, Is.EqualTo(0x1004));
    }

    [Test]
    public void Plp_Delay_WhenClearingI()
    {
        var (cpu, bus) = Boot(U | I, PLP, NOP, NOP, NOP);
        cpu.S = 0xFC;
        cpu.Irq = true;
        bus.Ram[0x01FD] = U;        // I clear
        cpu.RunInstruction();       // PLP
        Assert.That(cpu.FlagInterrupt, Is.False);
        cpu.RunInstruction();       // one more instruction runs
        Assert.That(cpu.PC, Is.EqualTo(0x1002));
        cpu.RunInstruction();       // IRQ
        Assert.That(cpu.PC, Is.EqualTo(0x2000));
    }

    [Test]
    public void Plp_Delay_WhenSettingI_IrqStillTaken()
    {
        var (cpu, bus) = Boot(U, PLP, NOP, NOP, NOP);
        cpu.S = 0xFC;
        bus.Ram[0x01FD] = U | I;    // I set
        cpu.Irq = true;
        cpu.RunInstruction();       // PLP
        Assert.That(cpu.FlagInterrupt, Is.True);
        cpu.RunInstruction();       // IRQ taken regardless
        Assert.That(cpu.PC, Is.EqualTo(0x2000));
        Assert.That(bus.Ram[0x01FB], Is.EqualTo(U | I), "pushed P has I set");
    }

    [Test]
    public void Rti_HasNoDelay_PendingIrqTakenImmediately()
    {
        var (cpu, bus) = Boot(U | I, RTI);
        cpu.S = 0xFA;
        bus.Ram[0x01FB] = U;        // P with I clear
        bus.Ram[0x01FC] = 0x00;
        bus.Ram[0x01FD] = 0x15;     // return to $1500
        bus.Ram[0x1500] = NOP;
        cpu.Irq = true;
        cpu.RunInstruction();       // RTI
        Assert.That(cpu.PC, Is.EqualTo(0x1500));
        Assert.That(cpu.FlagInterrupt, Is.False);
        bus.Clear();
        cpu.RunInstruction();       // IRQ, no instruction in between
        bus.AssertTrace("R 1500 EA", "R 1500 EA", "W 01FD 15", "W 01FC 00", "W 01FB 20", "R FFFE 00", "R FFFF 20");
        Assert.That(cpu.PC, Is.EqualTo(0x2000));
    }

    // -----------------------------------------------------------------------------------------------
    // Where in an instruction the line is sampled
    // -----------------------------------------------------------------------------------------------

    [Test]
    public void Irq_AssertedInSecondToLastCycle_IsTakenAfterThisInstruction()
    {
        var (cpu, bus) = Boot(U, 0xAD, 0x34, 0x12, NOP, NOP);   // LDA abs = 4 cycles
        cpu.Clock(2);
        cpu.Irq = true;             // asserted during cycle 3
        cpu.Clock(2);
        Assert.That(cpu.AtInstructionBoundary, Is.True);
        bus.Clear();
        cpu.RunInstruction();
        Assert.That(cpu.PC, Is.EqualTo(0x2000));
        Assert.That(bus.Trace[0], Is.EqualTo(new BusAccess(0x1003, NOP, false)));
    }

    [Test]
    public void Irq_AssertedInLastCycle_IsTakenOnlyAfterTheNextInstruction()
    {
        var (cpu, bus) = Boot(U, 0xAD, 0x34, 0x12, NOP, NOP);
        cpu.Clock(3);
        cpu.Irq = true;             // asserted during cycle 4 (the last one)
        cpu.Clock(1);
        Assert.That(cpu.AtInstructionBoundary, Is.True);
        cpu.RunInstruction();       // NOP at $1003 executes first
        Assert.That(cpu.PC, Is.EqualTo(0x1004));
        bus.Clear();
        cpu.RunInstruction();
        Assert.That(cpu.PC, Is.EqualTo(0x2000));
        // pushed P = $22: the LDA loaded $00, so Z is set
        bus.AssertTrace("R 1004 EA", "R 1004 EA", "W 01FD 10", "W 01FC 04", "W 01FB 22", "R FFFE 00", "R FFFF 20");
    }

    [Test]
    public void TakenBranch_NoPageCross_IrqInSecondToLastCycleIsDelayedOneMoreInstruction()
    {
        // BNE +2 at $1000 (taken, same page) -> $1004: NOP, NOP.
        var (cpu, bus) = Boot(U, 0xD0, 0x02, 0x77, 0x77, NOP, NOP, NOP);
        cpu.Clock(1);               // opcode fetch
        cpu.Irq = true;             // asserted during cycle 2 (operand fetch)
        cpu.Clock(2);               // operand fetch + dummy read of next opcode; the branch is done
        Assert.That(cpu.AtInstructionBoundary, Is.True);
        Assert.That(cpu.PC, Is.EqualTo(0x1004));
        cpu.RunInstruction();       // NOP at $1004 runs first
        Assert.That(cpu.PC, Is.EqualTo(0x1005));
        bus.Clear();
        cpu.RunInstruction();
        Assert.That(cpu.PC, Is.EqualTo(0x2000));
        bus.AssertTrace("R 1005 EA", "R 1005 EA", "W 01FD 10", "W 01FC 05", "W 01FB 20", "R FFFE 00", "R FFFF 20");
    }

    [Test]
    public void TakenBranch_NoPageCross_IrqAssertedBeforeTheBranchIsTakenRightAfterIt()
    {
        var (cpu, bus) = Boot(U, 0xD0, 0x02, 0x77, 0x77, NOP, NOP, NOP);
        cpu.Irq = true;             // asserted during cycle 1
        cpu.Clock(3);
        Assert.That(cpu.PC, Is.EqualTo(0x1004));
        bus.Clear();
        cpu.RunInstruction();
        Assert.That(cpu.PC, Is.EqualTo(0x2000));
        bus.AssertTrace("R 1004 EA", "R 1004 EA", "W 01FD 10", "W 01FC 04", "W 01FB 20", "R FFFE 00", "R FFFF 20");
    }

    [Test]
    public void NotTakenBranch_UsesTheNormalPoll()
    {
        var (cpu, _) = Boot(U | Cpu6502.FlagZ, 0xD0, 0x02, NOP, NOP);   // BNE not taken (Z set)
        cpu.Clock(1);
        cpu.Irq = true;             // during cycle 2 = second-to-last of a 2-cycle... no: it IS the last cycle
        cpu.Clock(1);
        cpu.RunInstruction();       // NOP at $1002 first
        Assert.That(cpu.PC, Is.EqualTo(0x1003));
        cpu.RunInstruction();
        Assert.That(cpu.PC, Is.EqualTo(0x2000));
    }

    [Test]
    public void TakenBranch_PageCross_IrqInSecondToLastCycleIsTakenRightAfterTheBranch()
    {
        // BNE at $10F0 with +$20 -> $1112 (page cross, 4 cycles).
        var bus = new RecordingBus();
        bus.Ram[0x10F0] = 0xD0; bus.Ram[0x10F1] = 0x20;
        bus.Ram[0x1112] = NOP; bus.Ram[0x1113] = NOP;
        bus.Ram[0xFFFE] = 0x00; bus.Ram[0xFFFF] = 0x20;
        var cpu = bus.CreateCpu(0x10F0, U);
        cpu.Clock(2);
        cpu.Irq = true;             // during cycle 3
        cpu.Clock(2);
        Assert.That(cpu.AtInstructionBoundary, Is.True);
        Assert.That(cpu.PC, Is.EqualTo(0x1112));
        bus.Clear();
        cpu.RunInstruction();
        Assert.That(cpu.PC, Is.EqualTo(0x2000));
        Assert.That(bus.Trace[0], Is.EqualTo(new BusAccess(0x1112, NOP, false)));
    }

    // -----------------------------------------------------------------------------------------------
    // NMI hijacking BRK
    // -----------------------------------------------------------------------------------------------

    [Test]
    public void Nmi_DuringBrk_HijacksTheVector()
    {
        var (cpu, bus) = Boot(U, BRK, 0x77, NOP, NOP, NOP);
        cpu.Clock(2);               // opcode fetch + padding byte
        cpu.Nmi = true;             // asserted while the return address is being pushed
        cpu.Clock(5);
        Assert.That(cpu.AtInstructionBoundary, Is.True);
        bus.AssertTrace(
            "R 1000 00",
            "R 1001 77",
            "W 01FD 10",
            "W 01FC 02",
            "W 01FB 30",            // B is still set: it is a BRK
            "R FFFA 00",
            "R FFFB 30");
        Assert.That(cpu.PC, Is.EqualTo(0x3000));

        // The NMI was consumed by the hijack: after RTI no second NMI is serviced.
        cpu.RunInstruction();       // RTI -> $1002
        Assert.That(cpu.PC, Is.EqualTo(0x1002));
        cpu.RunInstructions(3);
        Assert.That(cpu.PC, Is.EqualTo(0x1005));
    }

    [Test]
    public void Nmi_AtTheVeryEndOfBrk_DoesNotHijack_RunsAfterTheNextInstruction()
    {
        var (cpu, bus) = Boot(U, BRK, 0x77);
        bus.Ram[0x2000] = NOP;
        bus.Ram[0x2001] = NOP;
        cpu.Clock(6);
        cpu.Nmi = true;             // during the last cycle (PCH fetch)
        cpu.Clock(1);
        bus.AssertTrace("R 1000 00", "R 1001 77", "W 01FD 10", "W 01FC 02", "W 01FB 30", "R FFFE 00", "R FFFF 20");
        Assert.That(cpu.PC, Is.EqualTo(0x2000));
        cpu.RunInstruction();       // first handler instruction runs
        Assert.That(cpu.PC, Is.EqualTo(0x2001));
        bus.Clear();
        cpu.RunInstruction();       // then the NMI
        Assert.That(cpu.PC, Is.EqualTo(0x3000));
        bus.AssertTrace("R 2001 EA", "R 2001 EA", "W 01FA 20", "W 01F9 01", "W 01F8 24", "R FFFA 00", "R FFFB 30");
    }

    [Test]
    public void Nmi_PendingBeforeBrk_HijacksToo()
    {
        // NMI edge in the last cycle of the instruction before BRK: too late for that instruction's poll,
        // but latched, so the BRK sequence sees it when it fetches the vector.
        var (cpu, bus) = Boot(U, NOP, BRK, 0x77);
        cpu.Clock(1);
        cpu.Nmi = true;
        cpu.Clock(1);               // NOP done, poll used the sample from cycle 1 (no NMI yet)
        bus.Clear();
        cpu.RunInstruction();
        Assert.That(bus.Trace[0], Is.EqualTo(new BusAccess(0x1001, BRK, false)), "BRK opcode fetched, not an NMI sequence");
        Assert.That(bus.Trace[5].Address, Is.EqualTo(0xFFFA), "but the NMI vector is used");
        Assert.That(bus.Ram[0x01FB], Is.EqualTo(0x30), "P pushed with B set");
        Assert.That(cpu.PC, Is.EqualTo(0x3000));
    }

    [Test]
    public void Nmi_DuringIrqSequence_HijacksTheVector()
    {
        var (cpu, bus) = Boot(U, NOP, NOP, NOP, NOP);
        cpu.Irq = true;
        cpu.RunInstruction();       // NOP
        cpu.Clock(2);               // IRQ sequence cycles 1-2
        cpu.Nmi = true;
        cpu.Clock(5);
        bus.Clear();
        Assert.That(cpu.PC, Is.EqualTo(0x3000), "NMI vector used by the IRQ sequence");
        Assert.That(bus.Ram[0x01FB], Is.EqualTo(0x20), "P pushed with B clear");
        cpu.Irq = false;
        cpu.RunInstruction();       // RTI at $3000
        cpu.RunInstructions(2);
        Assert.That(cpu.PC, Is.EqualTo(0x1003), "no second NMI");
    }

    // -----------------------------------------------------------------------------------------------
    // RDY interaction
    // -----------------------------------------------------------------------------------------------

    [Test]
    public void Irq_AssertedDuringRdyStall_IsRemembered()
    {
        var (cpu, bus) = Boot(U, NOP, NOP, NOP);
        cpu.Clock(1);               // NOP cycle 1
        cpu.Rdy = false;
        cpu.Clock(3);               // stalled before cycle 2
        cpu.Irq = true;
        cpu.Clock(2);               // still stalled, line sampled
        Assert.That(cpu.StallCycles, Is.EqualTo(5));
        cpu.Rdy = true;
        cpu.Clock(1);               // NOP cycle 2 -> poll sees the IRQ
        Assert.That(cpu.AtInstructionBoundary, Is.True);
        bus.Clear();
        cpu.RunInstruction();
        Assert.That(cpu.PC, Is.EqualTo(0x2000));
        Assert.That(bus.Trace[0], Is.EqualTo(new BusAccess(0x1001, NOP, false)));
    }

    [Test]
    public void Nmi_EdgeDuringRdyStall_IsRemembered()
    {
        var (cpu, _) = Boot(U, NOP, NOP, NOP);
        cpu.Rdy = false;
        cpu.Clock(2);               // opcode fetch stalls
        cpu.Nmi = true;
        cpu.Clock(2);
        cpu.Nmi = false;            // pulse ended while still stalled
        cpu.Clock(1);
        cpu.Rdy = true;
        cpu.RunInstruction();       // NOP
        Assert.That(cpu.PC, Is.EqualTo(0x1001));
        cpu.RunInstruction();       // NMI
        Assert.That(cpu.PC, Is.EqualTo(0x3000));
    }

    [Test]
    public void InterruptPending_ReflectsThePoll()
    {
        var (cpu, _) = Boot(U, NOP, NOP);
        Assert.That(cpu.InterruptPending, Is.False);
        cpu.Irq = true;
        cpu.Clock(1);
        Assert.That(cpu.InterruptPending, Is.False, "not polled until the instruction ends");
        cpu.Clock(1);
        Assert.That(cpu.InterruptPending, Is.True);
        cpu.Clock(1);
        Assert.That(cpu.InterruptPending, Is.False, "cleared once the sequence starts");
    }
}
