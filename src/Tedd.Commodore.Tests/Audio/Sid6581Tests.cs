using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Tedd.Commodore.Emulator.Audio;

namespace Tedd.Commodore.Tests.Audio;

/// <summary>
/// Tests for the 6581 model. Cycle counts are derived from the reSID 0.16 semantics: the envelope rate counter
/// starts at 0 after reset and steps the envelope every <c>period</c> cycles, so attack rate 0 (period 9) reaches
/// $FF exactly 9 * 255 = 2295 cycles after the gate is set, and the exponential counter changes its period at
/// $5D, $36, $1A, $0E, $06 and $00.
/// </summary>
[TestFixture]
public class Sid6581Tests
{
    private const int Voice1 = 0;
    private const int Voice2 = 7;
    private const int Voice3 = 14;
    private const double Pal = Sid6581.PalClockFrequency;

    private static void SetFreq(Sid6581 sid, int voice, int freq)
    {
        sid.Write(voice + Sid6581.RegFreqLo, (byte)freq);
        sid.Write(voice + Sid6581.RegFreqHi, (byte)(freq >> 8));
    }

    private static void SetPw(Sid6581 sid, int voice, int pw)
    {
        sid.Write(voice + Sid6581.RegPwLo, (byte)pw);
        sid.Write(voice + Sid6581.RegPwHi, (byte)(pw >> 8));
    }

    private static void Run(Sid6581 sid, int cycles)
    {
        for (int i = 0; i < cycles; i++)
            sid.Clock();
    }

    /// <summary>Clocks until exactly <paramref name="cycle"/> cycles have run since the counter was started.</summary>
    private static void RunTo(Sid6581 sid, ref int elapsed, int cycle)
    {
        while (elapsed < cycle)
        {
            sid.Clock();
            elapsed++;
        }
    }

    private static byte Osc3(Sid6581 sid) => sid.Read(Sid6581.RegOsc3);
    private static byte Env3(Sid6581 sid) => sid.Read(Sid6581.RegEnv3);

    private static string TestResultsDir()
    {
        for (var d = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); d != null; d = d.Parent)
        {
            if (File.Exists(Path.Combine(d.FullName, "Tedd.Commodore.Tests.csproj")))
                return Path.Combine(d.FullName, "TestResults");
        }
        return Path.Combine(TestContext.CurrentContext.WorkDirectory, "TestResults");
    }

    // ------------------------------------------------------------------------------------------------
    // Oscillators
    // ------------------------------------------------------------------------------------------------

    [Test]
    public void Sawtooth_TracksUpperBitsOfAccumulator()
    {
        var sid = new Sid6581();
        SetFreq(sid, Voice3, 0x1000);
        sid.Write(Voice3 + Sid6581.RegControl, Sid6581.ControlSawtooth);

        for (int k = 1; k <= 8192; k++)
        {
            sid.Clock();
            int expected = ((0x1000 * k) & 0xFFFFFF) >> 16;
            Assert.That(Osc3(sid), Is.EqualTo(expected), $"cycle {k}");
        }
    }

    [Test]
    public void Sawtooth_PeriodIs2Pow24DividedByFreq()
    {
        var sid = new Sid6581();
        SetFreq(sid, Voice3, 0x0400);
        sid.Write(Voice3 + Sid6581.RegControl, Sid6581.ControlSawtooth);

        var wraps = new List<int>();
        int prev = 0;
        for (int k = 1; k <= 4 * 16384; k++)
        {
            sid.Clock();
            int v = Osc3(sid);
            if (v < prev)
                wraps.Add(k);
            prev = v;
        }

        Assert.That(wraps, Is.EqualTo(new[] { 16384, 32768, 49152, 65536 }));
    }

    [Test]
    public void Sawtooth_FrequencyInHzFollowsDataSheetFormula()
    {
        // f = FREQ * clock / 2^24 (SID data sheet); FREQ 7382 is 433.5 Hz on PAL -> 433 complete cycles in 1 s.
        var sid = new Sid6581();
        SetFreq(sid, Voice3, 7382);
        sid.Write(Voice3 + Sid6581.RegControl, Sid6581.ControlSawtooth);

        int wraps = 0, prev = 0;
        for (int k = 0; k < (int)Pal; k++)
        {
            sid.Clock();
            int v = Osc3(sid);
            if (v < prev) wraps++;
            prev = v;
        }

        Assert.That(wraps, Is.EqualTo(433).Within(1));
    }

    [TestCase(0x800, 2048)]
    [TestCase(0x400, 3072)]
    [TestCase(0xC00, 1024)]
    [TestCase(0x000, 4096)]
    public void Pulse_DutyCycleFollowsPulseWidth(int pw, int expectedHighCycles)
    {
        var sid = new Sid6581();
        SetFreq(sid, Voice3, 0x1000);
        SetPw(sid, Voice3, pw);
        sid.Write(Voice3 + Sid6581.RegControl, Sid6581.ControlPulse);

        int high = 0;
        for (int k = 1; k <= 4096; k++)
        {
            sid.Clock();
            int v = Osc3(sid);
            Assert.That(v, Is.EqualTo(0xFF).Or.EqualTo(0x00));
            if (v == 0xFF) high++;
        }

        Assert.That(high, Is.EqualTo(expectedHighCycles));
    }

    [Test]
    public void Triangle_RisesThenFalls()
    {
        var sid = new Sid6581();
        SetFreq(sid, Voice3, 0x1000);
        sid.Write(Voice3 + Sid6581.RegControl, Sid6581.ControlTriangle);

        int prev = 0;
        for (int k = 1; k <= 2048; k++)
        {
            sid.Clock();
            int v = Osc3(sid);
            Assert.That(v, Is.GreaterThanOrEqualTo(prev), $"rising half, cycle {k}");
            prev = v;
        }
        Assert.That(prev, Is.EqualTo(0xFF));

        for (int k = 2049; k <= 4096; k++)
        {
            sid.Clock();
            int v = Osc3(sid);
            Assert.That(v, Is.LessThanOrEqualTo(prev), $"falling half, cycle {k}");
            prev = v;
        }
        Assert.That(prev, Is.EqualTo(0));
    }

    [Test]
    public void Noise_ShiftRegisterIsClockedOnAccumulatorBit19RisingEdge()
    {
        // FREQ $8000: the accumulator gains $8000 per cycle, so bit 19 ($80000) goes high at cycles 16, 48, 80...
        var sid = new Sid6581();
        SetFreq(sid, Voice3, 0x8000);
        sid.Write(Voice3 + Sid6581.RegControl, Sid6581.ControlNoise);

        Assert.That(sid.PeekNoiseShiftRegister(2), Is.EqualTo(0x7FFFF8));
        Assert.That(Osc3(sid), Is.EqualTo(0xFC), "bits 20,18,14,11,9,5 of $7FFFF8 are 1, bits 2 and 0 are 0");

        Run(sid, 15);
        Assert.That(sid.PeekNoiseShiftRegister(2), Is.EqualTo(0x7FFFF8));
        sid.Clock(); // cycle 16
        Assert.That(sid.PeekNoiseShiftRegister(2), Is.EqualTo(0x7FFFF0), "feedback bit 22 xor bit 17 = 0");
        Run(sid, 31); // cycle 47
        Assert.That(sid.PeekNoiseShiftRegister(2), Is.EqualTo(0x7FFFF0));
        sid.Clock(); // cycle 48
        Assert.That(sid.PeekNoiseShiftRegister(2), Is.EqualTo(0x7FFFE0));
    }

    [Test]
    public void Noise_ChangesAndIsDeterministicAfterTestBitReset()
    {
        static List<byte> Record(Sid6581 sid, int cycles)
        {
            var list = new List<byte>(cycles);
            for (int i = 0; i < cycles; i++)
            {
                sid.Clock();
                list.Add(Osc3(sid));
            }
            return list;
        }

        var a = new Sid6581();
        SetFreq(a, Voice3, 0xFFFF);
        a.Write(Voice3 + Sid6581.RegControl, Sid6581.ControlNoise);
        var seqA = Record(a, 4096);

        var distinct = new HashSet<byte>(seqA);
        Assert.That(distinct, Has.Count.GreaterThan(50), "noise output should vary");

        // Same program on a fresh chip gives the same sequence.
        var b = new Sid6581();
        SetFreq(b, Voice3, 0xFFFF);
        b.Write(Voice3 + Sid6581.RegControl, Sid6581.ControlNoise);
        Assert.That(Record(b, 4096), Is.EqualTo(seqA));

        // Setting and clearing the test bit mid-stream restarts the accumulator and LFSR: the sequence repeats.
        a.Write(Voice3 + Sid6581.RegControl, Sid6581.ControlNoise | Sid6581.ControlTest);
        Run(a, 123);
        Assert.That(a.PeekAccumulator(2), Is.EqualTo(0), "accumulator is held while the test bit is set");
        a.Write(Voice3 + Sid6581.RegControl, Sid6581.ControlNoise);
        Assert.That(Record(a, 4096), Is.EqualTo(seqA));
    }

    [Test]
    public void TestBit_HoldsAccumulatorAndForcesPulseHigh()
    {
        var sid = new Sid6581();
        SetFreq(sid, Voice3, 0x1000);
        SetPw(sid, Voice3, 0x800);
        sid.Write(Voice3 + Sid6581.RegControl, Sid6581.ControlPulse | Sid6581.ControlTest);

        for (int k = 0; k < 100; k++)
        {
            sid.Clock();
            Assert.That(Osc3(sid), Is.EqualTo(0xFF), "test bit sets the pulse output high");
            Assert.That(sid.PeekAccumulator(2), Is.EqualTo(0));
        }

        sid.Write(Voice3 + Sid6581.RegControl, Sid6581.ControlSawtooth | Sid6581.ControlTest);
        Run(sid, 100);
        Assert.That(Osc3(sid), Is.EqualTo(0), "sawtooth of a held accumulator is 0");

        // Releasing the test bit lets the accumulator count from 0 again.
        sid.Write(Voice3 + Sid6581.RegControl, Sid6581.ControlSawtooth);
        Run(sid, 16);
        Assert.That(sid.PeekAccumulator(2), Is.EqualTo(0x10000));
        Assert.That(Osc3(sid), Is.EqualTo(1));
    }

    [Test]
    public void CombinedWaveform_IsAndOfSelectedWaveforms()
    {
        var sid = new Sid6581();
        SetFreq(sid, Voice3, 0x1000);
        SetPw(sid, Voice3, 0x800);
        sid.Write(Voice3 + Sid6581.RegControl, Sid6581.ControlSawtooth | Sid6581.ControlPulse);

        int elapsed = 0;
        RunTo(sid, ref elapsed, 1000);
        Assert.That(Osc3(sid), Is.EqualTo(0), "pulse is low for the first half so the AND is 0");
        RunTo(sid, ref elapsed, 3000);
        Assert.That(sid.PeekWaveform(2), Is.EqualTo(3000), "pulse high: sawtooth value passes through");
        Assert.That(Osc3(sid), Is.EqualTo(3000 >> 4));
    }

    [Test]
    public void Sync_ResetsAccumulatorOnSourceMsbRisingEdge()
    {
        // Voice 3 is synced by voice 2. Voice 2 at FREQ $1000 sets its MSB in cycle 2048.
        var sid = new Sid6581();
        SetFreq(sid, Voice2, 0x1000);
        sid.Write(Voice2 + Sid6581.RegControl, Sid6581.ControlSawtooth);
        SetFreq(sid, Voice3, 0x0100);
        sid.Write(Voice3 + Sid6581.RegControl, Sid6581.ControlSawtooth | Sid6581.ControlSync);

        int elapsed = 0;
        RunTo(sid, ref elapsed, 2047);
        Assert.That(Osc3(sid), Is.EqualTo(7));
        Assert.That(sid.PeekAccumulator(2), Is.EqualTo(0x100 * 2047));
        RunTo(sid, ref elapsed, 2048);
        Assert.That(sid.PeekAccumulator(2), Is.EqualTo(0), "synced in the cycle the source MSB rises");
        Assert.That(Osc3(sid), Is.EqualTo(0));
        RunTo(sid, ref elapsed, 2048 + 256);
        Assert.That(Osc3(sid), Is.EqualTo(1), "counting restarted from 0");

        // Without the sync bit the accumulator just keeps going.
        var free = new Sid6581();
        SetFreq(free, Voice2, 0x1000);
        free.Write(Voice2 + Sid6581.RegControl, Sid6581.ControlSawtooth);
        SetFreq(free, Voice3, 0x0100);
        free.Write(Voice3 + Sid6581.RegControl, Sid6581.ControlSawtooth);
        Run(free, 2048);
        Assert.That(Osc3(free), Is.EqualTo(8));
    }

    [Test]
    public void Sync_SourceSyncedInTheSameCycleDoesNotSyncItsDestination()
    {
        // reSID: "A special case occurs when a sync source is synced itself on the same cycle as when its MSB is
        // set high. In this case the destination will not be synced." Voice 1 and voice 2 both reach the MSB in
        // cycle 2048; voice 2 (synced by voice 1) must not sync voice 3.
        var sid = new Sid6581();
        SetFreq(sid, Voice1, 0x1000);
        sid.Write(Voice1 + Sid6581.RegControl, Sid6581.ControlSawtooth);
        SetFreq(sid, Voice2, 0x1000);
        sid.Write(Voice2 + Sid6581.RegControl, Sid6581.ControlSawtooth | Sid6581.ControlSync);
        SetFreq(sid, Voice3, 0x0100);
        sid.Write(Voice3 + Sid6581.RegControl, Sid6581.ControlSawtooth | Sid6581.ControlSync);

        Run(sid, 2048);
        Assert.That(sid.PeekAccumulator(1), Is.EqualTo(0), "voice 2 is synced by voice 1");
        Assert.That(sid.PeekAccumulator(2), Is.EqualTo(0x100 * 2048), "voice 3 is not synced");
        Assert.That(Osc3(sid), Is.EqualTo(8));
    }

    [Test]
    public void RingMod_XorsSourceMsbIntoTriangle()
    {
        // Both accumulators are $BB8000 after 3000 cycles at FREQ $1000. Without ring modulation the MSB inverts
        // the triangle: (~$BB8000 >> 11) & $FFF = $88F. With ring modulation the XOR of the two MSBs is 0 and the
        // triangle is not inverted: ($BB8000 >> 11) & $FFF = $770.
        var plain = new Sid6581();
        SetFreq(plain, Voice2, 0x1000);
        SetFreq(plain, Voice3, 0x1000);
        plain.Write(Voice3 + Sid6581.RegControl, Sid6581.ControlTriangle);
        Run(plain, 3000);
        Assert.That(plain.PeekWaveform(2), Is.EqualTo(0x88F));
        Assert.That(Osc3(plain), Is.EqualTo(0x88));

        var ring = new Sid6581();
        SetFreq(ring, Voice2, 0x1000);
        SetFreq(ring, Voice3, 0x1000);
        ring.Write(Voice3 + Sid6581.RegControl, Sid6581.ControlTriangle | Sid6581.ControlRingMod);
        Run(ring, 3000);
        Assert.That(ring.PeekWaveform(2), Is.EqualTo(0x770));
        Assert.That(Osc3(ring), Is.EqualTo(0x77));

        // A source whose MSB is clear leaves the triangle unchanged.
        var idle = new Sid6581();
        SetFreq(idle, Voice3, 0x1000);
        idle.Write(Voice3 + Sid6581.RegControl, Sid6581.ControlTriangle | Sid6581.ControlRingMod);
        Run(idle, 3000);
        Assert.That(Osc3(idle), Is.EqualTo(0x88));
    }

    // ------------------------------------------------------------------------------------------------
    // Envelope
    // ------------------------------------------------------------------------------------------------

    [Test]
    public void Envelope_AttackRate0Reaches255After255RatePeriods()
    {
        var sid = new Sid6581();
        sid.Write(Voice3 + Sid6581.RegAttackDecay, 0x00);
        sid.Write(Voice3 + Sid6581.RegSustainRelease, 0xF0);
        sid.Write(Voice3 + Sid6581.RegControl, Sid6581.ControlSawtooth | Sid6581.ControlGate);

        int elapsed = 0;
        RunTo(sid, ref elapsed, 8);
        Assert.That(Env3(sid), Is.EqualTo(0));
        RunTo(sid, ref elapsed, 9);
        Assert.That(Env3(sid), Is.EqualTo(1), "first step after one rate period of 9 cycles");
        RunTo(sid, ref elapsed, 2200);
        Assert.That(Env3(sid), Is.LessThan(255));
        RunTo(sid, ref elapsed, 2294);
        Assert.That(Env3(sid), Is.EqualTo(254));
        RunTo(sid, ref elapsed, 2295);
        Assert.That(Env3(sid), Is.EqualTo(255), "9 * 255 cycles");
        RunTo(sid, ref elapsed, 2304);
        Assert.That(Env3(sid), Is.EqualTo(255), "sustain 15 holds the level");
        RunTo(sid, ref elapsed, 20000);
        Assert.That(Env3(sid), Is.EqualTo(255));
    }

    [Test]
    public void Envelope_DecayFollowsExponentialCounter()
    {
        // Attack 0, decay 0, sustain 0: the decay steps every 9 cycles at level > $5D, every 18 cycles down to
        // $36, every 36 down to $1A, every 72 down to $0E, every 144 down to $06 and every 270 down to $00.
        var sid = new Sid6581();
        sid.Write(Voice3 + Sid6581.RegAttackDecay, 0x00);
        sid.Write(Voice3 + Sid6581.RegSustainRelease, 0x00);
        sid.Write(Voice3 + Sid6581.RegControl, Sid6581.ControlSawtooth | Sid6581.ControlGate);

        var expectations = new (int Cycle, int Level)[]
        {
            (2295, 0xFF),
            (2303, 0xFF), (2304, 0xFE),
            (3752, 0x5E), (3753, 0x5D),
            (3761, 0x5D), (3771, 0x5C),
            (4454, 0x37), (4455, 0x36),
            (5462, 0x1B), (5463, 0x1A),
            (6326, 0x0F), (6327, 0x0E),
            (7478, 0x07), (7479, 0x06),
            (9098, 0x01), (9099, 0x00),
            (30000, 0x00),
        };

        int elapsed = 0;
        foreach (var (cycle, level) in expectations)
        {
            RunTo(sid, ref elapsed, cycle);
            Assert.That(Env3(sid), Is.EqualTo(level), $"cycle {cycle}");
        }
    }

    [Test]
    public void Envelope_SustainLevelIsHeld()
    {
        // Sustain 8 -> level $88 = 136, reached 119 decay steps (of 9 cycles) after the attack completes.
        var sid = new Sid6581();
        sid.Write(Voice3 + Sid6581.RegAttackDecay, 0x00);
        sid.Write(Voice3 + Sid6581.RegSustainRelease, 0x80);
        sid.Write(Voice3 + Sid6581.RegControl, Sid6581.ControlSawtooth | Sid6581.ControlGate);

        int elapsed = 0;
        RunTo(sid, ref elapsed, 2295 + 119 * 9 - 1);
        Assert.That(Env3(sid), Is.EqualTo(0x89));
        RunTo(sid, ref elapsed, 2295 + 119 * 9);
        Assert.That(Env3(sid), Is.EqualTo(0x88));
        RunTo(sid, ref elapsed, 10000);
        Assert.That(Env3(sid), Is.EqualTo(0x88));
        RunTo(sid, ref elapsed, 100000);
        Assert.That(Env3(sid), Is.EqualTo(0x88));
    }

    [Test]
    public void Envelope_ReleaseDecaysToZeroAndStaysThere()
    {
        var sid = new Sid6581();
        sid.Write(Voice3 + Sid6581.RegAttackDecay, 0x00);
        sid.Write(Voice3 + Sid6581.RegSustainRelease, 0x80);
        sid.Write(Voice3 + Sid6581.RegControl, Sid6581.ControlSawtooth | Sid6581.ControlGate);
        Run(sid, 5000);
        Assert.That(Env3(sid), Is.EqualTo(0x88));

        sid.Write(Voice3 + Sid6581.RegControl, Sid6581.ControlSawtooth); // gate off, release 0
        int prev = Env3(sid);
        int reachedZeroAt = -1;
        for (int k = 1; k <= 20000; k++)
        {
            sid.Clock();
            int v = Env3(sid);
            Assert.That(v, Is.LessThanOrEqualTo(prev), $"release must never increase (cycle {k})");
            if (v == 0 && reachedZeroAt < 0) reachedZeroAt = k;
            prev = v;
        }

        Assert.That(reachedZeroAt, Is.GreaterThan(0).And.LessThan(6000));
        Assert.That(Env3(sid), Is.EqualTo(0));
    }

    [Test]
    public void Envelope_GateOffDuringAttackStartsRelease()
    {
        // Attack 2 (period 63): level 100 after 6300 cycles.
        var sid = new Sid6581();
        sid.Write(Voice3 + Sid6581.RegAttackDecay, 0x20);
        sid.Write(Voice3 + Sid6581.RegSustainRelease, 0x00);
        sid.Write(Voice3 + Sid6581.RegControl, Sid6581.ControlSawtooth | Sid6581.ControlGate);

        int elapsed = 0;
        RunTo(sid, ref elapsed, 6300);
        Assert.That(Env3(sid), Is.EqualTo(100));

        // Gate off, release 0 (period 9). The exponential counter period is updated in every state (reSID), so
        // the attack passing $5D left it at 2: the first release step needs two rate periods = 18 cycles.
        sid.Write(Voice3 + Sid6581.RegControl, Sid6581.ControlSawtooth);
        RunTo(sid, ref elapsed, 6317);
        Assert.That(Env3(sid), Is.EqualTo(100));
        RunTo(sid, ref elapsed, 6318);
        Assert.That(Env3(sid), Is.EqualTo(99), "release steps from the current level");

        int prev = 99;
        for (int k = 0; k < 3000; k++)
        {
            sid.Clock();
            Assert.That(Env3(sid), Is.LessThanOrEqualTo(prev));
            prev = Env3(sid);
        }
        Assert.That(prev, Is.LessThan(50));
    }

    [Test]
    public void Envelope_GateOnDuringReleaseResumesAttackFromCurrentLevel()
    {
        var sid = new Sid6581();
        sid.Write(Voice3 + Sid6581.RegAttackDecay, 0x00);
        sid.Write(Voice3 + Sid6581.RegSustainRelease, 0xF0);
        sid.Write(Voice3 + Sid6581.RegControl, Sid6581.ControlSawtooth | Sid6581.ControlGate);
        Run(sid, 3000);
        Assert.That(Env3(sid), Is.EqualTo(255));

        sid.Write(Voice3 + Sid6581.RegControl, Sid6581.ControlSawtooth);
        Run(sid, 1000); // 111 release steps: 255 - 111 = 144
        Assert.That(Env3(sid), Is.EqualTo(144));

        sid.Write(Voice3 + Sid6581.RegControl, Sid6581.ControlSawtooth | Sid6581.ControlGate);
        int prev = Env3(sid);
        bool reachedTop = false;
        for (int k = 0; k < 2000; k++)
        {
            sid.Clock();
            int v = Env3(sid);
            Assert.That(v, Is.GreaterThanOrEqualTo(prev), "attack resumes upwards from the current level");
            prev = v;
            if (v == 255) { reachedTop = true; break; }
        }
        Assert.That(reachedTop, Is.True);
    }

    [Test]
    public void Envelope_AdsrDelayBug()
    {
        // reSID: "If the rate counter comparison value is set below the current value of the rate counter, the
        // counter will continue counting up until it wraps around to zero at 2^15 = 0x8000, and then count
        // rate_period - 1 before the envelope can finally be stepped." Attack 15 (31251) for 20000 cycles, then
        // attack 0 (9): the counter wraps to 1 in cycle 32768 and reaches 9 in cycle 32776.
        var sid = new Sid6581();
        sid.Write(Voice3 + Sid6581.RegAttackDecay, 0xF0);
        sid.Write(Voice3 + Sid6581.RegSustainRelease, 0xF0);
        sid.Write(Voice3 + Sid6581.RegControl, Sid6581.ControlSawtooth | Sid6581.ControlGate);

        int elapsed = 0;
        RunTo(sid, ref elapsed, 20000);
        Assert.That(Env3(sid), Is.EqualTo(0));
        sid.Write(Voice3 + Sid6581.RegAttackDecay, 0x00);
        RunTo(sid, ref elapsed, 32775);
        Assert.That(Env3(sid), Is.EqualTo(0));
        RunTo(sid, ref elapsed, 32776);
        Assert.That(Env3(sid), Is.EqualTo(1));
        RunTo(sid, ref elapsed, 32776 + 9);
        Assert.That(Env3(sid), Is.EqualTo(2));
    }

    // ------------------------------------------------------------------------------------------------
    // Registers
    // ------------------------------------------------------------------------------------------------

    [Test]
    public void Osc3AndEnv3_ReflectVoice3Only()
    {
        var sid = new Sid6581();
        SetFreq(sid, Voice1, 0x1000);
        sid.Write(Voice1 + Sid6581.RegAttackDecay, 0x00);
        sid.Write(Voice1 + Sid6581.RegSustainRelease, 0xF0);
        sid.Write(Voice1 + Sid6581.RegControl, Sid6581.ControlSawtooth | Sid6581.ControlGate);
        Run(sid, 3000);
        Assert.That(Osc3(sid), Is.EqualTo(0));
        Assert.That(Env3(sid), Is.EqualTo(0));
        Assert.That(sid.PeekEnvelope(0), Is.EqualTo(255));

        SetFreq(sid, Voice3, 0x1000);
        sid.Write(Voice3 + Sid6581.RegAttackDecay, 0x00);
        sid.Write(Voice3 + Sid6581.RegSustainRelease, 0xF0);
        sid.Write(Voice3 + Sid6581.RegControl, Sid6581.ControlSawtooth | Sid6581.ControlGate);
        Run(sid, 3000);
        Assert.That(Osc3(sid), Is.EqualTo(((0x1000 * 3000) & 0xFFFFFF) >> 16));
        Assert.That(Env3(sid), Is.EqualTo(255));
        Assert.That(sid.Peek(Sid6581.RegOsc3), Is.EqualTo(sid.Read(Sid6581.RegOsc3)));
        Assert.That(sid.Peek(Sid6581.RegEnv3), Is.EqualTo(sid.Read(Sid6581.RegEnv3)));
    }

    [Test]
    public void PotRegisters_ReturnPaddleInputs()
    {
        var sid = new Sid6581();
        Assert.That(sid.Read(Sid6581.RegPotX), Is.EqualTo(0xFF));
        Assert.That(sid.Read(Sid6581.RegPotY), Is.EqualTo(0xFF));

        sid.PotX = 0x12;
        sid.PotY = 0x34;
        Assert.That(sid.Read(Sid6581.RegPotX), Is.EqualTo(0x12));
        Assert.That(sid.Read(Sid6581.RegPotY), Is.EqualTo(0x34));
        Assert.That(sid.Peek(Sid6581.RegPotX), Is.EqualTo(0x12));

        sid.Reset();
        Assert.That(sid.Read(Sid6581.RegPotX), Is.EqualTo(0x12), "pot inputs are external and survive a reset");
    }

    [Test]
    public void WriteOnlyAndUnusedRegisters_ReadZero()
    {
        var sid = new Sid6581();
        for (int reg = 0; reg <= 0x18; reg++)
            sid.Write(reg, (byte)(0xA0 + reg));
        Run(sid, 100);

        for (int reg = 0; reg <= 0x18; reg++)
        {
            Assert.That(sid.Read(reg), Is.EqualTo(0), $"register ${reg:X2} is write-only");
            Assert.That(sid.Peek(reg), Is.EqualTo(0xA0 + reg), $"Peek returns the written value of ${reg:X2}");
        }
        for (int reg = 0x1D; reg <= 0x1F; reg++)
        {
            Assert.That(sid.Read(reg), Is.EqualTo(0), $"register ${reg:X2} is unused");
            Assert.That(sid.Peek(reg), Is.EqualTo(0));
        }
    }

    [Test]
    public void Reset_ClearsRegistersAndState()
    {
        var sid = new Sid6581();
        SetFreq(sid, Voice3, 0x1000);
        sid.Write(Voice3 + Sid6581.RegAttackDecay, 0x00);
        sid.Write(Voice3 + Sid6581.RegSustainRelease, 0xF0);
        sid.Write(Voice3 + Sid6581.RegControl, Sid6581.ControlSawtooth | Sid6581.ControlGate);
        sid.Write(Sid6581.RegModeVolume, 0x0F);
        Run(sid, 3000);
        Assert.That(sid.Output, Is.Not.EqualTo(0f));
        Assert.That(Env3(sid), Is.EqualTo(255));

        sid.Reset();
        Assert.That(Osc3(sid), Is.EqualTo(0));
        Assert.That(Env3(sid), Is.EqualTo(0));
        Assert.That(sid.Output, Is.EqualTo(0f));
        for (int reg = 0; reg <= 0x18; reg++)
            Assert.That(sid.Peek(reg), Is.EqualTo(0));

        Run(sid, 3000);
        Assert.That(Osc3(sid), Is.EqualTo(0), "frequency was cleared");
        Assert.That(Env3(sid), Is.EqualTo(0), "gate was cleared");
        Assert.That(sid.Output, Is.EqualTo(0f));
    }

    // ------------------------------------------------------------------------------------------------
    // Mixer and filter
    // ------------------------------------------------------------------------------------------------

    [Test]
    public void Volume0_ProducesSilence()
    {
        var sid = new Sid6581();
        SetFreq(sid, Voice1, 0x1000);
        sid.Write(Voice1 + Sid6581.RegAttackDecay, 0x00);
        sid.Write(Voice1 + Sid6581.RegSustainRelease, 0xF0);
        sid.Write(Voice1 + Sid6581.RegControl, Sid6581.ControlSawtooth | Sid6581.ControlGate);
        sid.Write(Sid6581.RegModeVolume, 0x00);

        for (int k = 0; k < 5000; k++)
        {
            sid.Clock();
            Assert.That(sid.Output, Is.EqualTo(0f));
        }

        sid.Write(Sid6581.RegModeVolume, 0x0F);
        float peak = 0f;
        for (int k = 0; k < 5000; k++)
        {
            sid.Clock();
            peak = Math.Max(peak, Math.Abs(sid.Output));
        }
        Assert.That(peak, Is.GreaterThan(0.3f).And.LessThanOrEqualTo(1f / 3f + 0.001f), "one full scale voice is a third of full scale");
    }

    [Test]
    public void Volume_ScalesLinearly()
    {
        static float Peak(int volume)
        {
            var sid = new Sid6581();
            SetFreq(sid, Voice1, 0x1000);
            sid.Write(Voice1 + Sid6581.RegAttackDecay, 0x00);
            sid.Write(Voice1 + Sid6581.RegSustainRelease, 0xF0);
            sid.Write(Voice1 + Sid6581.RegControl, Sid6581.ControlSawtooth | Sid6581.ControlGate);
            sid.Write(Sid6581.RegModeVolume, (byte)volume);
            Run(sid, 3000);
            float peak = 0f;
            for (int k = 0; k < 4096; k++)
            {
                sid.Clock();
                peak = Math.Max(peak, Math.Abs(sid.Output));
            }
            return peak;
        }

        float full = Peak(15);
        Assert.That(Peak(7), Is.EqualTo(full * 7f / 15f).Within(0.001f));
        Assert.That(Peak(1), Is.EqualTo(full / 15f).Within(0.001f));
    }

    private static Sid6581 ToneSid(int freq, bool routed, byte mode, int fc)
    {
        var sid = new Sid6581();
        SetFreq(sid, Voice1, freq);
        sid.Write(Voice1 + Sid6581.RegAttackDecay, 0x00);
        sid.Write(Voice1 + Sid6581.RegSustainRelease, 0xF0);
        sid.Write(Voice1 + Sid6581.RegControl, Sid6581.ControlTriangle | Sid6581.ControlGate);
        sid.Write(Sid6581.RegFilterCutoffLo, (byte)(fc & 7));
        sid.Write(Sid6581.RegFilterCutoffHi, (byte)(fc >> 3));
        sid.Write(Sid6581.RegFilterResonanceRouting, (byte)(routed ? Sid6581.FilterVoice1 : 0));
        sid.Write(Sid6581.RegModeVolume, (byte)(mode | 0x0F));
        return sid;
    }

    private static double Rms(Sid6581 sid, int warmup, int cycles)
    {
        Run(sid, warmup);
        double sum = 0, sumSq = 0;
        for (int i = 0; i < cycles; i++)
        {
            sid.Clock();
            double v = sid.Output;
            sum += v;
            sumSq += v * v;
        }
        double mean = sum / cycles;
        return Math.Sqrt(Math.Max(0, sumSq / cycles - mean * mean));
    }

    private const int LowToneFreq = 3405;    // ~200 Hz on PAL
    private const int HighToneFreq = 0xFFFF; // ~3848 Hz on PAL

    [Test]
    public void Filter_LowPassAttenuatesHighFrequencyMoreThanLow()
    {
        const int fc = 256; // 250 Hz on the reSID 6581 curve
        double lowPlain = Rms(ToneSid(LowToneFreq, false, Sid6581.ModeLowPass, fc), 20000, 100000);
        double highPlain = Rms(ToneSid(HighToneFreq, false, Sid6581.ModeLowPass, fc), 20000, 100000);
        double lowFiltered = Rms(ToneSid(LowToneFreq, true, Sid6581.ModeLowPass, fc), 20000, 100000);
        double highFiltered = Rms(ToneSid(HighToneFreq, true, Sid6581.ModeLowPass, fc), 20000, 100000);

        TestContext.Out.WriteLine($"LP fc={fc}: low {lowPlain:F4} -> {lowFiltered:F4}, high {highPlain:F4} -> {highFiltered:F4}");
        Assert.That(lowPlain, Is.EqualTo(highPlain).Within(0.02));
        Assert.That(lowFiltered, Is.GreaterThan(lowPlain * 0.5), "200 Hz passes a 250 Hz low-pass mostly unattenuated");
        Assert.That(highFiltered, Is.LessThan(highPlain * 0.1), "3.8 kHz is strongly attenuated");
        Assert.That(highFiltered, Is.LessThan(lowFiltered / 10));
    }

    [Test]
    public void Filter_HighPassAttenuatesLowFrequencyMoreThanHigh()
    {
        const int fc = 1024; // 4600 Hz on the reSID 6581 curve
        double lowPlain = Rms(ToneSid(LowToneFreq, false, Sid6581.ModeHighPass, fc), 20000, 100000);
        double highPlain = Rms(ToneSid(HighToneFreq, false, Sid6581.ModeHighPass, fc), 20000, 100000);
        double lowFiltered = Rms(ToneSid(LowToneFreq, true, Sid6581.ModeHighPass, fc), 20000, 100000);
        double highFiltered = Rms(ToneSid(HighToneFreq, true, Sid6581.ModeHighPass, fc), 20000, 100000);

        TestContext.Out.WriteLine($"HP fc={fc}: low {lowPlain:F4} -> {lowFiltered:F4}, high {highPlain:F4} -> {highFiltered:F4}");
        Assert.That(lowFiltered, Is.LessThan(lowPlain * 0.1), "200 Hz is strongly attenuated by a 4.6 kHz high-pass");
        Assert.That(highFiltered, Is.GreaterThan(highPlain * 0.3));
        Assert.That(lowFiltered, Is.LessThan(highFiltered / 10));
    }

    [Test]
    public void Filter_BandPassPeaksNearCutoff()
    {
        const int cutoff = 640;      // 780 Hz
        const int nearFreq = 13280;  // ~780 Hz on PAL
        double near = Rms(ToneSid(nearFreq, true, Sid6581.ModeBandPass, cutoff), 20000, 100000);
        double low = Rms(ToneSid(LowToneFreq, true, Sid6581.ModeBandPass, cutoff), 20000, 100000);
        double high = Rms(ToneSid(HighToneFreq, true, Sid6581.ModeBandPass, cutoff), 20000, 100000);

        TestContext.Out.WriteLine($"BP fc={cutoff}: low {low:F4}, near {near:F4}, high {high:F4}");
        Assert.That(near, Is.GreaterThan(low * 2));
        Assert.That(near, Is.GreaterThan(high * 2));
    }

    [Test]
    public void Filter_ResonanceBoostsCutoffRegion()
    {
        const int cutoff = 640;      // 780 Hz
        const int nearFreq = 13280;  // ~780 Hz

        static double RmsWithRes(int res, int freq, int fc)
        {
            var sid = ToneSid(freq, true, Sid6581.ModeLowPass, fc);
            sid.Write(Sid6581.RegFilterResonanceRouting, (byte)((res << 4) | Sid6581.FilterVoice1));
            return Rms(sid, 20000, 100000);
        }

        double flat = RmsWithRes(0, nearFreq, cutoff);
        double resonant = RmsWithRes(15, nearFreq, cutoff);
        Assert.That(resonant, Is.GreaterThan(flat * 1.5), "Q rises from 0.707 to about 1.7 with resonance 15");
    }

    [Test]
    public void Filter_Voice3OffSilencesVoice3OnlyWhenNotRouted()
    {
        static Sid6581 Voice3Sid(byte routing, byte mode)
        {
            var sid = new Sid6581();
            SetFreq(sid, Voice3, 0x1000);
            sid.Write(Voice3 + Sid6581.RegAttackDecay, 0x00);
            sid.Write(Voice3 + Sid6581.RegSustainRelease, 0xF0);
            sid.Write(Voice3 + Sid6581.RegControl, Sid6581.ControlSawtooth | Sid6581.ControlGate);
            sid.Write(Sid6581.RegFilterCutoffHi, 0xFF);
            sid.Write(Sid6581.RegFilterResonanceRouting, routing);
            sid.Write(Sid6581.RegModeVolume, (byte)(mode | 0x0F));
            Run(sid, 3000);
            return sid;
        }

        static float Peak(Sid6581 sid)
        {
            float peak = 0f;
            for (int k = 0; k < 4096; k++)
            {
                sid.Clock();
                peak = Math.Max(peak, Math.Abs(sid.Output));
            }
            return peak;
        }

        Assert.That(Peak(Voice3Sid(0, 0)), Is.GreaterThan(0.3f), "voice 3 audible");
        Assert.That(Peak(Voice3Sid(0, Sid6581.ModeVoice3Off)), Is.EqualTo(0f), "3 OFF silences an unrouted voice 3");
        Assert.That(Peak(Voice3Sid(Sid6581.FilterVoice3, (byte)(Sid6581.ModeVoice3Off | Sid6581.ModeLowPass))), Is.GreaterThan(0.1f),
            "3 OFF does not affect voice 3 when it is routed through the filter (reSID)");
        Assert.That(Peak(Voice3Sid(Sid6581.FilterVoice3, Sid6581.ModeVoice3Off)), Is.EqualTo(0f),
            "routed into the filter with no filter mode selected, nothing reaches the output");
    }

    [Test]
    public void Filter_CutoffCurveMatchesResidPoints()
    {
        Assert.That(Sid6581.CutoffFrequencyHz(0), Is.EqualTo(220f).Within(0.01f));
        Assert.That(Sid6581.CutoffFrequencyHz(256), Is.EqualTo(250f).Within(0.01f));
        Assert.That(Sid6581.CutoffFrequencyHz(768), Is.EqualTo(1600f).Within(0.01f));
        Assert.That(Sid6581.CutoffFrequencyHz(1023), Is.EqualTo(6000f).Within(0.01f));
        Assert.That(Sid6581.CutoffFrequencyHz(1024), Is.EqualTo(4600f).Within(0.01f), "the 6581 curve drops at the DAC MSB");
        Assert.That(Sid6581.CutoffFrequencyHz(1536), Is.EqualTo(14500f).Within(0.01f));
        Assert.That(Sid6581.CutoffFrequencyHz(2047), Is.EqualTo(18000f).Within(0.01f));

        for (int fc = 1; fc < 2048; fc++)
        {
            if (fc == 1024) continue;
            Assert.That(Sid6581.CutoffFrequencyHz(fc), Is.GreaterThanOrEqualTo(Sid6581.CutoffFrequencyHz(fc - 1) - 0.01f), $"fc {fc}");
        }
    }

    [Test]
    public void Output_StaysWithinRange()
    {
        var sid = new Sid6581();
        SetFreq(sid, Voice1, 0x2000);
        sid.Write(Voice1 + Sid6581.RegAttackDecay, 0x00);
        sid.Write(Voice1 + Sid6581.RegSustainRelease, 0xF0);
        sid.Write(Voice1 + Sid6581.RegControl, Sid6581.ControlSawtooth | Sid6581.ControlGate);
        SetFreq(sid, Voice2, 0x3000);
        SetPw(sid, Voice2, 0x800);
        sid.Write(Voice2 + Sid6581.RegAttackDecay, 0x00);
        sid.Write(Voice2 + Sid6581.RegSustainRelease, 0xF0);
        sid.Write(Voice2 + Sid6581.RegControl, Sid6581.ControlPulse | Sid6581.ControlGate);
        SetFreq(sid, Voice3, 0x1234);
        sid.Write(Voice3 + Sid6581.RegAttackDecay, 0x00);
        sid.Write(Voice3 + Sid6581.RegSustainRelease, 0xF0);
        sid.Write(Voice3 + Sid6581.RegControl, Sid6581.ControlTriangle | Sid6581.ControlGate);
        sid.Write(Sid6581.RegFilterResonanceRouting, 0xF7);
        sid.Write(Sid6581.RegModeVolume, 0x7F);

        float min = 0f, max = 0f;
        for (int fc = 0; fc < 2048; fc++)
        {
            sid.Write(Sid6581.RegFilterCutoffLo, (byte)(fc & 7));
            sid.Write(Sid6581.RegFilterCutoffHi, (byte)(fc >> 3));
            for (int k = 0; k < 100; k++)
            {
                sid.Clock();
                float v = sid.Output;
                Assert.That(float.IsFinite(v), Is.True);
                if (v < min) min = v;
                if (v > max) max = v;
            }
        }

        Assert.That(max, Is.LessThanOrEqualTo(1f));
        Assert.That(min, Is.GreaterThanOrEqualTo(-1f));
        Assert.That(max, Is.GreaterThan(0.2f));
        Assert.That(min, Is.LessThan(-0.2f));
    }

    // ------------------------------------------------------------------------------------------------
    // Resampler and WAV output
    // ------------------------------------------------------------------------------------------------

    [TestCase(44100)]
    [TestCase(48000)]
    [TestCase(22050)]
    public void Resampler_ProducesSampleRateSamplesPerSecond(int sampleRate)
    {
        var sid = new Sid6581();
        var resampler = new SidResampler(sid, sampleRate, Pal, bufferSize: sampleRate * 2);
        for (int k = 0; k < (int)Pal; k++)
            resampler.Clock();

        Assert.That(resampler.Available, Is.EqualTo(sampleRate).Within(2));
        Assert.That(resampler.SamplesProduced, Is.EqualTo(sampleRate).Within(2));
        Assert.That(resampler.Overruns, Is.EqualTo(0));
    }

    [Test]
    public void Resampler_ReadDrainsBuffer()
    {
        var sid = new Sid6581();
        var resampler = new SidResampler(sid, 44100);
        for (int k = 0; k < 100000; k++)
            resampler.Clock();

        int available = resampler.Available;
        Assert.That(available, Is.EqualTo((int)(100000L * 44100L / (long)Pal)).Within(1));

        var buffer = new short[1000];
        Assert.That(resampler.Read(buffer), Is.EqualTo(1000));
        Assert.That(resampler.Available, Is.EqualTo(available - 1000));

        var rest = new short[10000];
        Assert.That(resampler.Read(rest), Is.EqualTo(available - 1000));
        Assert.That(resampler.Available, Is.EqualTo(0));
        Assert.That(resampler.Read(rest), Is.EqualTo(0));
    }

    [Test]
    public void Resampler_AveragesCycleOutputsIntoSamples()
    {
        // Test bit + pulse gives a constant waveform of $FFF; with a full envelope and volume 15 the output is
        // (($FFF - $800) * 255) / (2048 * 255) / 3 = 2047 / 6144 = 0.33317 -> 10917 in 16 bits.
        var sid = new Sid6581();
        sid.Write(Voice1 + Sid6581.RegAttackDecay, 0x00);
        sid.Write(Voice1 + Sid6581.RegSustainRelease, 0xF0);
        sid.Write(Voice1 + Sid6581.RegControl, Sid6581.ControlPulse | Sid6581.ControlTest | Sid6581.ControlGate);
        sid.Write(Sid6581.RegModeVolume, 0x0F);
        Run(sid, 3000);

        var resampler = new SidResampler(sid, 44100);
        for (int k = 0; k < 50000; k++)
            resampler.Clock();

        var samples = new short[resampler.Available];
        resampler.Read(samples);
        int expected = (int)Math.Round(2047.0 / 6144.0 * 32767.0);
        foreach (var s in samples)
            Assert.That(s, Is.EqualTo(expected).Within(1));
    }

    [Test]
    public void Resampler_DropsSamplesWhenBufferIsFull()
    {
        var sid = new Sid6581();
        var resampler = new SidResampler(sid, 44100, Pal, bufferSize: 10);
        for (int k = 0; k < (int)Pal; k++)
            resampler.Clock();

        Assert.That(resampler.Available, Is.EqualTo(10));
        Assert.That(resampler.Overruns, Is.EqualTo(44100 - 10).Within(2));
        Assert.That(resampler.Capacity, Is.EqualTo(10));
    }

    [Test]
    public void Resampler_RejectsInvalidArguments()
    {
        var sid = new Sid6581();
        Assert.That(() => new SidResampler(sid, 0), Throws.TypeOf<ArgumentOutOfRangeException>());
        Assert.That(() => new SidResampler(sid, 2_000_000), Throws.TypeOf<ArgumentOutOfRangeException>());
        Assert.That(() => new SidResampler(null!, 44100), Throws.TypeOf<ArgumentNullException>());
    }

    [Test]
    public void WavWriter_EncodesCanonicalHeader()
    {
        var samples = new short[] { 0, 1, -1, short.MaxValue, short.MinValue };
        var bytes = WavWriter.Encode(44100, samples);

        Assert.That(bytes, Has.Length.EqualTo(44 + samples.Length * 2));
        Assert.That(System.Text.Encoding.ASCII.GetString(bytes, 0, 4), Is.EqualTo("RIFF"));
        Assert.That(BitConverter.ToInt32(bytes, 4), Is.EqualTo(36 + samples.Length * 2));
        Assert.That(System.Text.Encoding.ASCII.GetString(bytes, 8, 4), Is.EqualTo("WAVE"));
        Assert.That(System.Text.Encoding.ASCII.GetString(bytes, 12, 4), Is.EqualTo("fmt "));
        Assert.That(BitConverter.ToInt16(bytes, 20), Is.EqualTo(1), "PCM");
        Assert.That(BitConverter.ToInt16(bytes, 22), Is.EqualTo(1), "mono");
        Assert.That(BitConverter.ToInt32(bytes, 24), Is.EqualTo(44100));
        Assert.That(BitConverter.ToInt32(bytes, 28), Is.EqualTo(88200), "byte rate");
        Assert.That(BitConverter.ToInt16(bytes, 32), Is.EqualTo(2), "block align");
        Assert.That(BitConverter.ToInt16(bytes, 34), Is.EqualTo(16), "bits per sample");
        Assert.That(System.Text.Encoding.ASCII.GetString(bytes, 36, 4), Is.EqualTo("data"));
        Assert.That(BitConverter.ToInt32(bytes, 40), Is.EqualTo(samples.Length * 2));
        for (int i = 0; i < samples.Length; i++)
            Assert.That(BitConverter.ToInt16(bytes, 44 + i * 2), Is.EqualTo(samples[i]));
    }

    [Test]
    public void WavFile_CMajorScaleIsWritten()
    {
        const int sampleRate = 44100;
        var sid = new Sid6581();
        var resampler = new SidResampler(sid, sampleRate, Pal, bufferSize: sampleRate * 4);

        // Low-pass at ~3.2 kHz with some resonance on voice 1; pulse wave with a short attack and a 100 ms release.
        sid.Write(Sid6581.RegFilterCutoffLo, 0x00);
        sid.Write(Sid6581.RegFilterCutoffHi, 0x70);
        sid.Write(Sid6581.RegFilterResonanceRouting, 0x80 | Sid6581.FilterVoice1);
        sid.Write(Sid6581.RegModeVolume, Sid6581.ModeLowPass | 0x0F);
        sid.Write(Voice1 + Sid6581.RegAttackDecay, 0x19);
        sid.Write(Voice1 + Sid6581.RegSustainRelease, 0xA8);
        SetPw(sid, Voice1, 0x800);

        double[] notes = { 261.63, 293.66, 329.63, 349.23, 392.00, 440.00, 493.88, 523.25 }; // C4 .. C5
        var samples = new List<short>(sampleRate * 2);
        var chunk = new short[4096];

        void RunAndCollect(int cycles)
        {
            for (int k = 0; k < cycles; k++)
            {
                resampler.Clock();
                if (resampler.Available >= chunk.Length)
                    samples.AddRange(chunk.AsSpan(0, resampler.Read(chunk)).ToArray());
            }
        }

        foreach (double hz in notes)
        {
            SetFreq(sid, Voice1, (int)Math.Round(hz * 16777216.0 / Pal));
            sid.Write(Voice1 + Sid6581.RegControl, Sid6581.ControlPulse | Sid6581.ControlGate);
            RunAndCollect((int)(0.12 * Pal));
            sid.Write(Voice1 + Sid6581.RegControl, Sid6581.ControlPulse);
            RunAndCollect((int)(0.03 * Pal));
        }
        while (resampler.Available > 0)
            samples.AddRange(chunk.AsSpan(0, resampler.Read(chunk)).ToArray());

        string path = Path.Combine(TestResultsDir(), "sid_scale.wav");
        WavWriter.Save(path, sampleRate, samples.ToArray());
        TestContext.Out.WriteLine($"Wrote {path}");

        var info = new FileInfo(path);
        Assert.That(info.Exists, Is.True);
        Assert.That(info.Length, Is.EqualTo(WavWriter.HeaderSize + samples.Count * 2));
        Assert.That(samples.Count, Is.EqualTo((int)(8 * 0.15 * sampleRate)).Within(20));

        int peak = 0;
        foreach (var s in samples)
            peak = Math.Max(peak, Math.Abs((int)s));
        Assert.That(peak, Is.GreaterThan(2000).And.LessThanOrEqualTo(short.MaxValue));
    }
}
