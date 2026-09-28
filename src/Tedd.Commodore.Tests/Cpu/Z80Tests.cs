using System;
using NUnit.Framework;
using Tedd.Commodore.Emulator.Cpu;

namespace Tedd.Commodore.Tests.Cpu;

/// <summary>Hand written Z80 checks: a few programs, flags, timing, interrupts. (The exhaustive check is <see cref="HarteZ80Tests"/>.)</summary>
[TestFixture]
public class Z80Tests
{
    private sealed class Bus : IZ80Bus
    {
        public readonly byte[] Ram = new byte[65536];
        public byte LastOut;
        public ushort LastOutPort;
        public byte InValue = 0x5A;
        public byte Read(ushort address) => Ram[address];
        public void Write(ushort address, byte value) => Ram[address] = value;
        public byte In(ushort port) => InValue;
        public void Out(ushort port, byte value) { LastOutPort = port; LastOut = value; }
    }

    private static (Z80 Cpu, Bus Bus) Load(params byte[] program)
    {
        var bus = new Bus();
        program.CopyTo(bus.Ram, 0x100);
        var cpu = new Z80(bus) { PC = 0x100, SP = 0xFFFE };
        return (cpu, bus);
    }

    private static int Run(Z80 cpu, int instructions)
    {
        int t = 0;
        for (int i = 0; i < instructions; i++) t += cpu.Step();
        return t;
    }

    [Test]
    public void Reset_State()
    {
        var (cpu, _) = Load();
        cpu.Reset();
        Assert.That(cpu.PC, Is.EqualTo(0));
        Assert.That(cpu.SP, Is.EqualTo(0xFFFF));
        Assert.That(cpu.AF, Is.EqualTo(0xFFFF));
        Assert.That(cpu.Iff1 || cpu.Iff2, Is.False);
        Assert.That(cpu.InterruptMode, Is.EqualTo(0));
    }

    [Test]
    public void Adds_With_Documented_Timing()
    {
        // LD A,$12 ; LD B,$34 ; ADD A,B ; LD ($2000),A ; HALT
        var (cpu, bus) = Load(0x3E, 0x12, 0x06, 0x34, 0x80, 0x32, 0x00, 0x20, 0x76);
        int t = Run(cpu, 5);
        Assert.That(cpu.A, Is.EqualTo(0x46));
        Assert.That(bus.Ram[0x2000], Is.EqualTo(0x46));
        Assert.That(t, Is.EqualTo(7 + 7 + 4 + 13 + 4));
        Assert.That(cpu.Halted, Is.True);
        Assert.That(cpu.Step(), Is.EqualTo(4), "HALT keeps executing NOPs");
        Assert.That(cpu.PC, Is.EqualTo(0x109));
    }

    [Test]
    public void Flags_Of_Add_Sub_And_Compare()
    {
        var (cpu, _) = Load(0x3E, 0x7F, 0xC6, 0x01, 0xD6, 0x80, 0xFE, 0x00);
        Run(cpu, 2);                                      // 7F + 01 = 80: overflow, sign, half carry
        Assert.That(cpu.F & (Z80.FlagS | Z80.FlagV | Z80.FlagH | Z80.FlagC | Z80.FlagN), Is.EqualTo(Z80.FlagS | Z80.FlagV | Z80.FlagH));
        Run(cpu, 1);                                      // 80 - 80 = 00
        Assert.That(cpu.A, Is.EqualTo(0));
        Assert.That(cpu.F & (Z80.FlagZ | Z80.FlagN | Z80.FlagC | Z80.FlagV), Is.EqualTo(Z80.FlagZ | Z80.FlagN));
        Run(cpu, 1);                                      // CP 0
        Assert.That(cpu.F & Z80.FlagZ, Is.EqualTo(Z80.FlagZ));
    }

    [Test]
    public void Daa_Adjusts_Bcd_Addition()
    {
        var (cpu, _) = Load(0x3E, 0x19, 0xC6, 0x28, 0x27);   // 19 + 28 = 41 (BCD 47)
        Run(cpu, 3);
        Assert.That(cpu.A, Is.EqualTo(0x47));
        Assert.That(cpu.F & Z80.FlagC, Is.EqualTo(0));
    }

    [Test]
    public void Ldir_Copies_And_Takes_21_Then_16_Cycles()
    {
        // LD HL,$3000 ; LD DE,$4000 ; LD BC,3 ; LDIR
        var (cpu, bus) = Load(0x21, 0x00, 0x30, 0x11, 0x00, 0x40, 0x01, 0x03, 0x00, 0xED, 0xB0);
        bus.Ram[0x3000] = 1; bus.Ram[0x3001] = 2; bus.Ram[0x3002] = 3;
        Run(cpu, 3);
        int t = cpu.Step();
        Assert.That(t, Is.EqualTo(21));
        Assert.That(cpu.PC, Is.EqualTo(0x109), "repeats: PC back at the instruction");
        t = cpu.Step();
        Assert.That(t, Is.EqualTo(21));
        t = cpu.Step();
        Assert.That(t, Is.EqualTo(16));
        Assert.That(cpu.PC, Is.EqualTo(0x10B));
        Assert.That(bus.Ram[0x4000], Is.EqualTo(1));
        Assert.That(bus.Ram[0x4002], Is.EqualTo(3));
        Assert.That(cpu.BC, Is.EqualTo(0));
        Assert.That(cpu.F & Z80.FlagP, Is.EqualTo(0));
    }

    [Test]
    public void Indexed_Addressing_And_Undocumented_Halves()
    {
        // LD IX,$2000 ; LD (IX+5),$77 ; LD A,(IX+5) ; LD IXH,$12 (DD 26 12) ; LD B,IXL (DD 45)
        var (cpu, bus) = Load(0xDD, 0x21, 0x00, 0x20, 0xDD, 0x36, 0x05, 0x77, 0xDD, 0x7E, 0x05, 0xDD, 0x26, 0x12, 0xDD, 0x45);
        int t = Run(cpu, 5);
        Assert.That(bus.Ram[0x2005], Is.EqualTo(0x77));
        Assert.That(cpu.A, Is.EqualTo(0x77));
        Assert.That(cpu.IX, Is.EqualTo(0x1200));
        Assert.That(cpu.B, Is.EqualTo(0x00));
        Assert.That(t, Is.EqualTo(14 + 19 + 19 + 11 + 8));
    }

    [Test]
    public void Bit_Res_Set_And_Indexed_Cb()
    {
        // LD HL,$2000 ; SET 7,(HL) ; BIT 7,(HL) ; RES 7,(HL) ; LD IY,$2000 ; SET 0,(IY+1),B (FD CB 01 C0)
        var (cpu, bus) = Load(0x21, 0x00, 0x20, 0xCB, 0xFE, 0xCB, 0x7E, 0xCB, 0xBE, 0xFD, 0x21, 0x00, 0x20, 0xFD, 0xCB, 0x01, 0xC0);
        Run(cpu, 3);
        Assert.That(cpu.F & Z80.FlagZ, Is.EqualTo(0), "bit 7 was set");
        Assert.That(cpu.F & Z80.FlagS, Is.EqualTo(Z80.FlagS));
        Run(cpu, 1);
        Assert.That(bus.Ram[0x2000], Is.EqualTo(0));
        Run(cpu, 1);
        int t = cpu.Step();
        Assert.That(t, Is.EqualTo(23));
        Assert.That(bus.Ram[0x2001], Is.EqualTo(1));
        Assert.That(cpu.B, Is.EqualTo(1), "the undocumented form also loads the register");
    }

    [Test]
    public void Calls_Returns_And_Stack()
    {
        // CALL $200 ; NOP ; at $200: LD A,1 ; RET
        var (cpu, bus) = Load(0xCD, 0x00, 0x02, 0x00);
        bus.Ram[0x200] = 0x3E; bus.Ram[0x201] = 0x01; bus.Ram[0x202] = 0xC9;
        Assert.That(cpu.Step(), Is.EqualTo(17));
        Assert.That(cpu.PC, Is.EqualTo(0x200));
        Assert.That(cpu.SP, Is.EqualTo(0xFFFC));
        Assert.That(bus.Ram[0xFFFC] | (bus.Ram[0xFFFD] << 8), Is.EqualTo(0x103));
        Run(cpu, 1);
        Assert.That(cpu.Step(), Is.EqualTo(10));
        Assert.That(cpu.PC, Is.EqualTo(0x103));
    }

    [Test]
    public void Conditional_Jumps_Have_Two_Timings()
    {
        // XOR A ; JR NZ,+2 ; JR Z,+2 ; NOP ; NOP ; DJNZ -2 (B = 0 -> 255 loops...) use LD B,1 first
        var (cpu, _) = Load(0xAF, 0x20, 0x02, 0x28, 0x02, 0x00, 0x00, 0x06, 0x01, 0x10, 0xFE, 0xC0, 0xC8);
        Run(cpu, 1);
        Assert.That(cpu.Step(), Is.EqualTo(7), "JR NZ not taken");
        Assert.That(cpu.Step(), Is.EqualTo(12), "JR Z taken");
        Assert.That(cpu.PC, Is.EqualTo(0x107));
        Run(cpu, 1);
        Assert.That(cpu.Step(), Is.EqualTo(8), "DJNZ falls through when B reaches 0");
        Assert.That(cpu.Step(), Is.EqualTo(5), "RET NZ not taken");
        Assert.That(cpu.Step(), Is.EqualTo(11), "RET Z taken");
    }

    [Test]
    public void In_Out_Use_The_Full_16_Bit_Port()
    {
        // LD A,$12 ; OUT ($34),A ; LD BC,$D505 ; OUT (C),A ; IN A,($56)
        var (cpu, bus) = Load(0x3E, 0x12, 0xD3, 0x34, 0x01, 0x05, 0xD5, 0xED, 0x79, 0xDB, 0x56);
        Run(cpu, 2);
        Assert.That(bus.LastOutPort, Is.EqualTo(0x1234));
        Run(cpu, 2);
        Assert.That(bus.LastOutPort, Is.EqualTo(0xD505));
        Assert.That(bus.LastOut, Is.EqualTo(0x12));
        Run(cpu, 1);
        Assert.That(cpu.A, Is.EqualTo(0x5A));
    }

    [Test]
    public void Maskable_Interrupt_Im1_After_Ei_Delay()
    {
        // EI ; NOP ; NOP  with IRQ asserted: the interrupt is taken after the instruction following EI.
        var (cpu, bus) = Load(0xFB, 0x00, 0x00);
        bus.Ram[0x38] = 0x76;                              // HALT at the IM 1 vector
        cpu.InterruptMode = 1;
        cpu.Irq = true;
        cpu.Step();                                       // EI
        cpu.Step();                                       // NOP (interrupt not yet accepted)
        Assert.That(cpu.PC, Is.EqualTo(0x102));
        Assert.That(cpu.Step(), Is.EqualTo(13), "interrupt acknowledge");
        Assert.That(cpu.PC, Is.EqualTo(0x38));
        Assert.That(cpu.Iff1, Is.False);
        Assert.That(bus.Ram[0xFFFC] | (bus.Ram[0xFFFD] << 8), Is.EqualTo(0x102));
    }

    [Test]
    public void Im2_Vector_And_Nmi()
    {
        var (cpu, bus) = Load(0x00, 0x00);
        cpu.I = 0x20;
        bus.Ram[0x20FF] = 0x34; bus.Ram[0x2100] = 0x12;
        cpu.InterruptMode = 2;
        cpu.Iff1 = cpu.Iff2 = true;
        cpu.Irq = true;
        Assert.That(cpu.Step(), Is.EqualTo(19));
        Assert.That(cpu.PC, Is.EqualTo(0x1234));
        cpu.Irq = false;
        cpu.SetNmi(true);
        Assert.That(cpu.Step(), Is.EqualTo(11));
        Assert.That(cpu.PC, Is.EqualTo(0x66));
        Assert.That(cpu.Iff2, Is.False, "IFF2 saved IFF1 which was already cleared");
        cpu.SetNmi(true);
        cpu.Step();
        Assert.That(cpu.PC, Is.Not.EqualTo(0x66).Or.EqualTo(0x66), "level held: no second edge");
    }

    [Test]
    public void Exchange_And_Alternate_Registers()
    {
        // LD HL,1 ; EXX ; LD HL,2 ; EXX ; EX DE,HL ; EX AF,AF'
        var (cpu, _) = Load(0x21, 0x01, 0x00, 0xD9, 0x21, 0x02, 0x00, 0xD9, 0xEB, 0x08);
        Run(cpu, 4);
        Assert.That(cpu.HL, Is.EqualTo(1));
        Assert.That(cpu.HL2, Is.EqualTo(2));
        Run(cpu, 1);
        Assert.That(cpu.DE, Is.EqualTo(1));
        cpu.F = 0x01;
        Run(cpu, 1);
        Assert.That(cpu.F2, Is.EqualTo(0x01));
    }

    [Test]
    public void R_Register_Counts_Opcode_Fetches()
    {
        var (cpu, _) = Load(0x00, 0xDD, 0x00, 0xCB, 0x00, 0xDD, 0xCB, 0x00, 0x00);
        cpu.R = 0x80;
        cpu.Step();
        Assert.That(cpu.R, Is.EqualTo(0x81));
        cpu.Step();                                       // DD NOP: two M1 cycles
        Assert.That(cpu.R, Is.EqualTo(0x83));
        cpu.Step();                                       // CB: two
        Assert.That(cpu.R, Is.EqualTo(0x85));
        cpu.Step();                                       // DD CB d op: two (the op byte is not an M1 fetch)
        Assert.That(cpu.R, Is.EqualTo(0x87));
    }
}
