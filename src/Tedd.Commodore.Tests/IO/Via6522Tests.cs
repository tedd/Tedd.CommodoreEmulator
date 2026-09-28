using System.Collections.Generic;
using NUnit.Framework;
using Tedd.Commodore.Emulator.IO;

namespace Tedd.Commodore.Tests.IO;

/// <summary>
/// Cycle-level tests for <see cref="Via6522"/>. Timing expectations come from the MOS 6522 data sheet
/// (timer intervals of N+2 cycles from the T1C-H/T2C-H write to the interrupt flag, N+2 period in free-running
/// mode) as described in docs/ARCHITECTURE.md.
/// </summary>
[TestFixture]
public class Via6522Tests
{
    private Via6522 _via = null!;
    private byte _portA;
    private byte _portB;

    [SetUp]
    public void SetUp()
    {
        _via = new Via6522("VIA-test");
        _portA = 0xFF;
        _portB = 0xFF;
        _via.PortAInput = () => _portA;
        _via.PortBInput = () => _portB;
    }

    private void Clock(int cycles)
    {
        for (int i = 0; i < cycles; i++)
            _via.Clock();
    }

    private bool Flag(byte bit) => (_via.Peek(Via6522.RegIfr) & bit) != 0;

    /// <summary>Starts timer 1 with latch N and returns the number of Clock() calls until the T1 flag appears.</summary>
    private int ClocksUntilT1Flag(int limit = 100000)
    {
        int n = 0;
        while (!Flag(Via6522.IfrT1))
        {
            _via.Clock();
            n++;
            if (n > limit) Assert.Fail("T1 flag never set");
        }
        return n;
    }

    // ------------------------------------------------------------------------------------------------
    // Reset / registers
    // ------------------------------------------------------------------------------------------------

    [Test]
    public void Reset_ClearsRegisters_AndPortsReadAsInputs()
    {
        _via.Write(Via6522.RegDdra, 0xFF);
        _via.Write(Via6522.RegOra, 0x12);
        _via.Write(Via6522.RegAcr, 0xC0);
        _via.Write(Via6522.RegIer, 0xFF);
        _via.Reset();

        Assert.That(_via.Peek(Via6522.RegDdra), Is.EqualTo(0));
        Assert.That(_via.Peek(Via6522.RegDdrb), Is.EqualTo(0));
        Assert.That(_via.Peek(Via6522.RegAcr), Is.EqualTo(0));
        Assert.That(_via.Peek(Via6522.RegPcr), Is.EqualTo(0));
        Assert.That(_via.Peek(Via6522.RegIfr), Is.EqualTo(0));
        Assert.That(_via.Peek(Via6522.RegIer), Is.EqualTo(0x80), "IER bit 7 always reads 1");
        Assert.That(_via.PortAOutput, Is.EqualTo(0xFF));
        Assert.That(_via.PortBOutput, Is.EqualTo(0xFF));
        Assert.That(_via.IrqLine, Is.False);
        Assert.That(_via.Ca2Output, Is.True);
        Assert.That(_via.Cb2Output, Is.True);
    }

    // ------------------------------------------------------------------------------------------------
    // Timer 1
    // ------------------------------------------------------------------------------------------------

    [Test]
    public void T1_OneShot_FlagIsSetExactlyNPlus2CyclesAfterWrite()
    {
        const int n = 10;
        _via.Write(Via6522.RegT1CL, n);
        _via.Write(Via6522.RegT1CH, 0);
        Assert.That(_via.Peek(Via6522.RegT1CL), Is.EqualTo(n), "counter is loaded from the latch on the T1C-H write");

        Clock(n + 1);
        Assert.That(Flag(Via6522.IfrT1), Is.False, "no flag after N+1 cycles");
        Assert.That(_via.Peek(Via6522.RegT1CL), Is.EqualTo(0));

        _via.Clock();
        Assert.That(Flag(Via6522.IfrT1), Is.True, "flag after N+2 cycles");
        Assert.That(_via.Peek(Via6522.RegT1CH), Is.EqualTo(0xFF), "counter wrapped to $FFFF");
    }

    [Test]
    public void T1_OneShot_NoSecondFlag_CounterKeepsWrapping()
    {
        const int n = 5;
        _via.Write(Via6522.RegT1CL, n);
        _via.Write(Via6522.RegT1CH, 0);
        Assert.That(ClocksUntilT1Flag(), Is.EqualTo(n + 2));

        _via.Read(Via6522.RegT1CL);   // clear
        Assert.That(Flag(Via6522.IfrT1), Is.False);
        Clock(3 * (n + 2));
        Assert.That(Flag(Via6522.IfrT1), Is.False, "one-shot mode sets the flag only once");
        // After the time-out the counter continued from $FFFF downwards.
        int counter = _via.Peek(Via6522.RegT1CL) | (_via.Peek(Via6522.RegT1CH) << 8);
        Assert.That(counter, Is.EqualTo(0xFFFF - 3 * (n + 2)));

        // Writing T1C-H re-arms it.
        _via.Write(Via6522.RegT1CH, 0);
        Assert.That(ClocksUntilT1Flag(), Is.EqualTo(n + 2));
    }

    [Test]
    public void T1_FreeRunning_PeriodIsNPlus2_AndReloadsFromLatch()
    {
        const int n = 5;
        _via.Write(Via6522.RegAcr, 0x40);
        _via.Write(Via6522.RegT1CL, n);
        _via.Write(Via6522.RegT1CH, 0);
        Assert.That(ClocksUntilT1Flag(), Is.EqualTo(n + 2));
        Assert.That(_via.Peek(Via6522.RegT1CL), Is.EqualTo(0xFF), "counter shows $FFFF for one cycle after time-out");
        Assert.That(_via.Read(Via6522.RegT1CL), Is.EqualTo(0xFF));
        Assert.That(Flag(Via6522.IfrT1), Is.False, "reading T1C-L clears the flag");

        _via.Clock();
        Assert.That(_via.Peek(Via6522.RegT1CL), Is.EqualTo(n), "reloaded from the latch");
        Assert.That(ClocksUntilT1Flag(), Is.EqualTo(n + 1), "second time-out N+2 cycles after the first");

        _via.Read(Via6522.RegT1CL);
        Assert.That(ClocksUntilT1Flag(), Is.EqualTo(n + 2), "period stays N+2");

        // A new latch value takes effect at the next reload without restarting the timer.
        _via.Read(Via6522.RegT1CL);
        _via.Clock();                                    // reload cycle (counter = n again)
        Assert.That(_via.Peek(Via6522.RegT1CL), Is.EqualTo(n));
        _via.Write(Via6522.RegT1LL, 2);
        Assert.That(ClocksUntilT1Flag(), Is.EqualTo(n + 1), "current period unaffected");
        _via.Read(Via6522.RegT1CL);
        Assert.That(ClocksUntilT1Flag(), Is.EqualTo(2 + 2), "next period uses the new latch");
    }

    [Test]
    public void T1_Pb7_OneShot_LowOnWrite_HighOnTimeout()
    {
        int changes = 0;
        _via.PortBChanged += () => changes++;
        _via.Write(Via6522.RegAcr, 0x80);
        Assert.That(_via.PortBOutput & 0x80, Is.EqualTo(0x80), "PB7 idles high");
        Assert.That(_via.Peek(Via6522.RegOrb) & 0x80, Is.EqualTo(0x80));

        _via.Write(Via6522.RegT1CL, 4);
        int before = changes;
        _via.Write(Via6522.RegT1CH, 0);
        Assert.That(_via.PortBOutput & 0x80, Is.EqualTo(0), "PB7 goes low when T1C-H is written");
        Assert.That(_via.Peek(Via6522.RegOrb) & 0x80, Is.EqualTo(0), "IRB shows the timer output on PB7");
        Assert.That(changes, Is.GreaterThan(before));

        Clock(5);
        Assert.That(_via.PortBOutput & 0x80, Is.EqualTo(0), "still low one cycle before time-out");
        before = changes;
        _via.Clock();
        Assert.That(Flag(Via6522.IfrT1), Is.True);
        Assert.That(_via.PortBOutput & 0x80, Is.EqualTo(0x80), "PB7 goes high at time-out");
        Assert.That(changes, Is.EqualTo(before + 1));

        Clock(20);
        Assert.That(_via.PortBOutput & 0x80, Is.EqualTo(0x80), "stays high in one-shot mode");
    }

    [Test]
    public void T1_Pb7_FreeRunning_TogglesAtEveryTimeout()
    {
        _via.Write(Via6522.RegAcr, 0xC0);
        _via.Write(Via6522.RegT1CL, 3);
        _via.Write(Via6522.RegT1CH, 0);
        Assert.That(_via.PortBOutput & 0x80, Is.EqualTo(0));

        for (int i = 0; i < 4; i++)
        {
            Clock(4);
            Assert.That(_via.PortBOutput & 0x80, Is.EqualTo(0), $"before time-out {i}");
            _via.Clock();
            Assert.That(_via.PortBOutput & 0x80, Is.EqualTo(0x80), $"toggled high at time-out {i}");
            _via.Read(Via6522.RegT1CL);
            Clock(5);
            Assert.That(_via.PortBOutput & 0x80, Is.EqualTo(0), $"toggled low at time-out {i}");
            _via.Read(Via6522.RegT1CL);
        }
    }

    [Test]
    public void T1_Pb7_NotOverlaidWhenAcrBit7Clear()
    {
        _via.Write(Via6522.RegDdrb, 0xFF);
        _via.Write(Via6522.RegOrb, 0xFF);
        _via.Write(Via6522.RegT1CL, 2);
        _via.Write(Via6522.RegT1CH, 0);
        Assert.That(_via.PortBOutput, Is.EqualTo(0xFF), "ORB drives PB7 when the timer output is disabled");
        Clock(4);
        Assert.That(_via.PortBOutput, Is.EqualTo(0xFF));
    }

    [Test]
    public void T1_FlagClearing()
    {
        _via.Write(Via6522.RegT1CL, 1);
        _via.Write(Via6522.RegT1CH, 0);
        Clock(3);
        Assert.That(Flag(Via6522.IfrT1), Is.True);

        _via.Read(Via6522.RegT1CH);
        Assert.That(Flag(Via6522.IfrT1), Is.True, "reading T1C-H does not clear");
        _via.Read(Via6522.RegT1LL);
        _via.Read(Via6522.RegT1LH);
        Assert.That(Flag(Via6522.IfrT1), Is.True, "reading the latches does not clear");

        _via.Read(Via6522.RegT1CL);
        Assert.That(Flag(Via6522.IfrT1), Is.False, "reading T1C-L clears");

        _via.Write(Via6522.RegT1CH, 0); Clock(3);
        Assert.That(Flag(Via6522.IfrT1), Is.True);
        _via.Write(Via6522.RegT1LH, 0);
        Assert.That(Flag(Via6522.IfrT1), Is.False, "writing T1L-H clears");

        _via.Write(Via6522.RegT1CH, 0); Clock(3);
        Assert.That(Flag(Via6522.IfrT1), Is.True);
        _via.Write(Via6522.RegT1CH, 0);
        Assert.That(Flag(Via6522.IfrT1), Is.False, "writing T1C-H clears");

        Clock(3);
        Assert.That(Flag(Via6522.IfrT1), Is.True);
        _via.Write(Via6522.RegIfr, Via6522.IfrT1);
        Assert.That(Flag(Via6522.IfrT1), Is.False, "writing 1 to IFR bit 6 clears");
    }

    // ------------------------------------------------------------------------------------------------
    // Timer 2
    // ------------------------------------------------------------------------------------------------

    [Test]
    public void T2_OneShot_FlagAtNPlus2_NoSecondFlag()
    {
        const int n = 8;
        _via.Write(Via6522.RegT2CL, n);
        _via.Write(Via6522.RegT2CH, 0);
        Assert.That(_via.Peek(Via6522.RegT2CL), Is.EqualTo(n));

        Clock(n + 1);
        Assert.That(Flag(Via6522.IfrT2), Is.False);
        _via.Clock();
        Assert.That(Flag(Via6522.IfrT2), Is.True, "flag after N+2 cycles");
        Assert.That(_via.Peek(Via6522.RegT2CH), Is.EqualTo(0xFF));

        _via.Read(Via6522.RegT2CL);
        Clock(5 * (n + 2));
        Assert.That(Flag(Via6522.IfrT2), Is.False, "no further flags until T2C-H is written");
        int counter = _via.Peek(Via6522.RegT2CL) | (_via.Peek(Via6522.RegT2CH) << 8);
        Assert.That(counter, Is.EqualTo(0xFFFF - 5 * (n + 2)), "keeps decrementing");
    }

    [Test]
    public void T2_16BitInterval()
    {
        _via.Write(Via6522.RegT2CL, 0x02);
        _via.Write(Via6522.RegT2CH, 0x01);   // N = 258
        Clock(259);
        Assert.That(Flag(Via6522.IfrT2), Is.False);
        _via.Clock();
        Assert.That(Flag(Via6522.IfrT2), Is.True);
    }

    [Test]
    public void T2_PulseCounting_CountsNegativeTransitionsOnPb6()
    {
        _via.Write(Via6522.RegAcr, 0x20);
        _via.Write(Via6522.RegT2CL, 3);
        _via.Write(Via6522.RegT2CH, 0);

        Clock(10);
        Assert.That(_via.Peek(Via6522.RegT2CL), Is.EqualTo(3), "φ2 does not decrement in pulse counting mode");

        for (int pulse = 1; pulse <= 3; pulse++)
        {
            _portB = 0xFF; _via.Clock();          // high (positive transition: ignored)
            Assert.That(_via.Peek(Via6522.RegT2CL), Is.EqualTo(3 - (pulse - 1)));
            _portB = 0xBF; _via.Clock();          // PB6 low: negative transition counts
            Assert.That(_via.Peek(Via6522.RegT2CL), Is.EqualTo(3 - pulse));
            _via.Clock(); _via.Clock();           // staying low does not count
            Assert.That(_via.Peek(Via6522.RegT2CL), Is.EqualTo(3 - pulse));
            Assert.That(Flag(Via6522.IfrT2), Is.EqualTo(pulse == 3), $"flag after pulse {pulse}");
        }

        // Further pulses keep decrementing (wrapping) without a new flag.
        _via.Read(Via6522.RegT2CL);
        _portB = 0xFF; _via.Clock();
        _portB = 0xBF; _via.Clock();
        Assert.That(_via.Peek(Via6522.RegT2CH), Is.EqualTo(0xFF));
        Assert.That(Flag(Via6522.IfrT2), Is.False);
    }

    [Test]
    public void T2_FlagClearing()
    {
        _via.Write(Via6522.RegT2CL, 1);
        _via.Write(Via6522.RegT2CH, 0);
        Clock(3);
        Assert.That(Flag(Via6522.IfrT2), Is.True);
        _via.Read(Via6522.RegT2CH);
        Assert.That(Flag(Via6522.IfrT2), Is.True, "reading T2C-H does not clear");
        _via.Write(Via6522.RegT2CL, 1);
        Assert.That(Flag(Via6522.IfrT2), Is.True, "writing T2C-L does not clear");
        _via.Read(Via6522.RegT2CL);
        Assert.That(Flag(Via6522.IfrT2), Is.False, "reading T2C-L clears");

        _via.Write(Via6522.RegT2CH, 0);
        Clock(3);
        Assert.That(Flag(Via6522.IfrT2), Is.True);
        _via.Write(Via6522.RegT2CH, 0);
        Assert.That(Flag(Via6522.IfrT2), Is.False, "writing T2C-H clears");
    }

    // ------------------------------------------------------------------------------------------------
    // CA1 / CB1
    // ------------------------------------------------------------------------------------------------

    [Test]
    public void Ca1_NegativeEdge_SetsFlag_ClearedByOraAccess()
    {
        _via.Write(Via6522.RegPcr, 0x00);
        _via.Ca1 = true;
        _via.Ca1 = false;
        Assert.That(Flag(Via6522.IfrCa1), Is.True);

        _via.Read(Via6522.RegOraNoHandshake);
        Assert.That(Flag(Via6522.IfrCa1), Is.True, "register 15 does not clear CA1");
        _via.Read(Via6522.RegOrb);
        Assert.That(Flag(Via6522.IfrCa1), Is.True, "ORB does not clear CA1");
        _via.Read(Via6522.RegOra);
        Assert.That(Flag(Via6522.IfrCa1), Is.False, "reading ORA clears CA1");

        _via.Ca1 = true;
        Assert.That(Flag(Via6522.IfrCa1), Is.False, "positive edge is not active");
        _via.Ca1 = false;
        Assert.That(Flag(Via6522.IfrCa1), Is.True);
        _via.Write(Via6522.RegOra, 0);
        Assert.That(Flag(Via6522.IfrCa1), Is.False, "writing ORA clears CA1");
    }

    [Test]
    public void Ca1_PositiveEdge_WhenPcrBit0Set()
    {
        _via.Write(Via6522.RegPcr, 0x01);
        _via.Ca1 = true;
        _via.Ca1 = false;
        Assert.That(Flag(Via6522.IfrCa1), Is.False, "negative edge is not active");
        _via.Ca1 = true;
        Assert.That(Flag(Via6522.IfrCa1), Is.True, "positive edge sets the flag");
    }

    [Test]
    public void Cb1_Edges_AndClearingByOrbAccess()
    {
        _via.Cb1 = true;
        _via.Cb1 = false;
        Assert.That(Flag(Via6522.IfrCb1), Is.True, "negative edge with PCR bit 4 = 0");
        _via.Read(Via6522.RegOra);
        Assert.That(Flag(Via6522.IfrCb1), Is.True, "ORA does not clear CB1");
        _via.Read(Via6522.RegOrb);
        Assert.That(Flag(Via6522.IfrCb1), Is.False, "reading ORB clears CB1");

        _via.Write(Via6522.RegPcr, 0x10);
        _via.Cb1 = true;
        Assert.That(Flag(Via6522.IfrCb1), Is.True, "positive edge with PCR bit 4 = 1");
        _via.Write(Via6522.RegOrb, 0);
        Assert.That(Flag(Via6522.IfrCb1), Is.False, "writing ORB clears CB1");
        _via.Cb1 = false;
        Assert.That(Flag(Via6522.IfrCb1), Is.False);
    }

    // ------------------------------------------------------------------------------------------------
    // CA2 modes
    // ------------------------------------------------------------------------------------------------

    [TestCase(0x00, false, true)]   // 000: input, negative edge, cleared by ORA access
    [TestCase(0x02, false, false)]  // 001: independent, negative edge
    [TestCase(0x04, true, true)]    // 010: input, positive edge
    [TestCase(0x06, true, false)]   // 011: independent, positive edge
    public void Ca2_InputModes(int pcr, bool positiveEdge, bool clearedByPort)
    {
        _via.Write(Via6522.RegPcr, (byte)pcr);
        Assert.That(_via.Ca2Output, Is.True, "input mode: line released");

        _via.Ca2 = true;
        _via.Ca2 = false;
        Assert.That(Flag(Via6522.IfrCa2), Is.EqualTo(!positiveEdge));
        _via.Ca2 = true;
        Assert.That(Flag(Via6522.IfrCa2), Is.True);

        _via.Read(Via6522.RegOra);
        Assert.That(Flag(Via6522.IfrCa2), Is.EqualTo(!clearedByPort), "ORA read");
        _via.Write(Via6522.RegIfr, Via6522.IfrCa2);
        Assert.That(Flag(Via6522.IfrCa2), Is.False, "IFR write always clears");

        _via.Ca2 = positiveEdge ? false : true;
        _via.Ca2 = positiveEdge;
        Assert.That(Flag(Via6522.IfrCa2), Is.True);
        _via.Write(Via6522.RegOra, 0);
        Assert.That(Flag(Via6522.IfrCa2), Is.EqualTo(!clearedByPort), "ORA write");
        _via.Write(Via6522.RegIfr, 0x7F);
        Assert.That(Flag(Via6522.IfrCa2), Is.False);
    }

    [Test]
    public void Ca2_HandshakeOutput()
    {
        int changes = 0;
        _via.Ca2Changed += () => changes++;
        _via.Write(Via6522.RegPcr, 0x08);   // CA2 mode 100
        Assert.That(_via.Ca2Output, Is.True, "idles high");

        _via.Read(Via6522.RegOra);
        Assert.That(_via.Ca2Output, Is.False, "goes low after ORA read");
        Assert.That(changes, Is.EqualTo(1));
        Clock(10);
        Assert.That(_via.Ca2Output, Is.False, "stays low until the CA1 edge");

        _via.Ca1 = true;
        _via.Ca1 = false;                    // active (negative) edge
        Assert.That(_via.Ca2Output, Is.True, "returns high on the CA1 active edge");
        Assert.That(changes, Is.EqualTo(2));

        _via.Write(Via6522.RegOra, 0x55);
        Assert.That(_via.Ca2Output, Is.False, "goes low after ORA write");
        _via.Ca1 = true;
        Assert.That(_via.Ca2Output, Is.False, "inactive edge does not release");
        _via.Ca1 = false;
        Assert.That(_via.Ca2Output, Is.True);
        Assert.That(changes, Is.EqualTo(4));

        _via.Read(Via6522.RegOraNoHandshake);
        Assert.That(_via.Ca2Output, Is.True, "register 15 does not trigger the handshake");
    }

    [Test]
    public void Ca2_PulseOutput_LowForOneCycle()
    {
        _via.Write(Via6522.RegPcr, 0x0A);   // CA2 mode 101
        Assert.That(_via.Ca2Output, Is.True);
        _via.Write(Via6522.RegOra, 0);
        Assert.That(_via.Ca2Output, Is.False, "low right after the access");
        _via.Clock();
        Assert.That(_via.Ca2Output, Is.True, "high again after one cycle");
        _via.Clock();
        Assert.That(_via.Ca2Output, Is.True);

        _via.Read(Via6522.RegOra);
        Assert.That(_via.Ca2Output, Is.False, "read also pulses");
        _via.Clock();
        Assert.That(_via.Ca2Output, Is.True);
    }

    [Test]
    public void Ca2_ManualOutput()
    {
        int changes = 0;
        _via.Ca2Changed += () => changes++;
        _via.Write(Via6522.RegPcr, 0x0C);   // 110: low
        Assert.That(_via.Ca2Output, Is.False);
        Assert.That(changes, Is.EqualTo(1));
        _via.Read(Via6522.RegOra);
        _via.Ca1 = false;
        Assert.That(_via.Ca2Output, Is.False, "port access / CA1 edges do not affect manual output");
        _via.Write(Via6522.RegPcr, 0x0E);   // 111: high
        Assert.That(_via.Ca2Output, Is.True);
        Assert.That(changes, Is.EqualTo(2));
        _via.Write(Via6522.RegPcr, 0x0E);
        Assert.That(changes, Is.EqualTo(2), "no event without a change");
    }

    // ------------------------------------------------------------------------------------------------
    // CB2 modes
    // ------------------------------------------------------------------------------------------------

    [TestCase(0x00, false, true)]
    [TestCase(0x20, false, false)]
    [TestCase(0x40, true, true)]
    [TestCase(0x60, true, false)]
    public void Cb2_InputModes(int pcr, bool positiveEdge, bool clearedByPort)
    {
        _via.Write(Via6522.RegPcr, (byte)pcr);
        _via.Cb2 = true;
        _via.Cb2 = false;
        Assert.That(Flag(Via6522.IfrCb2), Is.EqualTo(!positiveEdge));
        _via.Cb2 = true;
        Assert.That(Flag(Via6522.IfrCb2), Is.True);

        _via.Read(Via6522.RegOra);
        Assert.That(Flag(Via6522.IfrCb2), Is.True, "ORA does not clear CB2");
        _via.Write(Via6522.RegOrb, 0);
        Assert.That(Flag(Via6522.IfrCb2), Is.EqualTo(!clearedByPort), "ORB write");
        _via.Write(Via6522.RegIfr, Via6522.IfrCb2);

        _via.Cb2 = !positiveEdge;
        _via.Cb2 = positiveEdge;
        Assert.That(Flag(Via6522.IfrCb2), Is.True);
        _via.Read(Via6522.RegOrb);
        Assert.That(Flag(Via6522.IfrCb2), Is.EqualTo(!clearedByPort), "ORB read");
    }

    [Test]
    public void Cb2_Handshake_OnOrbWriteOnly_ReleasedByCb1Edge()
    {
        int changes = 0;
        _via.Cb2Changed += () => changes++;
        _via.Write(Via6522.RegPcr, 0x80);   // CB2 mode 100
        Assert.That(_via.Cb2Output, Is.True);

        _via.Read(Via6522.RegOrb);
        Assert.That(_via.Cb2Output, Is.True, "ORB read does not start the CB2 handshake");
        _via.Write(Via6522.RegOrb, 0);
        Assert.That(_via.Cb2Output, Is.False, "ORB write does");
        Assert.That(changes, Is.EqualTo(1));
        Clock(5);
        Assert.That(_via.Cb2Output, Is.False);
        _via.Cb1 = false;                    // active negative edge
        Assert.That(_via.Cb2Output, Is.True);
        Assert.That(changes, Is.EqualTo(2));
    }

    [Test]
    public void Cb2_PulseAndManualOutput()
    {
        _via.Write(Via6522.RegPcr, 0xA0);   // pulse
        _via.Read(Via6522.RegOrb);
        Assert.That(_via.Cb2Output, Is.True, "ORB read does not pulse");
        _via.Write(Via6522.RegOrb, 0);
        Assert.That(_via.Cb2Output, Is.False);
        _via.Clock();
        Assert.That(_via.Cb2Output, Is.True);

        _via.Write(Via6522.RegPcr, 0xC0);   // manual low
        Assert.That(_via.Cb2Output, Is.False);
        _via.Write(Via6522.RegPcr, 0xE0);   // manual high
        Assert.That(_via.Cb2Output, Is.True);
    }

    // ------------------------------------------------------------------------------------------------
    // IFR / IER / IRQ
    // ------------------------------------------------------------------------------------------------

    [Test]
    public void Ier_SetAndClearSemantics()
    {
        _via.Write(Via6522.RegIer, 0x80 | Via6522.IfrT1 | Via6522.IfrCa1);
        Assert.That(_via.Read(Via6522.RegIer), Is.EqualTo(0x80 | Via6522.IfrT1 | Via6522.IfrCa1));
        _via.Write(Via6522.RegIer, 0x80 | Via6522.IfrT2);
        Assert.That(_via.Read(Via6522.RegIer), Is.EqualTo(0x80 | Via6522.IfrT1 | Via6522.IfrCa1 | Via6522.IfrT2), "set adds bits");
        _via.Write(Via6522.RegIer, Via6522.IfrT1);
        Assert.That(_via.Read(Via6522.RegIer), Is.EqualTo(0x80 | Via6522.IfrCa1 | Via6522.IfrT2), "clear removes bits");
        _via.Write(Via6522.RegIer, 0x7F);
        Assert.That(_via.Read(Via6522.RegIer), Is.EqualTo(0x80));
    }

    [Test]
    public void Ifr_Bit7_AndIrqLine_FollowEnabledFlags()
    {
        _via.Ca1 = false;                    // CA1 flag
        Assert.That(_via.Read(Via6522.RegIfr), Is.EqualTo(Via6522.IfrCa1), "bit 7 clear while disabled");
        Assert.That(_via.IrqLine, Is.False);

        _via.Write(Via6522.RegIer, 0x80 | Via6522.IfrT1);
        Assert.That(_via.IrqLine, Is.False, "different flag enabled");

        _via.Write(Via6522.RegIer, 0x80 | Via6522.IfrCa1);
        Assert.That(_via.Read(Via6522.RegIfr), Is.EqualTo(0x80 | Via6522.IfrCa1));
        Assert.That(_via.IrqLine, Is.True);
        Assert.That(_via.Read(Via6522.RegIfr), Is.EqualTo(0x80 | Via6522.IfrCa1), "reading IFR does not clear it");

        _via.Write(Via6522.RegIer, Via6522.IfrCa1);
        Assert.That(_via.IrqLine, Is.False, "disabling the flag drops IRQ");
        Assert.That(Flag(Via6522.IfrCa1), Is.True, "flag itself remains");

        _via.Write(Via6522.RegIer, 0x80 | Via6522.IfrCa1);
        _via.Write(Via6522.RegIfr, 0x80);
        Assert.That(_via.IrqLine, Is.True, "writing bit 7 of IFR has no effect");
        _via.Write(Via6522.RegIfr, Via6522.IfrCa1);
        Assert.That(_via.IrqLine, Is.False);
        Assert.That(_via.Read(Via6522.RegIfr), Is.EqualTo(0));
    }

    [Test]
    public void Irq_FromTimer1_WhenEnabled()
    {
        _via.Write(Via6522.RegIer, 0x80 | Via6522.IfrT1);
        _via.Write(Via6522.RegT1CL, 3);
        _via.Write(Via6522.RegT1CH, 0);
        Clock(4);
        Assert.That(_via.IrqLine, Is.False);
        _via.Clock();
        Assert.That(_via.IrqLine, Is.True);
        _via.Read(Via6522.RegT1CL);
        Assert.That(_via.IrqLine, Is.False);
    }

    // ------------------------------------------------------------------------------------------------
    // Ports
    // ------------------------------------------------------------------------------------------------

    [Test]
    public void PortA_ReadReturnsPinLevels_OutputDrivesOra()
    {
        int changes = 0;
        _via.PortAChanged += () => changes++;
        _via.Write(Via6522.RegDdra, 0x0F);
        _via.Write(Via6522.RegOra, 0x05);
        Assert.That(changes, Is.EqualTo(2));
        Assert.That(_via.PortAOutput, Is.EqualTo(0xF5), "ORA | ~DDRA");
        _portA = 0xA3;
        Assert.That(_via.Read(Via6522.RegOra), Is.EqualTo(0xA1), "(ORA | ~DDRA) & pins");
        Assert.That(_via.Read(Via6522.RegOraNoHandshake), Is.EqualTo(0xA1));

        _via.Write(Via6522.RegOraNoHandshake, 0x0A);
        Assert.That(changes, Is.EqualTo(3));
        Assert.That(_via.PortAOutput, Is.EqualTo(0xFA));
        Assert.That(_via.Read(Via6522.RegOra), Is.EqualTo(0xA2));
    }

    [Test]
    public void PortB_ReadReturnsOrbForOutputs_PinsForInputs()
    {
        int changes = 0;
        _via.PortBChanged += () => changes++;
        _via.Write(Via6522.RegDdrb, 0xF0);
        _via.Write(Via6522.RegOrb, 0x3A);
        Assert.That(changes, Is.EqualTo(2));
        Assert.That(_via.PortBOutput, Is.EqualTo(0x3F), "ORB | ~DDRB");
        _portB = 0x85;
        Assert.That(_via.Read(Via6522.RegOrb), Is.EqualTo(0x35), "ORB for output bits (even though the pin reads differently), pins for inputs");
    }

    [Test]
    public void PortA_InputLatching_OnCa1Edge()
    {
        _portA = 0x11;
        _via.Write(Via6522.RegAcr, 0x01);
        Assert.That(_via.Read(Via6522.RegOra), Is.EqualTo(0x11), "latch primed when latching is enabled");
        _portA = 0x22;
        Assert.That(_via.Read(Via6522.RegOra), Is.EqualTo(0x11), "pin changes are not visible without a CA1 edge");
        _via.Ca1 = false;                    // active edge latches
        Assert.That(_via.Read(Via6522.RegOra), Is.EqualTo(0x22));
        _portA = 0x33;
        _via.Ca1 = true;                     // inactive edge
        Assert.That(_via.Read(Via6522.RegOra), Is.EqualTo(0x22));
        _via.Write(Via6522.RegAcr, 0x00);
        Assert.That(_via.Read(Via6522.RegOra), Is.EqualTo(0x33), "latching disabled: live pins");
    }

    [Test]
    public void PortB_InputLatching_OnCb1Edge()
    {
        _via.Write(Via6522.RegDdrb, 0xF0);
        _via.Write(Via6522.RegOrb, 0x50);
        _portB = 0x01;
        _via.Write(Via6522.RegAcr, 0x02);
        _portB = 0x02;
        Assert.That(_via.Read(Via6522.RegOrb), Is.EqualTo(0x51), "latched inputs, ORB outputs");
        _via.Cb1 = false;
        Assert.That(_via.Read(Via6522.RegOrb), Is.EqualTo(0x52));
        _via.Write(Via6522.RegOrb, 0x60);
        Assert.That(_via.Read(Via6522.RegOrb), Is.EqualTo(0x62), "output bits are not latched");
    }

    // ------------------------------------------------------------------------------------------------
    // Shift register
    // ------------------------------------------------------------------------------------------------

    [Test]
    public void Sr_ShiftOutUnderPhi2_Produces8ClocksOnCb1AndDataOnCb2()
    {
        _via.Write(Via6522.RegAcr, 0x18);   // mode 110
        Assert.That(_via.Cb1Output, Is.True, "clock idles high");
        Clock(4);
        Assert.That(_via.Cb1Output, Is.True, "nothing happens until the SR is written");

        _via.Write(Via6522.RegSr, 0xA5);
        var bits = new List<bool>();
        bool prevCb1 = _via.Cb1Output;
        int cycles = 0;
        while (!Flag(Via6522.IfrSr) && cycles < 100)
        {
            _via.Clock();
            cycles++;
            if (prevCb1 && !_via.Cb1Output)
                bits.Add(_via.Cb2Output);   // data is valid on the falling edge of the shift clock
            prevCb1 = _via.Cb1Output;
        }
        Assert.That(cycles, Is.EqualTo(16), "8 bits at φ2/2");
        Assert.That(bits, Is.EqualTo(new[] { true, false, true, false, false, true, false, true }), "MSB first");
        Assert.That(_via.Cb1Output, Is.True, "clock idles high after the transfer");
        Assert.That(_via.Peek(Via6522.RegSr), Is.EqualTo(0xA5), "bit 7 is rotated back into bit 0");

        Clock(20);
        Assert.That(_via.Cb1Output, Is.True, "shifting stopped");
        Assert.That(_via.Read(Via6522.RegSr), Is.EqualTo(0xA5));
        Assert.That(Flag(Via6522.IfrSr), Is.False, "reading the SR clears the flag");
    }

    [Test]
    public void Sr_ShiftInUnderPhi2_SamplesCb2OnRisingEdge()
    {
        _via.Write(Via6522.RegAcr, 0x08);   // mode 010
        _via.Read(Via6522.RegSr);            // starts shifting
        bool[] data = { true, true, false, false, true, false, true, false };   // 0xCA
        for (int i = 0; i < 8; i++)
        {
            _via.Clock();                    // falling edge
            Assert.That(_via.Cb1Output, Is.False);
            _via.Cb2 = data[i];
            Assert.That(Flag(Via6522.IfrSr), Is.False);
            _via.Clock();                    // rising edge samples CB2
            Assert.That(_via.Cb1Output, Is.True);
        }
        Assert.That(Flag(Via6522.IfrSr), Is.True, "flag after 8 shifts");
        Assert.That(_via.Peek(Via6522.RegSr), Is.EqualTo(0xCA));
        Assert.That(Flag(Via6522.IfrCb2), Is.False, "CB2 edges do not set the CB2 flag while the SR is enabled");
    }

    [Test]
    public void Sr_ShiftInUnderExternalCb1Clock()
    {
        _via.Write(Via6522.RegAcr, 0x0C);   // mode 011
        _via.Read(Via6522.RegSr);
        bool[] data = { false, true, false, true, true, false, false, true };   // 0x59
        for (int i = 0; i < 8; i++)
        {
            _via.Cb1 = false;
            _via.Cb2 = data[i];
            _via.Cb1 = true;                 // rising edge shifts
            Clock(3);                        // φ2 has no influence
        }
        Assert.That(Flag(Via6522.IfrSr), Is.True);
        Assert.That(_via.Peek(Via6522.RegSr), Is.EqualTo(0x59));

        // Further clocks are ignored until the SR is read again.
        _via.Cb1 = false; _via.Cb2 = true; _via.Cb1 = true;
        Assert.That(_via.Peek(Via6522.RegSr), Is.EqualTo(0x59));
    }

    [Test]
    public void Sr_ShiftOutUnderT2_HalfPeriodIsLatchPlus2()
    {
        _via.Write(Via6522.RegT2CL, 2);
        _via.Write(Via6522.RegAcr, 0x14);   // mode 101
        _via.Write(Via6522.RegSr, 0x80);
        Assert.That(_via.Cb2Output, Is.True, "nothing shifted yet");
        Clock(3);
        Assert.That(_via.Cb1Output, Is.True);
        _via.Clock();
        Assert.That(_via.Cb1Output, Is.False, "first falling edge after N+2 cycles");
        Assert.That(_via.Cb2Output, Is.True, "bit 7 = 1 on CB2");
        Clock(3);
        Assert.That(_via.Cb1Output, Is.False);
        _via.Clock();
        Assert.That(_via.Cb1Output, Is.True, "rising edge N+2 cycles later");
        Clock(4);
        Assert.That(_via.Cb2Output, Is.False, "second bit = 0");
        // 8 bits = 16 half periods of 4 cycles = 64 cycles in total; 12 used so far.
        Clock(51);
        Assert.That(Flag(Via6522.IfrSr), Is.False);
        _via.Clock();
        Assert.That(Flag(Via6522.IfrSr), Is.True);
    }

    [Test]
    public void Sr_FreeRunningOutput_NeverSetsFlag()
    {
        _via.Write(Via6522.RegT2CL, 0);
        _via.Write(Via6522.RegAcr, 0x10);   // mode 100
        _via.Write(Via6522.RegSr, 0x0F);
        int fallingEdges = 0;
        bool prev = _via.Cb1Output;
        for (int i = 0; i < 200; i++)
        {
            _via.Clock();
            if (prev && !_via.Cb1Output) fallingEdges++;
            prev = _via.Cb1Output;
        }
        Assert.That(fallingEdges, Is.GreaterThan(16), "keeps shifting");
        Assert.That(Flag(Via6522.IfrSr), Is.False);
        Assert.That(_via.Peek(Via6522.RegSr), Is.AnyOf(0x0F, 0x1E, 0x3C, 0x78, 0xF0, 0xE1, 0xC3, 0x87), "rotates");
    }

    [Test]
    public void Sr_Disabled_DoesNothing()
    {
        _via.Write(Via6522.RegSr, 0xFF);
        Clock(50);
        Assert.That(Flag(Via6522.IfrSr), Is.False);
        Assert.That(_via.Cb1Output, Is.True);
        Assert.That(_via.Read(Via6522.RegSr), Is.EqualTo(0xFF));
    }

    // ------------------------------------------------------------------------------------------------
    // Peek / register 15
    // ------------------------------------------------------------------------------------------------

    [Test]
    public void Peek_HasNoSideEffects()
    {
        _via.Write(Via6522.RegPcr, 0x08);   // CA2 handshake
        _via.Write(Via6522.RegAcr, 0x18);   // SR shift out under φ2
        _via.Ca1 = false;                    // CA1 flag
        _via.Write(Via6522.RegT1CL, 1); _via.Write(Via6522.RegT1CH, 0);
        _via.Write(Via6522.RegT2CL, 1); _via.Write(Via6522.RegT2CH, 0);
        Clock(3);
        Assert.That(Flag(Via6522.IfrT1 | Via6522.IfrT2 | Via6522.IfrCa1), Is.True);
        byte ifr = _via.Peek(Via6522.RegIfr);
        Assert.That(ifr & (Via6522.IfrT1 | Via6522.IfrT2 | Via6522.IfrCa1), Is.EqualTo(Via6522.IfrT1 | Via6522.IfrT2 | Via6522.IfrCa1));

        for (int reg = 0; reg < 16; reg++)
            _via.Peek(reg);

        Assert.That(_via.Peek(Via6522.RegIfr), Is.EqualTo(ifr), "no flags cleared");
        Assert.That(_via.Ca2Output, Is.True, "no handshake triggered");
        Clock(4);
        Assert.That(_via.Cb1Output, Is.True, "no shift started");
        Assert.That(Flag(Via6522.IfrSr), Is.False);

        Assert.That(_via.Peek(Via6522.RegIer), Is.EqualTo(0x80));
        Assert.That(_via.Peek(Via6522.RegAcr), Is.EqualTo(0x18));
        Assert.That(_via.Peek(Via6522.RegPcr), Is.EqualTo(0x08));
    }

    [Test]
    public void Register15_NoHandshake_NoFlagClearing()
    {
        // CA2 as input (mode 000): both CA1 and CA2 flags survive register 15 accesses.
        _via.Ca1 = false;
        _via.Ca2 = false;
        Assert.That(Flag(Via6522.IfrCa1), Is.True);
        Assert.That(Flag(Via6522.IfrCa2), Is.True);

        _via.Read(Via6522.RegOraNoHandshake);
        _via.Write(Via6522.RegOraNoHandshake, 0x00);
        Assert.That(Flag(Via6522.IfrCa1), Is.True);
        Assert.That(Flag(Via6522.IfrCa2), Is.True);

        _via.Read(Via6522.RegOra);
        Assert.That(Flag(Via6522.IfrCa1), Is.False);
        Assert.That(Flag(Via6522.IfrCa2), Is.False);

        // CA2 handshake output: register 15 does not start the handshake, register 1 does.
        _via.Write(Via6522.RegPcr, 0x08);
        _via.Read(Via6522.RegOraNoHandshake);
        _via.Write(Via6522.RegOraNoHandshake, 0x00);
        Assert.That(_via.Ca2Output, Is.True);
        _via.Read(Via6522.RegOra);
        Assert.That(_via.Ca2Output, Is.False);
    }
}
