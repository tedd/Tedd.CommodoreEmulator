using NUnit.Framework;
using Tedd.Commodore.Emulator.Cpu;
using Tedd.Commodore.Tests.Support;

namespace Tedd.Commodore.Tests.Cpu;

/// <summary>
/// Stack pointer wrap-around, JSR/RTS return-address convention (JSR pushes the address of its last byte,
/// RTS adds one; MOS programming manual chapter 8), the JMP (ind) page-wrap bug (64doc.txt), nesting and
/// stack contents after nested interrupts.
/// </summary>
[TestFixture]
public class StackAndJumpTests
{
    private const byte U = Cpu6502.FlagU;
    private const byte NOP = 0xEA;
    private const byte RTS = 0x60;
    private const byte RTI = 0x40;

    private static (Cpu6502 Cpu, RecordingBus Bus) Boot(params int[] code)
    {
        var bus = new RecordingBus();
        for (int i = 0; i < code.Length; i++)
            bus.Ram[0x1000 + i] = (byte)code[i];
        return (bus.CreateCpu(p: U), bus);
    }

    // -----------------------------------------------------------------------------------------------
    // S wraps around inside page 1
    // -----------------------------------------------------------------------------------------------

    [Test]
    public void Push_WrapsFrom00ToFF()
    {
        var (cpu, bus) = Boot(0x48, 0x48);   // PHA, PHA
        cpu.A = 0x5A;
        cpu.S = 0x00;
        cpu.RunInstruction();
        bus.AssertTrace("R 1000 48", "R 1001 48", "W 0100 5A");
        Assert.That(cpu.S, Is.EqualTo(0xFF));
        cpu.A = 0x5B;
        cpu.RunInstruction();
        Assert.That(bus.Ram[0x01FF], Is.EqualTo(0x5B));
        Assert.That(cpu.S, Is.EqualTo(0xFE));
        Assert.That(bus.Ram[0x00FF], Is.EqualTo(0), "nothing leaks into page 0");
    }

    [Test]
    public void Pull_WrapsFromFFTo00()
    {
        var (cpu, bus) = Boot(0x68, NOP);    // PLA
        cpu.S = 0xFF;
        bus.Ram[0x01FF] = 0x99;              // dummy read target
        bus.Ram[0x0100] = 0x5A;
        cpu.RunInstruction();
        bus.AssertTrace("R 1000 68", "R 1001 EA", "R 01FF 99", "R 0100 5A");
        Assert.That(cpu.A, Is.EqualTo(0x5A));
        Assert.That(cpu.S, Is.EqualTo(0x00));
    }

    [Test]
    public void Jsr_AcrossTheStackWrap_RtsReturnsCorrectly()
    {
        var (cpu, bus) = Boot(0x20, 0x00, 0x20, NOP);   // JSR $2000
        bus.Ram[0x2000] = RTS;
        cpu.S = 0x00;
        cpu.RunInstruction();
        Assert.That(cpu.PC, Is.EqualTo(0x2000));
        Assert.That(cpu.S, Is.EqualTo(0xFE));
        Assert.That(bus.Ram[0x0100], Is.EqualTo(0x10), "PCH at $0100");
        Assert.That(bus.Ram[0x01FF], Is.EqualTo(0x02), "PCL at $01FF");
        bus.Clear();
        cpu.RunInstruction();       // RTS
        bus.AssertTrace("R 2000 60", "R 2001 00", "R 01FE 00", "R 01FF 02", "R 0100 10", "R 1002 20");
        Assert.That(cpu.PC, Is.EqualTo(0x1003));
        Assert.That(cpu.S, Is.EqualTo(0x00));
    }

    [Test]
    public void Brk_AcrossTheStackWrap()
    {
        var (cpu, bus) = Boot(0x00, 0x77);
        bus.Ram[0xFFFE] = 0x00; bus.Ram[0xFFFF] = 0x20;
        cpu.S = 0x01;
        cpu.RunInstruction();
        bus.AssertTrace("R 1000 00", "R 1001 77", "W 0101 10", "W 0100 02", "W 01FF 30", "R FFFE 00", "R FFFF 20");
        Assert.That(cpu.S, Is.EqualTo(0xFE));
    }

    // -----------------------------------------------------------------------------------------------
    // JSR / RTS convention
    // -----------------------------------------------------------------------------------------------

    [Test]
    public void Jsr_PushesAddressOfItsLastByte()
    {
        var (cpu, bus) = Boot(NOP, 0x20, 0x34, 0x12, NOP);   // JSR at $1001, last byte at $1003
        cpu.RunInstruction();
        cpu.RunInstruction();
        Assert.That(cpu.PC, Is.EqualTo(0x1234));
        Assert.That(bus.Ram[0x01FD], Is.EqualTo(0x10), "PCH");
        Assert.That(bus.Ram[0x01FC], Is.EqualTo(0x03), "PCL = address of the last byte of JSR");
        Assert.That(cpu.S, Is.EqualTo(0xFB));
    }

    [Test]
    public void Rts_AddsOneToThePulledAddress()
    {
        var (cpu, bus) = Boot(RTS);
        cpu.S = 0xFB;
        bus.Ram[0x01FC] = 0xFF;
        bus.Ram[0x01FD] = 0x12;     // $12FF + 1 = $1300: the increment carries into the high byte
        cpu.RunInstruction();
        Assert.That(cpu.PC, Is.EqualTo(0x1300));
        Assert.That(cpu.S, Is.EqualTo(0xFD));
    }

    [Test]
    public void Rts_WithoutJsr_ReturnsToPushedAddressPlusOne_ViaPha()
    {
        // The classic "push return address then RTS" idiom: PHA high, PHA low, RTS -> address + 1.
        var (cpu, _) = Boot(0xA9, 0x20, 0x48, 0xA9, 0xFF, 0x48, RTS);   // LDA #$20; PHA; LDA #$FF; PHA; RTS
        cpu.RunInstructions(5);
        Assert.That(cpu.PC, Is.EqualTo(0x2100));
        Assert.That(cpu.S, Is.EqualTo(0xFD));
    }

    [Test]
    public void JsrRts_RoundTrip_ResumesAfterJsr()
    {
        var (cpu, bus) = Boot(0x20, 0x00, 0x20, 0xA9, 0x42);   // JSR $2000; LDA #$42
        bus.Ram[0x2000] = 0xA2; bus.Ram[0x2001] = 0x07;         // LDX #$07
        bus.Ram[0x2002] = RTS;
        cpu.RunInstructions(4);
        Assert.That(cpu.PC, Is.EqualTo(0x1005));
        Assert.That(cpu.X, Is.EqualTo(0x07));
        Assert.That(cpu.A, Is.EqualTo(0x42));
        Assert.That(cpu.S, Is.EqualTo(0xFD));
        Assert.That(cpu.Cycles, Is.EqualTo(6 + 2 + 6 + 2));
    }

    // -----------------------------------------------------------------------------------------------
    // JMP
    // -----------------------------------------------------------------------------------------------

    [Test]
    public void JmpIndirect_ThroughXXFF_TakesHighByteFromXX00()
    {
        var (cpu, bus) = Boot(0x6C, 0xFF, 0x10);   // JMP ($10FF)
        bus.Ram[0x10FF] = 0x34;
        bus.Ram[0x1000] = 0x6C;                     // (the opcode itself is the wrongly used high byte)
        bus.Ram[0x1100] = 0x12;                     // never used
        cpu.RunInstruction();
        Assert.That(cpu.PC, Is.EqualTo(0x6C34));
        bus.AssertTrace("R 1000 6C", "R 1001 FF", "R 1002 10", "R 10FF 34", "R 1000 6C");
    }

    [Test]
    public void JmpIndirect_NotAtPageEnd_Normal()
    {
        var (cpu, bus) = Boot(0x6C, 0xFE, 0x10);   // JMP ($10FE)
        bus.Ram[0x10FE] = 0x34;
        bus.Ram[0x10FF] = 0x12;
        cpu.RunInstruction();
        Assert.That(cpu.PC, Is.EqualTo(0x1234));
        Assert.That(cpu.Cycles, Is.EqualTo(5));
    }

    [Test]
    public void JmpAbsolute_ToItself_Loops()
    {
        var (cpu, _) = Boot(0x4C, 0x00, 0x10);
        cpu.RunInstructions(10);
        Assert.That(cpu.PC, Is.EqualTo(0x1000));
        Assert.That(cpu.Cycles, Is.EqualTo(30));
    }

    // -----------------------------------------------------------------------------------------------
    // Nesting
    // -----------------------------------------------------------------------------------------------

    [Test]
    public void NestedSubroutines_ThreeDeep_StackContentsAndUnwinding()
    {
        var (cpu, bus) = Boot(0x20, 0x00, 0x20, NOP);      // $1000 JSR $2000 ; $1003 NOP
        bus.Ram[0x2000] = 0x20; bus.Ram[0x2001] = 0x00; bus.Ram[0x2002] = 0x21;   // $2000 JSR $2100
        bus.Ram[0x2003] = RTS;
        bus.Ram[0x2100] = 0x20; bus.Ram[0x2101] = 0x00; bus.Ram[0x2102] = 0x22;   // $2100 JSR $2200
        bus.Ram[0x2103] = RTS;
        bus.Ram[0x2200] = RTS;

        cpu.RunInstructions(3);
        Assert.That(cpu.PC, Is.EqualTo(0x2200));
        Assert.That(cpu.S, Is.EqualTo(0xF7));
        Assert.That(bus.Ram[0x01FD], Is.EqualTo(0x10)); Assert.That(bus.Ram[0x01FC], Is.EqualTo(0x02));
        Assert.That(bus.Ram[0x01FB], Is.EqualTo(0x20)); Assert.That(bus.Ram[0x01FA], Is.EqualTo(0x02));
        Assert.That(bus.Ram[0x01F9], Is.EqualTo(0x21)); Assert.That(bus.Ram[0x01F8], Is.EqualTo(0x02));

        cpu.RunInstruction();
        Assert.That((cpu.PC, cpu.S), Is.EqualTo(((ushort)0x2103, (byte)0xF9)));
        cpu.RunInstruction();
        Assert.That((cpu.PC, cpu.S), Is.EqualTo(((ushort)0x2003, (byte)0xFB)));
        cpu.RunInstruction();
        Assert.That((cpu.PC, cpu.S), Is.EqualTo(((ushort)0x1003, (byte)0xFD)));
    }

    [Test]
    public void Recursion_128Levels_WrapsTheStackPointerAndUnwinds()
    {
        // $1000: DEX ; BEQ +3 ; JSR $1000 ; RTS   -> recurses X times, S moves 2*X bytes.
        var (cpu, bus) = Boot(0xCA, 0xF0, 0x03, 0x20, 0x00, 0x10, RTS);
        cpu.X = 128;
        cpu.S = 0xFD;
        // DEX runs 128 times, the JSR 127 times (the last DEX makes BEQ skip it): 254 bytes are pushed,
        // $01FD down to $0100, so S wraps from $00 to $FF.
        int guard = 0;
        while (!(cpu.X == 0 && cpu.PC == 0x1006) && guard++ < 10_000)
            cpu.RunInstruction();
        Assert.That(cpu.X, Is.EqualTo(0));
        Assert.That(cpu.S, Is.EqualTo(0xFF), "127 return addresses on the stack, S wrapped once");
        Assert.That(bus.Ram[0x0100], Is.EqualTo(0x05), "innermost PCL at $0100");
        Assert.That(bus.Ram[0x0101], Is.EqualTo(0x10), "innermost PCH at $0101");

        // Unwind: every RTS returns to $1006 (the pushed $1005 + 1) until S is back at $FD.
        guard = 0;
        while (cpu.S != 0xFD && guard++ < 10_000)
            cpu.RunInstruction();
        Assert.That(cpu.S, Is.EqualTo(0xFD));
        Assert.That(cpu.PC, Is.EqualTo(0x1006), "back at the outermost RTS");
    }

    [Test]
    public void NestedInterrupts_StackHoldsBothFrames()
    {
        var (cpu, bus) = Boot(NOP, NOP, NOP);
        bus.Ram[0xFFFE] = 0x00; bus.Ram[0xFFFF] = 0x20;     // IRQ -> $2000: NOP ; RTI
        bus.Ram[0xFFFA] = 0x00; bus.Ram[0xFFFB] = 0x30;     // NMI -> $3000: RTI
        bus.Ram[0x2000] = NOP; bus.Ram[0x2001] = RTI;
        bus.Ram[0x3000] = RTI;
        cpu.P = U | Cpu6502.FlagC;

        cpu.Irq = true;
        cpu.RunInstruction();           // NOP at $1000
        cpu.RunInstruction();           // IRQ
        cpu.Irq = false;
        Assert.That((cpu.PC, cpu.S), Is.EqualTo(((ushort)0x2000, (byte)0xFA)));
        Assert.That(bus.Ram[0x01FD], Is.EqualTo(0x10));
        Assert.That(bus.Ram[0x01FC], Is.EqualTo(0x01));
        Assert.That(bus.Ram[0x01FB], Is.EqualTo(U | Cpu6502.FlagC), "P of the interrupted code: I clear, B clear");

        cpu.Nmi = true;
        cpu.RunInstruction();           // NOP at $2000
        cpu.RunInstruction();           // NMI
        Assert.That((cpu.PC, cpu.S), Is.EqualTo(((ushort)0x3000, (byte)0xF7)));
        Assert.That(bus.Ram[0x01FA], Is.EqualTo(0x20));
        Assert.That(bus.Ram[0x01F9], Is.EqualTo(0x01));
        Assert.That(bus.Ram[0x01F8], Is.EqualTo(U | Cpu6502.FlagC | Cpu6502.FlagI), "P inside the IRQ handler: I set");

        cpu.RunInstruction();           // RTI from NMI
        Assert.That((cpu.PC, cpu.S), Is.EqualTo(((ushort)0x2001, (byte)0xFA)));
        Assert.That(cpu.FlagInterrupt, Is.True);
        cpu.RunInstruction();           // RTI from IRQ
        Assert.That((cpu.PC, cpu.S), Is.EqualTo(((ushort)0x1001, (byte)0xFD)));
        Assert.That(cpu.P, Is.EqualTo(U | Cpu6502.FlagC));
        cpu.RunInstructions(2);
        Assert.That(cpu.PC, Is.EqualTo(0x1003), "no spurious interrupts afterwards");
    }

    [Test]
    public void BrkInsideSubroutine_UnwindsInOrder()
    {
        var (cpu, bus) = Boot(0x20, 0x00, 0x20, NOP);      // JSR $2000
        bus.Ram[0x2000] = 0x00; bus.Ram[0x2001] = 0x77;     // BRK + signature byte
        bus.Ram[0x2002] = RTS;
        bus.Ram[0xFFFE] = 0x00; bus.Ram[0xFFFF] = 0x30;
        bus.Ram[0x3000] = RTI;
        cpu.RunInstruction();           // JSR
        cpu.RunInstruction();           // BRK
        Assert.That((cpu.PC, cpu.S), Is.EqualTo(((ushort)0x3000, (byte)0xF8)));
        Assert.That(bus.Ram[0x01FB], Is.EqualTo(0x20)); Assert.That(bus.Ram[0x01FA], Is.EqualTo(0x02));
        Assert.That(bus.Ram[0x01F9], Is.EqualTo(0x30), "P with B set");
        cpu.RunInstruction();           // RTI -> $2002 (skips the signature byte)
        Assert.That((cpu.PC, cpu.S), Is.EqualTo(((ushort)0x2002, (byte)0xFB)));
        Assert.That(cpu.FlagInterrupt, Is.False, "RTI restored the pre-BRK P");
        cpu.RunInstruction();           // RTS -> $1003
        Assert.That((cpu.PC, cpu.S), Is.EqualTo(((ushort)0x1003, (byte)0xFD)));
    }
}
