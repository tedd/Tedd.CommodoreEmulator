using System;
using NUnit.Framework;
using Tedd.Commodore.Emulator.C128;
using Tedd.Commodore.Emulator.C64;
using Tedd.Commodore.Emulator.Tools;
using Tedd.Commodore.Hosting;
using Tedd.Commodore.Tests.Support;

namespace Tedd.Commodore.Tests.Machine;

/// <summary>
/// Devices in the expansion port (<see cref="ExpansionDevice"/>): I/O 1 / I/O 2 reads and writes, lines and ROM
/// windows the device changes while it runs, the IRQ/NMI lines and the reset line. Synthetic ROMs only.
/// </summary>
[TestFixture]
public class ExpansionDeviceTests
{
    private static C64Memory Fresh()
    {
        var m = new C64Memory(C64MemoryTests.FakeRoms());
        for (int i = 0; i < 0x10000; i++) m.Ram[i] = 0x11;
        return m;
    }

    private static void SetPort(C64Memory m, int lines)
    {
        m.Write(0, 0x2F);
        m.Write(1, (byte)(0x30 | (lines & 7)));
    }

    [Test]
    public void Io1_And_Io2_Reach_The_Device()
    {
        var m = Fresh();
        var d = new TestExpansionDevice();
        m.Expansion = d;

        m.Write(0xDF10, 0x5A);
        Assert.That(d.LastWrite, Is.EqualTo(((ushort)0xDF10, (byte)0x5A)), "the device gets the full address");
        Assert.That(d.IoRam[0x10], Is.EqualTo(0x5A));
        Assert.That(m.Read(0xDF10), Is.EqualTo(0x5A));
        Assert.That(m.Ram[0xDF10], Is.EqualTo(0x11), "I/O writes do not reach the RAM underneath");

        d.Input = 0x42;
        Assert.That(m.Read(0xDE04), Is.EqualTo(0x42));
        m.Read(0xC000);
        Assert.That(m.Read(0xDE10), Is.EqualTo(0x11), "an address the device does not answer is open bus");

        m.Expansion = null;
        m.Write(0xDF11, 0x77);
        Assert.That(d.IoRam[0x11], Is.EqualTo(0), "unplugged");
        m.Read(0xC000);
        Assert.That(m.Read(0xDF10), Is.EqualTo(0x11), "an empty port is open bus");
    }

    [Test]
    public void Peek_Does_Not_Cause_Read_Side_Effects()
    {
        var m = Fresh();
        var d = new TestExpansionDevice();
        m.Expansion = d;
        Assert.That(m.Read(0xDE03), Is.EqualTo(1));
        Assert.That(m.Peek(0xDE03), Is.EqualTo(1));
        Assert.That(m.Peek(0xDE03), Is.EqualTo(1));
        Assert.That(m.Read(0xDE03), Is.EqualTo(2));
        Assert.That(d.ReadCount, Is.EqualTo(2));
    }

    [Test]
    public void Io_Pages_Belong_To_The_Device_Only_While_Io_Is_Mapped()
    {
        var m = Fresh();
        var d = new TestExpansionDevice();
        m.Expansion = d;
        SetPort(m, 3);   // CHAREN = 0: the character ROM at $D000
        Assert.That(m.Read(0xDE03), Is.EqualTo(0xCC));
        m.Write(0xDF00, 0x99);
        Assert.That(d.WriteCount, Is.EqualTo(0));
        Assert.That(d.ReadCount, Is.EqualTo(0));
        Assert.That(m.Ram[0xDF00], Is.EqualTo(0x99), "the write goes to RAM under the character ROM");
    }

    [Test]
    public void The_Map_Follows_The_Lines_And_Windows_The_Device_Sets()
    {
        var m = Fresh();
        var d = new TestExpansionDevice();
        m.Expansion = d;
        Assert.That(m.Read(0x8000), Is.EqualTo(0x11), "lines high: nothing mapped");
        Assert.That(m.Read(0xA000), Is.EqualTo(0xBA), "BASIC");

        m.Write(0xDE01, 0x02);   // EXROM low, GAME high: 8K configuration
        Assert.That(m.Read(0x8000), Is.EqualTo(0x80), "bank 0 at ROML");
        Assert.That(m.Read(0xA000), Is.EqualTo(0xBA), "BASIC stays");
        m.Write(0xDE00, 2);
        Assert.That(m.Read(0x9FFF), Is.EqualTo(0x82), "bank 2");
        d.Banks[2 * ExpansionDevice.WindowSize + 0x123] = 0x5C;
        Assert.That(m.Read(0x8123), Is.EqualTo(0x5C), "the CPU reads the device's array directly");
        m.Write(0x8123, 0x33);
        Assert.That(m.Ram[0x8123], Is.EqualTo(0x33), "writes go to the RAM under ROML");
        Assert.That(d.Banks[2 * ExpansionDevice.WindowSize + 0x123], Is.EqualTo(0x5C));

        m.Write(0xDE01, 0x01);   // EXROM high, GAME low: Ultimax
        Assert.That(m.Read(0x8000), Is.EqualTo(0x82), "ROML");
        m.Read(0x0800);
        Assert.That(m.Read(0x2000), Is.EqualTo(0x11), "open: last bus value");
        m.Ram[0x0800] = 0x44;
        m.Read(0x0800);
        Assert.That(m.Read(0xE000), Is.EqualTo(0x44), "no ROMH: $E000 is open bus");

        m.Write(0xDE01, 0x03);
        Assert.That(m.Read(0x8123), Is.EqualTo(0x33), "lines high again: RAM");
        Assert.That(m.Read(0xE000), Is.EqualTo(0xEA), "KERNAL");
    }

    [Test]
    public void A_Selected_Window_Without_Data_Is_Open_Bus()
    {
        var m = Fresh();
        var d = new TestExpansionDevice();
        m.Expansion = d;
        d.Map(null);
        m.Write(0xDE01, 0x00);   // EXROM and GAME low: 16K configuration, but neither window has data
        m.Ram[0x0800] = 0x66;
        m.Read(0x0800);
        Assert.That(m.Read(0x8000), Is.EqualTo(0x66));
        m.Read(0x0800);
        Assert.That(m.Read(0xA000), Is.EqualTo(0x66));
    }

    [Test]
    public void Windows_Must_Fit_In_Their_Array()
    {
        var d = new TestExpansionDevice();
        Assert.Throws<ArgumentOutOfRangeException>(() => d.Map(new byte[8192], 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => d.Map(d.Banks, 3 * ExpansionDevice.WindowSize + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => d.Map(d.Banks, -1));
        d.Map(d.Banks, 3 * ExpansionDevice.WindowSize);
        Assert.That(d.RomLOffset, Is.EqualTo(3 * ExpansionDevice.WindowSize));
    }

    [Test]
    public void An_Unplugged_Device_No_Longer_Changes_The_Map()
    {
        var m = Fresh();
        var first = new TestExpansionDevice();
        var second = new TestExpansionDevice();
        m.Expansion = first;
        m.Expansion = second;
        first.WriteIo(0xDE01, 0x02);   // the unplugged device switches to 8K
        Assert.That(m.Read(0x8000), Is.EqualTo(0x11));
        second.WriteIo(0xDE01, 0x02);
        Assert.That(m.Read(0x8000), Is.EqualTo(0x80));
    }

    [Test]
    public void One_Cartridge_Can_Be_Plugged_Into_Two_Machines()
    {
        var romL = new byte[8192];
        Array.Fill(romL, (byte)0x8A);
        var cart = new Cartridge(romL, null, exrom: false, game: true, "shared");
        var a = Fresh();
        var b = Fresh();
        a.Expansion = cart;
        b.Expansion = cart;
        Assert.That(a.Read(0x8000), Is.EqualTo(0x8A));
        Assert.That(b.Read(0x8000), Is.EqualTo(0x8A));
    }

    // A KERNAL that enables interrupts and has the device raise an IRQ; the IRQ handler counts at $0400, releases the
    // IRQ and has the device raise an NMI; the NMI handler counts at $0401 and releases it.
    private static RomSet InterruptRoms()
    {
        var program = Assembler.Assemble("""
                    * = $E000
            reset:  sei
                    ldx #$FF
                    txs
                    cli
                    lda #$01        ; device: assert IRQ
                    sta $DE02
            wait:   jmp wait
            irq:    inc $0400
                    lda #$00        ; release IRQ
                    sta $DE02
                    lda #$02        ; assert NMI
                    sta $DE02
                    rti
            nmi:    inc $0401
                    lda #$00        ; release NMI
                    sta $DE02
                    rti

                    * = $FFFA
                    .word nmi, reset, irq
            """);
        var kernal = new byte[RomSet.KernalSize];
        program.CopyTo((address, value) => kernal[address - 0xE000] = value);
        return new RomSet(new byte[RomSet.BasicSize], kernal, new byte[RomSet.CharSize], null, ".", "interrupt test");
    }

    [Test]
    public void The_Device_Irq_And_Nmi_Lines_Reach_The_Cpu()
    {
        var c64 = new C64(InterruptRoms());
        var d = new TestExpansionDevice();
        c64.AttachExpansion(d);
        Assert.That(c64.Expansion, Is.SameAs(d));
        Assert.That(c64.Memory.Expansion, Is.SameAs(d));
        c64.RunCycles(2000);
        Assert.That(c64.Ram[0x0400], Is.EqualTo(1), "one IRQ, released by the handler");
        Assert.That(c64.Ram[0x0401], Is.EqualTo(1), "one NMI");
        Assert.That(d.Irq || d.Nmi, Is.False);
    }

    [Test]
    public void A_Held_Irq_Keeps_Interrupting_And_A_Held_Nmi_Fires_Once()
    {
        var c64 = new C64(InterruptRoms());
        var d = new HeldLines();
        c64.AttachExpansion(d);
        c64.RunCycles(100);
        c64.Ram[0x0400] = c64.Ram[0x0401] = 0;
        d.Hold(irq: false, nmi: true);
        c64.RunCycles(2000);
        Assert.That(c64.Ram[0x0401], Is.EqualTo(1), "NMI is edge triggered");
        d.Hold(irq: true, nmi: false);
        c64.RunCycles(2000);
        Assert.That(c64.Ram[0x0400], Is.GreaterThan(10), "IRQ is level triggered");
    }

    /// <summary>Lines that only the test changes (the program's writes to $DE02 are ignored).</summary>
    private sealed class HeldLines : ExpansionDevice
    {
        public HeldLines() : base("held lines") { }

        public void Hold(bool irq, bool nmi)
        {
            Irq = irq;
            Nmi = nmi;
        }
    }

    [Test]
    public void The_Reset_Line_Resets_The_Device_Before_The_Map_Is_Rebuilt()
    {
        var c64 = new C64(InterruptRoms());
        var d = new TestExpansionDevice();
        c64.AttachExpansion(d);
        Assert.That(d.Resets, Is.EqualTo(0), "plugging in does not reset");
        c64.Memory.Write(0xDE01, 0x02);
        Assert.That(c64.PeekMemory(0x8000), Is.EqualTo(0x80));
        c64.Reset(hard: false);
        Assert.That((d.Resets, d.LastResetHard), Is.EqualTo((1, (bool?)false)));
        Assert.That(c64.PeekMemory(0x8000), Is.EqualTo(c64.Ram[0x8000]), "the device's reset released EXROM");
        c64.Reset(hard: true);
        Assert.That((d.Resets, d.LastResetHard), Is.EqualTo((2, (bool?)true)));
    }

    [Test]
    public void The_Session_Plugs_Devices_In_With_Or_Without_A_Reset()
    {
        var session = new EmulatorSession(InterruptRoms(), attachDrive: false);
        var d = new TestExpansionDevice();
        session.AttachExpansion(d);
        Assert.That(session.Expansion, Is.SameAs(d));
        Assert.That(d.Resets, Is.EqualTo(0));
        Assert.That(session.MediaDescription, Is.EqualTo("Cartridge: test device"));

        session.AttachExpansion(d, reset: true);
        Assert.That(d.Resets, Is.EqualTo(1));

        session.AttachExpansion(null);
        Assert.That(session.Expansion, Is.Null);
        Assert.That(session.MediaDescription, Is.Empty);
    }

    [Test]
    public void The_C128_Has_The_Same_Port()
    {
        var m = new C128Memory(C128MemoryTests.FakeRoms());
        var d = new TestExpansionDevice();
        m.Expansion = d;

        // I/O 1 / I/O 2 in C128 mode
        m.Write(0xDF20, 0x3C);
        Assert.That(d.IoRam[0x20], Is.EqualTo(0x3C));
        Assert.That(m.Read(0xDF20), Is.EqualTo(0x3C));
        Assert.That(m.Peek(0xDE03), Is.EqualTo(0));

        // The MCR reads the lines (bit 4 GAME, bit 5 EXROM; 1 = high)
        Assert.That(m.Peek(0xD505) & 0x30, Is.EqualTo(0x30));
        m.Write(0xDE01, 0x02);
        Assert.That(m.Peek(0xD505) & 0x30, Is.EqualTo(0x10), "EXROM low");

        // External function ROM (CR bits 2-3 = 10): ROML at $8000 and, without ROMH, again at $A000
        m.Write(0xFF00, 0x08);
        m.Write(0xDE00, 1);
        Assert.That(m.Peek(0x8000), Is.EqualTo(0x81));
        Assert.That(m.Peek(0xA000), Is.EqualTo(0x81));
        m.Write(0xDE00, 3);
        Assert.That(m.Peek(0x9FFF), Is.EqualTo(0x83), "bank switch rebuilds the page tables");

        // C64 mode: the PLA maps ROML from the lines
        m.Write(0xFF00, 0x00);
        m.Write(0xD505, 0x40);
        Assert.That(m.Peek(0x8000), Is.EqualTo(0x83), "8K configuration");
        m.Write(0xDE01, 0x03);
        Assert.That(m.Peek(0x8000), Is.EqualTo(m.Ram[0x8000]), "lines released: RAM");
    }
}
