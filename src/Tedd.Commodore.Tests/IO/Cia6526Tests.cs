using System;
using System.Collections.Generic;
using NUnit.Framework;
using Tedd.Commodore.Emulator.IO;

namespace Tedd.Commodore.Tests.IO;

/// <summary>
/// Cycle-level tests for <see cref="Cia6526"/>.
///
/// Cycle convention used throughout (same as the machine loop): a register access happens first in a cycle, then
/// <see cref="Cia6526.Clock"/> is called. "Write in cycle N, read in cycle N+k" therefore means: Write, then k
/// Clock() calls, then Read. The expected values are those of the Lorenz model as implemented by VICE and Hoxs64.
/// </summary>
[TestFixture]
public class Cia6526Tests
{
    private static Cia6526 NewCia(bool model6526A = false)
    {
        var cia = new Cia6526("test") { Model6526A = model6526A };
        return cia;
    }

    private static void Clocks(Cia6526 cia, int n)
    {
        for (int i = 0; i < n; i++)
            cia.Clock();
    }

    /// <summary>Writes the latch and loads the counter (timer stopped: high byte write loads after two cycles).</summary>
    private static void LoadTimerA(Cia6526 cia, ushort value)
    {
        cia.Write(Cia6526.RegTaLo, (byte)value);
        cia.Write(Cia6526.RegTaHi, (byte)(value >> 8));
        Clocks(cia, 2);
        Assert.That(TimerA(cia), Is.EqualTo(value), "timer A not loaded");
    }

    private static void LoadTimerB(Cia6526 cia, ushort value)
    {
        cia.Write(Cia6526.RegTbLo, (byte)value);
        cia.Write(Cia6526.RegTbHi, (byte)(value >> 8));
        Clocks(cia, 2);
        Assert.That(TimerB(cia), Is.EqualTo(value), "timer B not loaded");
    }

    private static ushort TimerA(Cia6526 cia) => (ushort)(cia.Peek(Cia6526.RegTaLo) | (cia.Peek(Cia6526.RegTaHi) << 8));
    private static ushort TimerB(Cia6526 cia) => (ushort)(cia.Peek(Cia6526.RegTbLo) | (cia.Peek(Cia6526.RegTbHi) << 8));

    /// <summary>Clocks once and reads timer A, n times; returns the values seen in cycles N+1 .. N+n.</summary>
    private static ushort[] TraceTimerA(Cia6526 cia, int n)
    {
        var trace = new ushort[n];
        for (int i = 0; i < n; i++)
        {
            cia.Clock();
            trace[i] = (ushort)(cia.Read(Cia6526.RegTaLo) | (cia.Read(Cia6526.RegTaHi) << 8));
        }
        return trace;
    }

    // ------------------------------------------------------------------------------------------------ reset

    [Test]
    public void Reset_ClearsRegistersAndSetsLatchesToFFFF()
    {
        var cia = NewCia();
        cia.Write(Cia6526.RegPra, 0x55);
        cia.Write(Cia6526.RegDdra, 0xFF);
        cia.Write(Cia6526.RegIcr, 0x9F);
        cia.Reset();

        Assert.Multiple(() =>
        {
            Assert.That(cia.Peek(Cia6526.RegDdra), Is.EqualTo(0));
            Assert.That(cia.Peek(Cia6526.RegDdrb), Is.EqualTo(0));
            Assert.That(cia.PortAOutput, Is.EqualTo(0xFF), "inputs float high");
            Assert.That(cia.PortBOutput, Is.EqualTo(0xFF));
            Assert.That(cia.Peek(Cia6526.RegPra), Is.EqualTo(0xFF));
            Assert.That(TimerA(cia), Is.EqualTo(0xFFFF));
            Assert.That(TimerB(cia), Is.EqualTo(0xFFFF));
            Assert.That(cia.Peek(Cia6526.RegIcr), Is.EqualTo(0));
            Assert.That(cia.Peek(Cia6526.RegCra), Is.EqualTo(0));
            Assert.That(cia.Peek(Cia6526.RegCrb), Is.EqualTo(0));
            Assert.That(cia.Peek(Cia6526.RegTodHr), Is.EqualTo(0));
            Assert.That(cia.IrqLine, Is.False);
        });

        // Mask was cleared by reset: a FLAG interrupt must not raise IRQ.
        cia.Flag = true;
        cia.Flag = false;
        Clocks(cia, 3);
        Assert.That(cia.IrqLine, Is.False);
    }

    // ------------------------------------------------------------------------------------------------ timer start / stop

    [Test]
    public void TimerA_StartDelay_FirstDecrementVisibleThreeCyclesAfterWrite()
    {
        var cia = NewCia();
        LoadTimerA(cia, 0x0010);

        cia.Write(Cia6526.RegCra, 0x01);                    // cycle N
        var trace = TraceTimerA(cia, 4);                     // cycles N+1 .. N+4
        // Lorenz: START -> Count2 -> Count3 -> decrement; VICE ciat_table / Hoxs64 feed CountA2.
        Assert.That(trace, Is.EqualTo(new ushort[] { 0x0010, 0x0010, 0x000F, 0x000E }));
    }

    [Test]
    public void TimerA_Stop_PipelineDrains_TimerCountsExactlyTheCyclesItWasStarted()
    {
        var cia = NewCia();
        LoadTimerA(cia, 0x0010);

        cia.Write(Cia6526.RegCra, 0x01);                    // cycle N
        var running = TraceTimerA(cia, 5);                   // N+1 .. N+5
        Assert.That(running, Is.EqualTo(new ushort[] { 0x10, 0x10, 0x0F, 0x0E, 0x0D }));

        cia.Write(Cia6526.RegCra, 0x00);                    // cycle M = N+5: stop
        var stopping = TraceTimerA(cia, 4);                  // M+1 .. M+4
        // Count2/Count3 already in the pipeline still decrement (two more), then the timer is frozen.
        Assert.That(stopping, Is.EqualTo(new ushort[] { 0x0C, 0x0B, 0x0B, 0x0B }));
        Assert.That(0x10 - 0x0B, Is.EqualTo(5), "M - N decrements in total");
    }

    [Test]
    public void TimerA_Underflow_ReloadsFromLatch_LatchVisibleTwoCycles_PeriodIsLatchPlusOne()
    {
        var cia = NewCia();
        LoadTimerA(cia, 2);

        cia.Write(Cia6526.RegCra, 0x01);                    // cycle N
        var values = new List<ushort>();
        var flags = new List<bool>();
        for (int i = 0; i < 9; i++)
        {
            cia.Clock();
            values.Add(TimerA(cia));
            flags.Add((cia.Peek(Cia6526.RegIcr) & Cia6526.IcrTimerA) != 0);
            cia.Read(Cia6526.RegIcr);                        // acknowledge so each underflow is seen separately
        }
        // Lorenz: underflow when the counter is 0 and the next count pulse is in flight ("TA == 0 && Count2");
        // reload in that cycle and the pulse in flight is swallowed: 2 2 1 [2] 2 1 [2] 2 1 ...
        Assert.That(values, Is.EqualTo(new ushort[] { 2, 2, 1, 2, 2, 1, 2, 2, 1 }));
        Assert.That(flags, Is.EqualTo(new[] { false, false, false, true, false, false, true, false, false }));
    }

    [Test]
    public void TimerA_ContinuousMode_UnderflowsEveryLatchPlusOneCycles()
    {
        var cia = NewCia();
        LoadTimerA(cia, 9);
        cia.Write(Cia6526.RegCra, 0x01);

        var underflowCycles = new List<int>();
        for (int cycle = 1; cycle <= 40; cycle++)
        {
            cia.Clock();
            if ((cia.Read(Cia6526.RegIcr) & Cia6526.IcrTimerA) != 0)
                underflowCycles.Add(cycle);
        }
        Assert.That(underflowCycles, Is.EqualTo(new[] { 11, 21, 31 }));
    }

    [Test]
    public void TimerA_LatchZero_UnderflowsEveryCycle()
    {
        var cia = NewCia();
        LoadTimerA(cia, 0);
        cia.Write(Cia6526.RegCra, 0x01);
        Clocks(cia, 2);                                      // pipeline fill
        for (int i = 0; i < 5; i++)
        {
            Assert.That(cia.Read(Cia6526.RegIcr) & Cia6526.IcrTimerA, Is.EqualTo(Cia6526.IcrTimerA), $"cycle {i}");
            cia.Clock();
        }
    }

    [Test]
    public void TimerA_OneShot_StopsAfterUnderflow_ClearsStartBit()
    {
        var cia = NewCia();
        LoadTimerA(cia, 3);
        cia.Write(Cia6526.RegCra, 0x09);                    // start + one-shot
        Clocks(cia, 4);                                      // decrement at N+3 (2), N+4 (1)
        Assert.That(TimerA(cia), Is.EqualTo(1));
        Assert.That(cia.Peek(Cia6526.RegCra) & 1, Is.EqualTo(1), "still running before underflow");
        cia.Clock();                                         // N+5: 1 -> 0 and underflow in the same cycle
        Assert.Multiple(() =>
        {
            Assert.That(cia.Peek(Cia6526.RegIcr) & Cia6526.IcrTimerA, Is.EqualTo(Cia6526.IcrTimerA));
            Assert.That(TimerA(cia), Is.EqualTo(3), "reloaded");
            Assert.That(cia.Peek(Cia6526.RegCra) & 1, Is.EqualTo(0), "START cleared by one-shot");
        });
        cia.Read(Cia6526.RegIcr);
        Clocks(cia, 20);
        Assert.That(TimerA(cia), Is.EqualTo(3), "counter frozen");
        Assert.That(cia.Peek(Cia6526.RegIcr) & Cia6526.IcrTimerA, Is.EqualTo(0), "no further underflow");
    }

    [Test]
    public void TimerA_LatchWriteWhileRunning_TakesEffectAtNextReload()
    {
        var cia = NewCia();
        LoadTimerA(cia, 3);
        cia.Write(Cia6526.RegCra, 0x01);
        Clocks(cia, 3);                                      // counter 2
        Assert.That(TimerA(cia), Is.EqualTo(2));
        cia.Write(Cia6526.RegTaLo, 0x08);                    // latch only; counter keeps counting
        cia.Clock();
        Assert.That(TimerA(cia), Is.EqualTo(1));
        cia.Clock();                                         // 1 -> 0, underflow -> reload from the new latch
        Assert.That(TimerA(cia), Is.EqualTo(8));
        Assert.That(cia.Read(Cia6526.RegIcr) & Cia6526.IcrTimerA, Is.EqualTo(Cia6526.IcrTimerA));
    }

    // ------------------------------------------------------------------------------------------------ loads

    [Test]
    public void TimerA_HighByteWriteWhileStopped_LoadsCounterAfterTwoCycles()
    {
        var cia = NewCia();
        cia.Write(Cia6526.RegTaLo, 0x34);
        Assert.That(TimerA(cia), Is.EqualTo(0xFFFF), "low byte write alone does not load");
        cia.Write(Cia6526.RegTaHi, 0x12);                    // cycle N
        cia.Clock();
        Assert.That(TimerA(cia), Is.EqualTo(0xFFFF), "N+1: not yet (Load0)");
        cia.Clock();
        Assert.That(TimerA(cia), Is.EqualTo(0x1234), "N+2: loaded (Load1)");
    }

    [Test]
    public void TimerA_HighByteWriteWhileRunning_DoesNotLoad()
    {
        var cia = NewCia();
        LoadTimerA(cia, 0x0100);
        cia.Write(Cia6526.RegCra, 0x01);
        Clocks(cia, 5);
        Assert.That(TimerA(cia), Is.EqualTo(0x00FD));
        cia.Write(Cia6526.RegTaHi, 0x20);
        Clocks(cia, 3);
        Assert.That(TimerA(cia), Is.EqualTo(0x00FA), "still counting down from the old value");
    }

    [Test]
    public void TimerA_ForceLoad_LoadsAfterTwoCycles_SwallowsOneCount_ReadsBackAsZero()
    {
        var cia = NewCia();
        LoadTimerA(cia, 0x0100);
        cia.Write(Cia6526.RegCra, 0x01);
        Clocks(cia, 5);
        Assert.That(TimerA(cia), Is.EqualTo(0x00FD));

        cia.Write(Cia6526.RegTaLo, 0x20);
        cia.Write(Cia6526.RegTaHi, 0x00);                    // running: no load
        cia.Write(Cia6526.RegCra, 0x11);                    // cycle N: force load + start
        Assert.That(cia.Read(Cia6526.RegCra), Is.EqualTo(0x01), "bit 4 reads as 0");
        cia.Clock();
        Assert.That(TimerA(cia), Is.EqualTo(0x00FC), "N+1: still counting");
        cia.Clock();
        Assert.That(TimerA(cia), Is.EqualTo(0x0020), "N+2: loaded");
        cia.Clock();
        Assert.That(TimerA(cia), Is.EqualTo(0x0020), "N+3: the count pulse in flight was swallowed by the load");
        cia.Clock();
        Assert.That(TimerA(cia), Is.EqualTo(0x001F), "N+4: counting again");
    }

    // ------------------------------------------------------------------------------------------------ interrupts

    [Test]
    public void Irq_Old6526_AssertedOneCycleAfterFlag_IrBitFollowsIrq()
    {
        var cia = NewCia(model6526A: false);
        LoadTimerA(cia, 1);
        cia.Write(Cia6526.RegIcr, 0x81);
        cia.Write(Cia6526.RegCra, 0x01);                    // cycle N; underflow in Clock N+2 (latch 1: 1 -> 0)
        Clocks(cia, 3);
        Assert.Multiple(() =>
        {
            Assert.That(cia.Peek(Cia6526.RegIcr), Is.EqualTo(0x01), "flag set, IR not yet");
            Assert.That(cia.IrqLine, Is.False, "IRQ one cycle later on the old 6526");
        });
        cia.Clock();
        Assert.Multiple(() =>
        {
            Assert.That(cia.Peek(Cia6526.RegIcr), Is.EqualTo(0x81));
            Assert.That(cia.IrqLine, Is.True);
        });
    }

    [Test]
    public void Irq_6526A_AssertedInTheSameCycleAsFlag()
    {
        var cia = NewCia(model6526A: true);
        LoadTimerA(cia, 1);
        cia.Write(Cia6526.RegIcr, 0x81);
        cia.Write(Cia6526.RegCra, 0x01);
        Clocks(cia, 2);
        Assert.That(cia.IrqLine, Is.False);
        cia.Clock();                                         // N+2: underflow
        Assert.Multiple(() =>
        {
            Assert.That(cia.Peek(Cia6526.RegIcr), Is.EqualTo(0x81));
            Assert.That(cia.IrqLine, Is.True);
        });
        Assert.That(cia.Read(Cia6526.RegIcr), Is.EqualTo(0x81));
        Assert.That(cia.IrqLine, Is.False, "read releases IRQ immediately");
    }

    [Test]
    public void IcrRead_ReturnsFlagsWithIr_ClearsFlags_ReleasesIrq()
    {
        var cia = NewCia();
        cia.Write(Cia6526.RegIcr, 0x90);                    // enable FLAG
        cia.Flag = true;
        cia.Flag = false;                                    // negative edge
        cia.Clock();                                         // flag recognised
        Assert.That(cia.Peek(Cia6526.RegIcr), Is.EqualTo(0x10));
        cia.Clock();                                         // IRQ (old 6526)
        Assert.That(cia.IrqLine, Is.True);

        Assert.That(cia.Read(Cia6526.RegIcr), Is.EqualTo(0x90));
        Assert.Multiple(() =>
        {
            Assert.That(cia.IrqLine, Is.False, "released immediately");
            Assert.That(cia.Peek(Cia6526.RegIcr) & 0x1F, Is.EqualTo(0), "flags cleared at once");
        });
        // IR (bit 7) lingers for two cycles after the read (VICE "irqflags &= CIA_IM_SET", Hoxs64 "icr &= 0x80");
        // a 6502 cannot observe this because two ICR reads are at least four cycles apart.
        Clocks(cia, 2);
        Assert.That(cia.Peek(Cia6526.RegIcr), Is.EqualTo(0));
        Clocks(cia, 5);
        Assert.That(cia.Read(Cia6526.RegIcr), Is.EqualTo(0), "nothing pending any more");
    }

    [Test]
    public void IcrWrite_Bit7SetsMaskBits_Bit7ClearClearsThem()
    {
        var cia = NewCia();
        LoadTimerA(cia, 1);
        cia.Write(Cia6526.RegIcr, 0x83);                    // enable TA + TB
        cia.Write(Cia6526.RegIcr, 0x02);                    // disable TB (bit 7 clear): TA stays enabled
        cia.Write(Cia6526.RegIcr, 0x80);                    // no bits: no change
        cia.Write(Cia6526.RegCra, 0x01);
        Clocks(cia, 5);
        Assert.That(cia.IrqLine, Is.True, "TA still enabled");
        Assert.That(cia.Read(Cia6526.RegIcr), Is.EqualTo(0x81));

        cia.Write(Cia6526.RegIcr, 0x01);                    // disable TA
        Clocks(cia, 6);                                      // several more underflows
        Assert.Multiple(() =>
        {
            Assert.That(cia.Peek(Cia6526.RegIcr) & Cia6526.IcrTimerA, Is.EqualTo(Cia6526.IcrTimerA), "flag still recorded");
            Assert.That(cia.Peek(Cia6526.RegIcr) & 0x80, Is.EqualTo(0), "but not IR");
            Assert.That(cia.IrqLine, Is.False);
        });
    }

    [Test]
    public void IcrWrite_EnablingMaskOfPendingFlag_RaisesIrq()
    {
        // Old 6526: Interrupt0 -> Interrupt1 -> IRQ two cycles after the write.
        var cia = NewCia();
        cia.Flag = true;
        cia.Flag = false;
        cia.Clock();
        Assert.That(cia.Peek(Cia6526.RegIcr), Is.EqualTo(0x10));
        Clocks(cia, 3);
        Assert.That(cia.IrqLine, Is.False, "masked");

        cia.Write(Cia6526.RegIcr, 0x90);
        cia.Clock();
        Assert.That(cia.IrqLine, Is.False);
        cia.Clock();
        Assert.Multiple(() =>
        {
            Assert.That(cia.IrqLine, Is.True);
            Assert.That(cia.Peek(Cia6526.RegIcr), Is.EqualTo(0x90));
        });

        // 6526A: one cycle after the write.
        var a = NewCia(model6526A: true);
        a.Flag = true;
        a.Flag = false;
        Clocks(a, 3);
        a.Write(Cia6526.RegIcr, 0x90);
        Assert.That(a.IrqLine, Is.False);
        a.Clock();
        Assert.That(a.IrqLine, Is.True);
    }

    [Test]
    public void IcrRead_InTheFlagCycle_Old6526LosesTheInterrupt()
    {
        // Lorenz / VICE / Hoxs64: reading the ICR in the same cycle a flag is set (before IR/IRQ are raised one
        // cycle later) returns the flag without bit 7 and the IRQ is never asserted.
        var cia = NewCia();
        LoadTimerA(cia, 1);
        cia.Write(Cia6526.RegIcr, 0x81);
        cia.Write(Cia6526.RegCra, 0x09);                    // one-shot so there is exactly one underflow
        Clocks(cia, 3);                                      // flag cycle
        Assert.That(cia.Read(Cia6526.RegIcr), Is.EqualTo(0x01), "no IR yet");
        Clocks(cia, 3);
        Assert.Multiple(() =>
        {
            Assert.That(cia.IrqLine, Is.False, "interrupt lost");
            Assert.That(cia.Peek(Cia6526.RegIcr), Is.EqualTo(0));
        });
    }

    [Test]
    public void Flag_NegativeEdgeSetsIcrBit4_PositiveEdgeDoesNot()
    {
        var cia = NewCia();
        cia.Flag = false;
        cia.Flag = true;                                     // positive edge: nothing
        Clocks(cia, 2);
        Assert.That(cia.Peek(Cia6526.RegIcr), Is.EqualTo(0));
        cia.Flag = false;                                    // negative edge
        Assert.That(cia.Peek(Cia6526.RegIcr), Is.EqualTo(0), "recognised in the next cycle");
        cia.Clock();
        Assert.That(cia.Read(Cia6526.RegIcr), Is.EqualTo(0x10));
        cia.Flag = false;                                    // no edge
        Clocks(cia, 3);
        Assert.That(cia.Peek(Cia6526.RegIcr) & 0x1F, Is.EqualTo(0));
    }

    // ------------------------------------------------------------------------------------------------ timer B

    [Test]
    public void TimerB_CountsTimerAUnderflows()
    {
        var cia = NewCia();
        LoadTimerA(cia, 1);                                  // underflow every 2 cycles
        LoadTimerB(cia, 5);
        cia.Write(Cia6526.RegCrb, 0x41);                    // TB: start, count TA underflows
        cia.Write(Cia6526.RegCra, 0x01);                    // cycle N; TA underflows in Clock N+2, N+4, N+6, ...

        var tb = new List<ushort>();
        var tbFlag = new List<bool>();
        for (int i = 0; i < 15; i++)
        {
            cia.Clock();
            tb.Add(TimerB(cia));
            tbFlag.Add((cia.Peek(Cia6526.RegIcr) & Cia6526.IcrTimerB) != 0);
        }
        // Timer A underflow pulses enter timer B's pipeline at Count1 -> decrement two cycles later (N+4, N+6, ...).
        // With sparse pulses the 0 is visible; the underflow fires when the next pulse reaches Count2 (N+13).
        Assert.That(tb, Is.EqualTo(new ushort[] { 5, 5, 5, 5, 4, 4, 3, 3, 2, 2, 1, 1, 0, 5, 5 }));
        Assert.That(tbFlag.IndexOf(true), Is.EqualTo(13), "TB underflow after latch+1 = 6 TA underflows");
    }

    [Test]
    public void TimerB_CountsTimerAUnderflowsOnlyWhileCntHigh()
    {
        var cia = NewCia();
        LoadTimerA(cia, 1);
        LoadTimerB(cia, 0x40);
        cia.Cnt = false;
        cia.Write(Cia6526.RegCrb, 0x61);                    // mode 11
        cia.Write(Cia6526.RegCra, 0x01);                    // TA underflows in Clock 3, 5, 7, ...
        Clocks(cia, 12);
        Assert.That(TimerB(cia), Is.EqualTo(0x40), "CNT low: TA underflows ignored");
        cia.Cnt = true;
        Clocks(cia, 12);                                     // underflows 13..23 count, decrements in 15..25 -> five seen
        Assert.That(TimerB(cia), Is.EqualTo(0x3B), "CNT high: counting");
        cia.Cnt = false;
        Clocks(cia, 12);
        Assert.That(TimerB(cia), Is.EqualTo(0x3A), "one pulse still in the pipeline, then ignored again");
    }

    [Test]
    public void TimerB_Phi2Mode_SameTimingAsTimerA()
    {
        var cia = NewCia();
        LoadTimerB(cia, 0x0010);
        cia.Write(Cia6526.RegCrb, 0x01);
        var trace = new ushort[4];
        for (int i = 0; i < 4; i++)
        {
            cia.Clock();
            trace[i] = TimerB(cia);
        }
        Assert.That(trace, Is.EqualTo(new ushort[] { 0x10, 0x10, 0x0F, 0x0E }));
    }

    // ------------------------------------------------------------------------------------------------ CNT

    [Test]
    public void Cnt_PositiveEdgeCountsTimerA_InCntMode_DecrementFourCyclesLater()
    {
        var cia = NewCia();
        LoadTimerA(cia, 5);
        cia.Cnt = false;
        cia.Write(Cia6526.RegCra, 0x21);                    // start, count CNT
        Clocks(cia, 10);
        Assert.That(TimerA(cia), Is.EqualTo(5), "no edges: no counting");

        cia.Cnt = true;                                      // positive edge
        Clocks(cia, 3);
        Assert.That(TimerA(cia), Is.EqualTo(5), "Count0 -> Count1 -> Count2 -> Count3");
        cia.Clock();
        Assert.That(TimerA(cia), Is.EqualTo(4));
        Clocks(cia, 10);
        Assert.That(TimerA(cia), Is.EqualTo(4), "level stays high: no further pulses");

        cia.Cnt = false;
        cia.Cnt = true;
        Clocks(cia, 4);
        Assert.That(TimerA(cia), Is.EqualTo(3));
    }

    [Test]
    public void Cnt_EdgesIgnoredInPhi2Mode_AndCountTimerBInCntMode()
    {
        var cia = NewCia();
        LoadTimerA(cia, 5);
        LoadTimerB(cia, 5);
        cia.Write(Cia6526.RegCra, 0x00);                    // TA stopped, phi2 mode
        cia.Write(Cia6526.RegCrb, 0x21);                    // TB counts CNT
        for (int i = 0; i < 3; i++)
        {
            cia.Cnt = false;
            cia.Clock();
            cia.Cnt = true;
            cia.Clock();
        }
        Clocks(cia, 4);
        Assert.Multiple(() =>
        {
            Assert.That(TimerA(cia), Is.EqualTo(5), "timer A stopped / phi2 mode ignores CNT");
            Assert.That(TimerB(cia), Is.EqualTo(2), "timer B counted three edges");
        });
    }

    // ------------------------------------------------------------------------------------------------ PB6 / PB7

    [Test]
    public void Pb6_ToggleMode_SetOnStart_TogglesOnEachUnderflow()
    {
        var cia = NewCia();
        cia.Write(Cia6526.RegDdrb, 0x00);                   // PBON forces PB6 to output regardless of DDRB
        LoadTimerA(cia, 1);
        Assert.That(cia.PortBOutput & 0x40, Is.EqualTo(0x40), "input floats high before PBON");
        cia.Write(Cia6526.RegPrb, 0x00);
        cia.Write(Cia6526.RegDdrb, 0xFF);
        Assert.That(cia.Read(Cia6526.RegPrb) & 0x40, Is.EqualTo(0));

        cia.Write(Cia6526.RegCra, 0x07);                    // start, PBON, toggle: flip-flop set by start
        Assert.That(cia.Read(Cia6526.RegPrb) & 0x40, Is.EqualTo(0x40), "toggle flip-flop is 1 after start");
        var levels = new List<int>();
        for (int i = 0; i < 6; i++)
        {
            cia.Clock();
            levels.Add(cia.Read(Cia6526.RegPrb) & 0x40);
        }
        // underflows in Clock N+2 and N+4 toggle the output
        Assert.That(levels, Is.EqualTo(new[] { 0x40, 0x40, 0x00, 0x00, 0x40, 0x40 }));
    }

    [Test]
    public void Pb6_PulseMode_HighForExactlyOneCycleAtUnderflow()
    {
        var cia = NewCia();
        cia.Write(Cia6526.RegPrb, 0x00);
        cia.Write(Cia6526.RegDdrb, 0xFF);
        LoadTimerA(cia, 3);
        cia.Write(Cia6526.RegCra, 0x03);                    // start, PBON, pulse
        Assert.That(cia.Read(Cia6526.RegPrb) & 0x40, Is.EqualTo(0));
        var levels = new List<int>();
        for (int i = 0; i < 10; i++)
        {
            cia.Clock();
            levels.Add(cia.Read(Cia6526.RegPrb) & 0x40);
        }
        // underflows in Clock N+4 and N+8 -> high visible in N+5 and N+9 only
        Assert.That(levels, Is.EqualTo(new[] { 0, 0, 0, 0, 0x40, 0, 0, 0, 0x40, 0 }));
    }

    [Test]
    public void Pb7_TimerBOutput_ToggleAndPulse_FirePortBChanged()
    {
        var cia = NewCia();
        cia.Write(Cia6526.RegPrb, 0x00);
        cia.Write(Cia6526.RegDdrb, 0xFF);
        LoadTimerB(cia, 1);
        int changed = 0;
        cia.PortBChanged += () => changed++;

        cia.Write(Cia6526.RegCrb, 0x07);                    // toggle
        Assert.That(cia.PortBOutput & 0x80, Is.EqualTo(0x80));
        Assert.That(changed, Is.EqualTo(1));
        Clocks(cia, 3);                                      // underflow in N+2 toggles
        Assert.That(cia.PortBOutput & 0x80, Is.EqualTo(0));
        Assert.That(changed, Is.EqualTo(2));

        cia.Write(Cia6526.RegCrb, 0x00);
        Clocks(cia, 4);
        LoadTimerB(cia, 1);
        cia.Write(Cia6526.RegCrb, 0x03);                    // pulse
        Assert.That(cia.PortBOutput & 0x80, Is.EqualTo(0));
        Clocks(cia, 3);
        Assert.That(cia.PortBOutput & 0x80, Is.EqualTo(0x80), "pulse high in the underflow cycle");
        cia.Clock();
        Assert.That(cia.PortBOutput & 0x80, Is.EqualTo(0), "and low again the cycle after");
    }

    // ------------------------------------------------------------------------------------------------ ports

    [Test]
    public void PortRead_IsOutputAndInput_InputsFloatHigh()
    {
        var cia = NewCia();
        cia.Write(Cia6526.RegDdra, 0xF0);
        cia.Write(Cia6526.RegPra, 0xA5);
        Assert.That(cia.PortAOutput, Is.EqualTo(0xAF), "PRA | ~DDRA");
        Assert.That(cia.Read(Cia6526.RegPra), Is.EqualTo(0xAF), "no external driver: pins float high");
        cia.PortAInput = () => 0x3C;
        Assert.That(cia.Read(Cia6526.RegPra), Is.EqualTo(0xAF & 0x3C));

        cia.Write(Cia6526.RegDdrb, 0x0F);
        cia.Write(Cia6526.RegPrb, 0x5A);
        cia.PortBInput = () => 0xC3;
        Assert.That(cia.PortBOutput, Is.EqualTo(0xFA));
        Assert.That(cia.Read(Cia6526.RegPrb), Is.EqualTo(0xFA & 0xC3));
        Assert.That(cia.Read(Cia6526.RegDdra), Is.EqualTo(0xF0));
        Assert.That(cia.Read(Cia6526.RegDdrb), Is.EqualTo(0x0F));
    }

    [Test]
    public void DdrChange_UpdatesOutputAndRaisesPortChangedOnlyWhenTheOutputChanges()
    {
        var cia = NewCia();
        int a = 0, b = 0;
        cia.PortAChanged += () => a++;
        cia.PortBChanged += () => b++;

        cia.Write(Cia6526.RegPra, 0x00);                    // all inputs: output still 0xFF
        Assert.That(cia.PortAOutput, Is.EqualTo(0xFF));
        Assert.That(a, Is.EqualTo(0), "no observable change");
        cia.Write(Cia6526.RegDdra, 0x0F);
        Assert.That(cia.PortAOutput, Is.EqualTo(0xF0));
        Assert.That(a, Is.EqualTo(1));
        cia.Write(Cia6526.RegDdra, 0x0F);
        Assert.That(a, Is.EqualTo(1), "same DDR: no event");
        cia.Write(Cia6526.RegPra, 0x03);
        Assert.That(cia.PortAOutput, Is.EqualTo(0xF3));
        Assert.That(a, Is.EqualTo(2));

        cia.Write(Cia6526.RegDdrb, 0xFF);
        Assert.That(cia.PortBOutput, Is.EqualTo(0x00));
        Assert.That(b, Is.EqualTo(1));
        cia.Write(Cia6526.RegPrb, 0x81);
        Assert.That(cia.PortBOutput, Is.EqualTo(0x81));
        Assert.That(b, Is.EqualTo(2));
    }

    // ------------------------------------------------------------------------------------------------ TOD

    private static void SetTod(Cia6526 cia, byte hr, byte min, byte sec, byte tenths)
    {
        cia.Write(Cia6526.RegTodHr, hr);                    // halts
        cia.Write(Cia6526.RegTodMin, min);
        cia.Write(Cia6526.RegTodSec, sec);
        cia.Write(Cia6526.RegTod10ths, tenths);             // restarts
    }

    private static (byte hr, byte min, byte sec, byte tenths) ReadTod(Cia6526 cia)
        => (cia.Read(Cia6526.RegTodHr), cia.Read(Cia6526.RegTodMin), cia.Read(Cia6526.RegTodSec), cia.Read(Cia6526.RegTod10ths));

    [Test]
    public void Tod_Ticks_DivideBy6At60Hz_DivideBy5At50Hz()
    {
        var cia = NewCia();
        for (int i = 0; i < 5; i++) cia.TodTick();
        Assert.That(cia.Peek(Cia6526.RegTod10ths), Is.EqualTo(0));
        cia.TodTick();
        Assert.That(cia.Peek(Cia6526.RegTod10ths), Is.EqualTo(1), "60 Hz: 6 pulses per tenth");

        var pal = NewCia();
        pal.Write(Cia6526.RegCra, 0x80);                    // 50 Hz
        for (int i = 0; i < 4; i++) pal.TodTick();
        Assert.That(pal.Peek(Cia6526.RegTod10ths), Is.EqualTo(0));
        pal.TodTick();
        Assert.That(pal.Peek(Cia6526.RegTod10ths), Is.EqualTo(1), "50 Hz: 5 pulses per tenth");
        for (int i = 0; i < 45; i++) pal.TodTick();
        Assert.That(ReadTod(pal), Is.EqualTo(((byte)0, (byte)0, (byte)1, (byte)0)), "1.0 s after 50 pulses");
    }

    [Test]
    public void Tod_BcdRollover_TenthsSecondsMinutes()
    {
        var cia = NewCia();
        SetTod(cia, 0x01, 0x59, 0x59, 0x09);
        for (int i = 0; i < 6; i++) cia.TodTick();
        Assert.That(ReadTod(cia), Is.EqualTo(((byte)0x02, (byte)0x00, (byte)0x00, (byte)0x00)));

        SetTod(cia, 0x02, 0x08, 0x09, 0x09);
        for (int i = 0; i < 6; i++) cia.TodTick();
        Assert.That(ReadTod(cia), Is.EqualTo(((byte)0x02, (byte)0x08, (byte)0x10, (byte)0x00)), "BCD 09 -> 10");
        for (int i = 0; i < 6 * 10 * 50; i++) cia.TodTick();
        Assert.That(ReadTod(cia), Is.EqualTo(((byte)0x02, (byte)0x09, (byte)0x00, (byte)0x00)), "59 s -> next minute");
    }

    [Test]
    public void Tod_AmPm_11To12FlipsAmPm_12To1KeepsIt()
    {
        var cia = NewCia();
        SetTod(cia, 0x11, 0x59, 0x59, 0x09);                // 11:59:59.9 AM
        for (int i = 0; i < 6; i++) cia.TodTick();
        Assert.That(ReadTod(cia), Is.EqualTo(((byte)0x92, (byte)0x00, (byte)0x00, (byte)0x00)), "12:00:00.0 PM");

        SetTod(cia, 0x12, 0x59, 0x59, 0x09);                // writing hour 12 inverts AM/PM: this is 12:59:59.9 PM
        Assert.That(cia.Peek(Cia6526.RegTodHr), Is.EqualTo(0x92));
        for (int i = 0; i < 6; i++) cia.TodTick();
        Assert.That(ReadTod(cia), Is.EqualTo(((byte)0x81, (byte)0x00, (byte)0x00, (byte)0x00)), "01:00:00.0 PM");

        SetTod(cia, 0x89, 0x59, 0x59, 0x09);                // 9:59:59.9 PM
        for (int i = 0; i < 6; i++) cia.TodTick();
        Assert.That(cia.Peek(Cia6526.RegTodHr), Is.EqualTo(0x90), "9 -> 10 PM");

        SetTod(cia, 0x91, 0x59, 0x59, 0x09);                // 11:59:59.9 PM
        for (int i = 0; i < 6; i++) cia.TodTick();
        Assert.That(cia.Peek(Cia6526.RegTodHr), Is.EqualTo(0x12), "12:00 AM");
    }

    [Test]
    public void Tod_WriteHour12_InvertsAmPmBit_OtherHoursUnchanged()
    {
        var cia = NewCia();
        cia.Write(Cia6526.RegTodHr, 0x12);
        Assert.That(cia.Peek(Cia6526.RegTodHr), Is.EqualTo(0x92));
        cia.Write(Cia6526.RegTodHr, 0x92);
        Assert.That(cia.Peek(Cia6526.RegTodHr), Is.EqualTo(0x12));
        cia.Write(Cia6526.RegTodHr, 0x11);
        Assert.That(cia.Peek(Cia6526.RegTodHr), Is.EqualTo(0x11));
        cia.Write(Cia6526.RegTodHr, 0x81);
        Assert.That(cia.Peek(Cia6526.RegTodHr), Is.EqualTo(0x81));
    }

    [Test]
    public void Tod_WriteHoursHalts_WriteTenthsRestarts()
    {
        var cia = NewCia();
        cia.Write(Cia6526.RegTodHr, 0x03);
        for (int i = 0; i < 60; i++) cia.TodTick();
        Assert.That(ReadTod(cia), Is.EqualTo(((byte)0x03, (byte)0x00, (byte)0x00, (byte)0x00)), "halted");
        cia.Write(Cia6526.RegTodSec, 0x30);
        for (int i = 0; i < 60; i++) cia.TodTick();
        Assert.That(cia.Read(Cia6526.RegTodSec), Is.EqualTo(0x30), "still halted");
        cia.Write(Cia6526.RegTod10ths, 0x05);
        for (int i = 0; i < 6; i++) cia.TodTick();
        Assert.That(ReadTod(cia), Is.EqualTo(((byte)0x03, (byte)0x00, (byte)0x30, (byte)0x06)), "running again");
    }

    [Test]
    public void Tod_ReadHoursLatches_ReadTenthsUnlatches()
    {
        var cia = NewCia();
        SetTod(cia, 0x01, 0x02, 0x03, 0x04);
        Assert.That(cia.Read(Cia6526.RegTodHr), Is.EqualTo(0x01));   // latch
        for (int i = 0; i < 6 * 15; i++) cia.TodTick();               // clock runs on: 01:02:04.9
        Assert.Multiple(() =>
        {
            Assert.That(cia.Read(Cia6526.RegTodMin), Is.EqualTo(0x02));
            Assert.That(cia.Read(Cia6526.RegTodSec), Is.EqualTo(0x03), "latched");
            Assert.That(cia.Read(Cia6526.RegTodHr), Is.EqualTo(0x01));
            Assert.That(cia.Peek(Cia6526.RegTod10ths), Is.EqualTo(0x04), "latched tenths (peek keeps the latch)");
        });
        Assert.That(cia.Read(Cia6526.RegTod10ths), Is.EqualTo(0x04), "latched tenths, releases the latch");
        Assert.That(cia.Read(Cia6526.RegTodSec), Is.EqualTo(0x04), "live again");
        Assert.That(cia.Read(Cia6526.RegTod10ths), Is.EqualTo(0x09));
    }

    [Test]
    public void Tod_Alarm_MatchSetsIcrBit2_WritesRoutedByCrbBit7()
    {
        var cia = NewCia();
        cia.Write(Cia6526.RegIcr, 0x84);
        cia.Write(Cia6526.RegCrb, 0x80);                    // alarm select
        cia.Write(Cia6526.RegTodHr, 0x00);
        cia.Write(Cia6526.RegTodMin, 0x00);
        cia.Write(Cia6526.RegTodSec, 0x00);
        cia.Write(Cia6526.RegTod10ths, 0x03);               // alarm 00:00:00.3
        cia.Write(Cia6526.RegCrb, 0x00);
        Assert.That(cia.Peek(Cia6526.RegTod10ths), Is.EqualTo(0), "alarm write does not touch the clock");

        for (int i = 0; i < 12; i++) cia.TodTick();          // 0.2 s
        cia.Clock();
        Assert.That(cia.Peek(Cia6526.RegIcr), Is.EqualTo(0));
        for (int i = 0; i < 6; i++) cia.TodTick();           // 0.3 s: match
        Assert.That(cia.Peek(Cia6526.RegIcr), Is.EqualTo(0), "flag reaches the ICR in the next cycle");
        cia.Clock();
        Assert.That(cia.Peek(Cia6526.RegIcr), Is.EqualTo(0x04));
        cia.Clock();
        Assert.Multiple(() =>
        {
            Assert.That(cia.IrqLine, Is.True);
            Assert.That(cia.Read(Cia6526.RegIcr), Is.EqualTo(0x84));
        });
        for (int i = 0; i < 6; i++) cia.TodTick();           // 0.4 s: no longer matching
        Clocks(cia, 3);
        Assert.That(cia.Peek(Cia6526.RegIcr) & 0x04, Is.EqualTo(0), "alarm is edge triggered");
    }

    [Test]
    public void Tod_UnusedBitsReadZero()
    {
        var cia = NewCia();
        cia.Write(Cia6526.RegTodHr, 0xFF);
        cia.Write(Cia6526.RegTodMin, 0xFF);
        cia.Write(Cia6526.RegTodSec, 0xFF);
        cia.Write(Cia6526.RegTod10ths, 0xFF);
        Assert.That(ReadTod(cia), Is.EqualTo(((byte)0x9F, (byte)0x7F, (byte)0x7F, (byte)0x0F)));
    }

    // ------------------------------------------------------------------------------------------------ SDR

    [Test]
    public void Sdr_OutputMode_ByteCompleteAfter16TimerAUnderflows()
    {
        var cia = NewCia();
        LoadTimerA(cia, 0);                                  // underflow every cycle once started
        cia.Write(Cia6526.RegIcr, 0x88);
        cia.Write(Cia6526.RegCra, 0x41);                    // start, serial output; underflows from Clock N+1 on
        cia.Write(Cia6526.RegSdr, 0xA5);
        Assert.That(cia.Read(Cia6526.RegSdr), Is.EqualTo(0xA5));
        Clocks(cia, 16);                                     // 15 underflows
        Assert.That(cia.Peek(Cia6526.RegIcr) & Cia6526.IcrSerial, Is.EqualTo(0));
        cia.Clock();                                         // 16th underflow
        Assert.That(cia.Peek(Cia6526.RegIcr) & Cia6526.IcrSerial, Is.EqualTo(Cia6526.IcrSerial));
        cia.Clock();
        Assert.That(cia.IrqLine, Is.True);
        cia.Read(Cia6526.RegIcr);
        Clocks(cia, 40);
        Assert.That(cia.Peek(Cia6526.RegIcr) & Cia6526.IcrSerial, Is.EqualTo(0), "no byte pending: no more serial interrupts");
    }

    [Test]
    public void Sdr_InputMode_EightCntEdgesShiftInAByte()
    {
        var cia = NewCia();
        cia.Write(Cia6526.RegCra, 0x00);                    // serial input
        byte value = 0xC5;
        for (int bit = 7; bit >= 0; bit--)
        {
            cia.Sp = ((value >> bit) & 1) != 0;
            cia.Cnt = false;
            cia.Clock();
            cia.Cnt = true;
            cia.Clock();
        }
        Assert.That(cia.Peek(Cia6526.RegIcr) & Cia6526.IcrSerial, Is.EqualTo(Cia6526.IcrSerial));
        Assert.That(cia.Read(Cia6526.RegSdr), Is.EqualTo(0xC5));
    }

    // ------------------------------------------------------------------------------------------------ peek

    [Test]
    public void Peek_HasNoSideEffects()
    {
        var cia = NewCia();
        cia.Write(Cia6526.RegIcr, 0x90);
        cia.Flag = true;
        cia.Flag = false;
        Clocks(cia, 2);
        Assert.That(cia.IrqLine, Is.True);
        Assert.That(cia.Peek(Cia6526.RegIcr), Is.EqualTo(0x90));
        Assert.Multiple(() =>
        {
            Assert.That(cia.Peek(Cia6526.RegIcr), Is.EqualTo(0x90), "still pending after peek");
            Assert.That(cia.IrqLine, Is.True, "IRQ not released by peek");
        });
        Assert.That(cia.Read(Cia6526.RegIcr), Is.EqualTo(0x90));

        SetTod(cia, 0x01, 0x00, 0x00, 0x00);
        Assert.That(cia.Peek(Cia6526.RegTodHr), Is.EqualTo(0x01));    // must not latch
        for (int i = 0; i < 6; i++) cia.TodTick();
        Assert.That(cia.Peek(Cia6526.RegTod10ths), Is.EqualTo(0x01), "clock visible, not latched");
        Assert.That(cia.Read(Cia6526.RegTodHr), Is.EqualTo(0x01));    // latch
        for (int i = 0; i < 6; i++) cia.TodTick();
        Assert.That(cia.Peek(Cia6526.RegTod10ths), Is.EqualTo(0x01), "latched value");
        Assert.That(cia.Peek(Cia6526.RegTod10ths), Is.EqualTo(0x01), "peek did not release the latch");
        Assert.That(cia.Read(Cia6526.RegTod10ths), Is.EqualTo(0x01));
        Assert.That(cia.Read(Cia6526.RegTod10ths), Is.EqualTo(0x02), "read released the latch");

        LoadTimerA(cia, 7);
        cia.Write(Cia6526.RegCra, 0x01);
        Clocks(cia, 4);
        Assert.That(cia.Peek(Cia6526.RegTaLo), Is.EqualTo(cia.Read(Cia6526.RegTaLo)));
    }
}
