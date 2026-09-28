using System.Linq;
using NUnit.Framework;
using Tedd.Commodore.Emulator.Cpu;
using Tedd.Commodore.Tests.Support;

namespace Tedd.Commodore.Tests.Cpu;

/// <summary>
/// Programs written in assembly, assembled with <see cref="Emulator.Tools.Assembler"/> and executed on the
/// cycle-stepped core. Expected cycle counts are derived from the per-instruction tables in 64doc.txt
/// (John West / Marko Mäkelä):
/// <code>
///   implied/accumulator 2   immediate 2   zp 3   zp,X/Y 4   abs 4   abs,X/Y read 4 (+1 on page cross)
///   abs,X/Y write 5   (zp,X) 6   (zp),Y read 5 (+1 on page cross)   (zp),Y write 6
///   RMW: zp 5, zp,X 6, abs 6, abs,X 7     JMP abs 3   JMP (ind) 5   JSR 6   RTS 6   RTI 6
///   PHA/PHP 3   PLA/PLP 4   BRK 7   IRQ/NMI sequence 7
///   branch: 2 not taken, 3 taken to the same page, 4 taken across a page boundary
/// </code>
/// </summary>
[TestFixture]
public class AssemblyProgramTests
{
    private const string Trap = "done:   jmp done\n";

    // ------------------------------------------------------------------ arithmetic

    [Test]
    public void Multiply8x8_ShiftAndAdd()
    {
        var host = CpuTestHost.FromAssembly("""
            ; $10 = multiplier, $11 = multiplicand, result in $20/$21 (little endian)
            start:  lda #0
                    sta $20
                    sta $21
                    sta $13         ; high byte of the shifted multiplicand
                    ldx #8
            loop:   lsr $10
                    bcc noadd
                    clc
                    lda $20
                    adc $11
                    sta $20
                    lda $21
                    adc $13
                    sta $21
            noadd:  asl $11
                    rol $13
                    dex
                    bne loop
            done:   jmp done
            """);
        host.Ram[0x10] = 200;
        host.Ram[0x11] = 123;
        host.RunUntilLabel("done");
        int product = host.Ram[0x20] | (host.Ram[0x21] << 8);
        Assert.That(product, Is.EqualTo(200 * 123));
    }

    [TestCase(0xFFFF, 0x0001, 0x0000, 1)]
    [TestCase(0x1234, 0x0FFF, 0x2233, 0)]
    [TestCase(0x00FF, 0x0001, 0x0100, 0)]
    public void Add16WithCarry(int a, int b, int expectedSum, int expectedCarry)
    {
        var host = CpuTestHost.FromAssembly("""
            start:  clc
                    lda $10
                    adc $12
                    sta $14
                    lda $11
                    adc $13
                    sta $15
                    lda #0
                    adc #0          ; capture the final carry
                    sta $16
            done:   jmp done
            """);
        host.Ram[0x10] = (byte)a;
        host.Ram[0x11] = (byte)(a >> 8);
        host.Ram[0x12] = (byte)b;
        host.Ram[0x13] = (byte)(b >> 8);
        int cycles = host.RunUntilLabel("done");
        Assert.That(host.Ram[0x14] | (host.Ram[0x15] << 8), Is.EqualTo(expectedSum));
        Assert.That(host.Ram[0x16], Is.EqualTo(expectedCarry));
        // clc 2 + lda zp 3 + adc zp 3 + sta zp 3 + lda 3 + adc 3 + sta 3 + lda # 2 + adc # 2 + sta 3 = 27 (64doc)
        Assert.That(cycles, Is.EqualTo(27));
        Assert.That(host.Cpu.Cycles, Is.EqualTo(27));
    }

    [TestCase(0x1234, 0x0FFF, 0x0235, 1)]
    [TestCase(0x0000, 0x0001, 0xFFFF, 0)]   // borrow -> carry clear
    public void Sub16WithBorrow(int a, int b, int expectedDiff, int expectedCarry)
    {
        var host = CpuTestHost.FromAssembly("""
            start:  sec
                    lda $10
                    sbc $12
                    sta $14
                    lda $11
                    sbc $13
                    sta $15
                    php
                    pla
                    and #1
                    sta $16
            done:   jmp done
            """);
        host.Ram[0x10] = (byte)a;
        host.Ram[0x11] = (byte)(a >> 8);
        host.Ram[0x12] = (byte)b;
        host.Ram[0x13] = (byte)(b >> 8);
        host.RunUntilLabel("done");
        Assert.That(host.Ram[0x14] | (host.Ram[0x15] << 8), Is.EqualTo(expectedDiff));
        Assert.That(host.Ram[0x16], Is.EqualTo(expectedCarry));
    }

    [Test]
    public void DecimalMode_Add()
    {
        var host = CpuTestHost.FromAssembly("""
            start:  sed
                    clc
                    lda #$19
                    adc #$28        ; 19 + 28 = 47 (BCD)
                    sta $20
                    php
                    pla
                    sta $21
                    clc
                    lda #$99
                    adc #$01        ; 99 + 01 = 00 with carry
                    sta $22
                    php
                    pla
                    sta $23
                    sec
                    lda #$45
                    adc #$45        ; 45 + 45 + 1 = 91
                    sta $24
                    cld
            done:   jmp done
            """);
        host.RunUntilLabel("done");
        Assert.That(host.Ram[0x20], Is.EqualTo(0x47));
        Assert.That(host.Ram[0x21] & Cpu6502.FlagC, Is.EqualTo(0), "19+28 does not carry");
        Assert.That(host.Ram[0x21] & Cpu6502.FlagD, Is.Not.EqualTo(0), "D flag is pushed");
        Assert.That(host.Ram[0x22], Is.EqualTo(0x00));
        Assert.That(host.Ram[0x23] & Cpu6502.FlagC, Is.Not.EqualTo(0), "99+01 carries");
        Assert.That(host.Ram[0x24], Is.EqualTo(0x91));
        Assert.That(host.Cpu.FlagDecimal, Is.False);
    }

    [Test]
    public void DecimalMode_Subtract()
    {
        var host = CpuTestHost.FromAssembly("""
            start:  sed
                    sec
                    lda #$50
                    sbc #$25        ; 50 - 25 = 25
                    sta $20
                    php
                    pla
                    sta $21
                    sec
                    lda #$00
                    sbc #$01        ; 00 - 01 = 99 with borrow (carry clear)
                    sta $22
                    php
                    pla
                    sta $23
                    clc
                    lda #$46
                    sbc #$12        ; 46 - 12 - 1 = 33
                    sta $24
                    cld
            done:   jmp done
            """);
        host.RunUntilLabel("done");
        Assert.That(host.Ram[0x20], Is.EqualTo(0x25));
        Assert.That(host.Ram[0x21] & Cpu6502.FlagC, Is.Not.EqualTo(0), "no borrow");
        Assert.That(host.Ram[0x22], Is.EqualTo(0x99));
        Assert.That(host.Ram[0x23] & Cpu6502.FlagC, Is.EqualTo(0), "borrow clears carry");
        Assert.That(host.Ram[0x24], Is.EqualTo(0x33));
    }

    /// <summary>
    /// NMOS decimal mode quirks (64doc.txt "Decimal mode"): the Z flag is computed from the binary result, so
    /// $99 + $01 gives A = $00 with Z clear (binary $9A); N comes from the intermediate result. The D flag is
    /// not cleared by BRK/IRQ on the NMOS 6502 (only the 65C02 does that) and is restored by RTI.
    /// </summary>
    [Test]
    public void DecimalFlag_Behaviour()
    {
        var host = CpuTestHost.FromAssembly("""
            start:  sed
                    clc
                    lda #$99
                    adc #$01
                    php
                    pla
                    sta $20         ; flags: Z must be clear (binary result $9A), C set
                    clc
                    lda #$79
                    adc #$01        ; $80: N set (intermediate result)
                    php
                    pla
                    sta $21
                    brk             ; D stays set through the interrupt on NMOS
                    .byte 0
                    php
                    pla
                    sta $23         ; after RTI: D restored (still set)
                    cld
                    php
                    pla
                    sta $24         ; D clear
            done:   jmp done
            brkh:   php
                    pla
                    sta $22         ; D still set inside the handler
                    rti
                    * = $fffe
                    .word brkh
            """);
        host.RunUntilLabel("done");
        Assert.That(host.Ram[0x20] & Cpu6502.FlagZ, Is.EqualTo(0), "Z from binary result");
        Assert.That(host.Ram[0x20] & Cpu6502.FlagC, Is.Not.EqualTo(0));
        Assert.That(host.Ram[0x21] & Cpu6502.FlagN, Is.Not.EqualTo(0), "N from intermediate result");
        Assert.That(host.Ram[0x22] & Cpu6502.FlagD, Is.Not.EqualTo(0), "D not cleared by BRK");
        Assert.That(host.Ram[0x23] & Cpu6502.FlagD, Is.Not.EqualTo(0), "D restored by RTI");
        Assert.That(host.Ram[0x24] & Cpu6502.FlagD, Is.EqualTo(0), "CLD clears D");
    }

    // ------------------------------------------------------------------ loops with exact cycle counts

    private const string CopyLoop = """
                * = $1000
        start:  ldx #0          ; 2
        loop:   lda table,x     ; 4 (+1 when table+X crosses a page)
                sta $2000,x     ; 5 (abs,X write is always 5)
                inx             ; 2
                cpx #32         ; 2
                bne loop        ; 3 taken (same page), 2 on the final fall-through
        done:   jmp done
        """;

    // ldx 2 + 32 iterations * (4 + 5 + 2 + 2 + 3) - 1 (last BNE not taken) = 513
    private const int CopyLoopCycles = 2 + 32 * 16 - 1;

    [Test]
    public void Loop_ExactCycleCount_NoPageCrossing()
    {
        var host = CpuTestHost.FromAssembly(CopyLoop + "\n        * = $1100\ntable:  .res 32, $aa\n");
        Assert.That(host.Label("table"), Is.EqualTo(0x1100));
        int cycles = host.RunUntilLabel("done");
        Assert.That(cycles, Is.EqualTo(CopyLoopCycles));
        Assert.That(host.Cpu.Cycles, Is.EqualTo(CopyLoopCycles));
        Assert.That(host.Ram.Skip(0x2000).Take(32).All(b => b == 0xAA), Is.True);
    }

    [Test]
    public void Loop_ExactCycleCount_WithPageCrossing()
    {
        // table at $10F0: table+X crosses into $1100 for X = 16..31 -> 16 extra cycles (64doc: abs,X read +1).
        var host = CpuTestHost.FromAssembly(CopyLoop + "\n        * = $10f0\ntable:  .res 32, $bb\n");
        Assert.That(host.Label("table"), Is.EqualTo(0x10F0));
        int cycles = host.RunUntilLabel("done");
        Assert.That(cycles, Is.EqualTo(CopyLoopCycles + 16));
        Assert.That(host.Cpu.Cycles, Is.EqualTo(CopyLoopCycles + 16));
        Assert.That(host.Ram.Skip(0x2000).Take(32).All(b => b == 0xBB), Is.True);

        // The extra cycle is a dummy read from the un-fixed address (high byte not yet incremented):
        // for X = 16 the CPU reads $1000 before reading $1100 (64doc, "Absolute indexed addressing, read").
        var reads = host.Trace.Where(t => !t.IsWrite && t.Address == 0x1000).ToList();
        Assert.That(reads, Is.Not.Empty, "dummy read at $1000 expected during the page-crossing access");
    }

    [Test]
    public void Branch_TakenAcrossPage_Costs4Cycles()
    {
        var host = CpuTestHost.FromAssembly("""
                    * = $10f0
            start:  clc             ; 2
                    bcc t1          ; 3: taken, $10F3 -> $10F5 stays on the page
                    nop
                    nop
            t1:     .res 8, $ea     ; 8 NOPs = 16 cycles, $10F5..$10FC
                    bcc t2          ; at $10FD: next PC $10FF, target $1101 -> page cross -> 4 cycles
                    nop
                    nop
            t2:     bcs t3          ; not taken: 2 cycles
            t3:
            done:   jmp done
            """);
        Assert.That(host.Label("t2"), Is.EqualTo(0x1101));
        int cycles = host.RunUntilLabel("done");
        Assert.That(cycles, Is.EqualTo(2 + 3 + 16 + 4 + 2));
    }

    // ------------------------------------------------------------------ stack and subroutines

    [Test]
    public void JsrRts_Nesting()
    {
        var host = CpuTestHost.FromAssembly("""
            start:  ldx #$ff
                    txs
                    lda #0
                    jsr sub1
                    sta $20
            done:   jmp done
            sub1:   jsr sub2
                    clc
                    adc #1
                    rts
            sub2:   jsr sub3
                    clc
                    adc #10
                    rts
            sub3:   clc
                    adc #100
                    rts
            """);
        int cycles = host.RunUntilLabel("done");
        Assert.That(host.Ram[0x20], Is.EqualTo(111));
        Assert.That(host.Cpu.S, Is.EqualTo(0xFF));
        // ldx 2, txs 2, lda 2, jsr 6, jsr 6, jsr 6, (clc 2, adc 2, rts 6) x3, sta 3 = 57 (64doc)
        Assert.That(cycles, Is.EqualTo(57));
        // JSR pushes PC of its last byte (return address - 1): sub1's return address is "sta $20" - 1.
        ushort ret = (ushort)(host.Label("done") - 2 - 1);
        Assert.That(host.Ram[0x1FF], Is.EqualTo(ret >> 8));
        Assert.That(host.Ram[0x1FE], Is.EqualTo(ret & 0xFF));
    }

    [Test]
    public void PhaPla_Stack()
    {
        var host = CpuTestHost.FromAssembly("""
            start:  ldx #$ff
                    txs
                    lda #$11
                    pha
                    lda #$22
                    pha
                    lda #$80
                    pha
                    lda #0
                    pla             ; $80 -> N set
                    php
                    sta $20
                    pla             ; discard P
                    pla             ; $22
                    sta $21
                    pla             ; $11
                    sta $22
                    tsx
                    stx $23
            done:   jmp done
            """);
        host.RunUntilLabel("done");
        Assert.That(host.Ram[0x20], Is.EqualTo(0x80));
        Assert.That(host.Ram[0x21], Is.EqualTo(0x22));
        Assert.That(host.Ram[0x22], Is.EqualTo(0x11));
        Assert.That(host.Ram[0x23], Is.EqualTo(0xFF), "stack pointer back at $FF");
        Assert.That(host.Ram[0x1FF], Is.EqualTo(0x11));
        Assert.That(host.Ram[0x1FE], Is.EqualTo(0x22));
        // $01FD held the $80 until the PHP (S was $FD again after the PLA) overwrote it with P.
        Assert.That(host.Ram[0x1FD] & Cpu6502.FlagN, Is.Not.EqualTo(0), "PLA sets N from the pulled value");
        Assert.That(host.Ram[0x1FD] & Cpu6502.FlagZ, Is.EqualTo(0));
        Assert.That(host.Ram[0x1FD] & Cpu6502.FlagB, Is.Not.EqualTo(0), "PHP pushes B set");
        var pushes = host.Trace.Where(t => t.IsWrite && t.Address == 0x1FD).Select(t => t.Value).ToArray();
        Assert.That(pushes[0], Is.EqualTo(0x80), "first push to $01FD was the PHA of $80");

        // PHA is 3 cycles, PLA is 4 (64doc).
        var pha = CpuTestHost.FromAssembly("start: pha\n pla\ndone: jmp done");
        Assert.That(pha.RunInstructions(1), Is.EqualTo(3));
        Assert.That(pha.RunInstructions(1), Is.EqualTo(4));
    }

    // ------------------------------------------------------------------ indirect addressing

    [Test]
    public void IndirectY_LinkedListWalk()
    {
        var host = CpuTestHost.FromAssembly("""
            ptr = $fb
            start:  lda #<node1
                    sta ptr
                    lda #>node1
                    sta ptr+1
                    lda #0
                    sta $20
                    sta $21
            walk:   inc $21         ; node count
                    ldy #2
                    lda (ptr),y     ; value
                    clc
                    adc $20
                    sta $20
                    ldy #0
                    lda (ptr),y     ; next low
                    tax
                    iny
                    lda (ptr),y     ; next high (0 terminates: all nodes live in page $10+)
                    sta ptr+1
                    stx ptr
                    bne walk
            done:   jmp done
            node1:  .word node2
                    .byte 10
            node2:  .word node3
                    .byte 20
            node3:  .word 0
                    .byte 30
            """);
        host.RunUntilLabel("done");
        Assert.That(host.Ram[0x20], Is.EqualTo(60));
        Assert.That(host.Ram[0x21], Is.EqualTo(3));
    }

    [Test]
    public void IndirectX_PointerTable()
    {
        var host = CpuTestHost.FromAssembly("""
                    * = $40
                    .word $1200, $1300, $1400
                    * = $1000
            start:  ldx #2
                    lda ($40,x)     ; pointer at $42/$43 -> $1300
                    sta $20
                    ldx #4
                    lda ($40,x)     ; pointer at $44/$45 -> $1400
                    sta $21
                    ldx #0
                    sta ($40,x)     ; store through $40/$41 -> $1200
            done:   jmp done
                    * = $1200
                    .byte $11
                    * = $1300
                    .byte $22
                    * = $1400
                    .byte $33
            """);
        Assert.That(host.Cpu.PC, Is.EqualTo(0x1000), "entry is the 'start' label, not the lowest origin");
        int cycles = host.RunUntilLabel("done");
        Assert.That(host.Ram[0x20], Is.EqualTo(0x22));
        Assert.That(host.Ram[0x21], Is.EqualTo(0x33));
        Assert.That(host.Ram[0x1200], Is.EqualTo(0x33));
        // ldx 2, lda (zp,X) 6, sta 3, ldx 2, lda 6, sta 3, ldx 2, sta (zp,X) 6 = 30 (64doc)
        Assert.That(cycles, Is.EqualTo(30));
    }

    // ------------------------------------------------------------------ strings

    [Test]
    public void StringCopy_ZeroTerminated()
    {
        var host = CpuTestHost.FromAssembly("""
            start:  ldy #0
            copy:   lda src,y
                    sta dst,y
                    beq done
                    iny
                    bne copy
            done:   jmp done
            src:    .text "HELLO, WORLD", 0
            dst:    .res 16, $ff
            """);
        host.RunUntilLabel("done");
        ushort dst = host.Label("dst");
        var copied = host.Ram.Skip(dst).Take(13).Select(b => (char)b).ToArray();
        Assert.That(new string(copied), Is.EqualTo("HELLO, WORLD\0"));
        Assert.That(host.Ram[dst + 13], Is.EqualTo(0xFF), "bytes after the terminator are untouched");
        Assert.That(host.Cpu.Y, Is.EqualTo(12));
    }

    [Test]
    public void StringCompare()
    {
        var host = CpuTestHost.FromAssembly("""
            start:  ldx #0
            c1:     lda s1,x
                    cmp s2,x
                    bne d1
                    tay             ; Z <- terminator?
                    beq e1
                    inx
                    bne c1
            d1:     lda #1
                    sta $20
                    jmp part2
            e1:     lda #0
                    sta $20
            part2:  ldx #0
            c2:     lda s1,x
                    cmp s3,x
                    bne d2
                    tay
                    beq e2
                    inx
                    bne c2
            d2:     stx $22         ; index of the first difference
                    lda #1
                    sta $21
                    jmp done
            e2:     lda #0
                    sta $21
            done:   jmp done
            s1:     .text "ABCDEF", 0
            s2:     .text "ABCDEF", 0
            s3:     .text "ABCXEF", 0
            """);
        host.RunUntilLabel("done");
        Assert.That(host.Ram[0x20], Is.EqualTo(0), "s1 == s2");
        Assert.That(host.Ram[0x21], Is.EqualTo(1), "s1 != s3");
        Assert.That(host.Ram[0x22], Is.EqualTo(3), "difference at index 3");
    }

    // ------------------------------------------------------------------ bit manipulation

    [Test]
    public void BitManipulation_PopcountReverseAndBit()
    {
        var host = CpuTestHost.FromAssembly("""
            start:  lda #0
                    sta $20         ; popcount
                    sta $21         ; bit-reversed
                    ldx #8
            next:   lsr $10         ; bit 0 -> C
                    bcc nobit
                    inc $20
            nobit:  rol $21         ; C -> bit 0 (INC/BCC leave C alone)
                    dex
                    bne next
                    lda #$ff
                    bit $11         ; $11 = $C0: N=1 V=1, Z=0
                    php
                    pla
                    sta $22
                    lda #$3f
                    bit $11         ; A & $C0 = 0 -> Z=1, N/V still from memory
                    php
                    pla
                    sta $23
                    lda $12
                    and #$0f        ; mask
                    ora #$50        ; set
                    eor #$ff        ; invert
                    sta $24
            done:   jmp done
            """);
        host.Ram[0x10] = 0b1011_0000;
        host.Ram[0x11] = 0xC0;
        host.Ram[0x12] = 0xA7;
        host.RunUntilLabel("done");
        Assert.That(host.Ram[0x20], Is.EqualTo(3));
        Assert.That(host.Ram[0x21], Is.EqualTo(0b0000_1101));
        Assert.That(host.Ram[0x22] & (Cpu6502.FlagN | Cpu6502.FlagV | Cpu6502.FlagZ), Is.EqualTo(Cpu6502.FlagN | Cpu6502.FlagV));
        Assert.That(host.Ram[0x23] & (Cpu6502.FlagN | Cpu6502.FlagV | Cpu6502.FlagZ), Is.EqualTo(Cpu6502.FlagN | Cpu6502.FlagV | Cpu6502.FlagZ));
        Assert.That(host.Ram[0x24], Is.EqualTo(unchecked((byte)~((0xA7 & 0x0F) | 0x50))));
    }

    // ------------------------------------------------------------------ self-modifying code

    [Test]
    public void SelfModifyingCode_WriteOpcodeThenExecuteIt()
    {
        var host = CpuTestHost.FromAssembly("""
            start:  ldx #0
                    lda #$e8        ; INX
                    sta target
                    jsr target      ; executes INX
                    lda #$ca        ; DEX
                    sta target
                    jsr target
                    jsr target      ; X = 0 + 1 - 1 - 1 = $FF
                    stx $20
                    lda #$a9        ; LDA #imm: patch the operand of an instruction
                    sta imm
                    lda #$77
                    sta imm+1
            imm:    nop
                    nop
                    sta $21
            done:   jmp done
            target: nop
                    rts
            """);
        host.RunUntilLabel("done");
        Assert.That(host.Ram[0x20], Is.EqualTo(0xFF));
        Assert.That(host.Ram[0x21], Is.EqualTo(0x77));
        Assert.That(host.Ram[host.Label("target")], Is.EqualTo(0xCA));
        // The opcode fetch at 'target' saw the patched byte: it appears as a read of $E8 and later $CA.
        ushort target = host.Label("target");
        var fetches = host.Trace.Where(t => !t.IsWrite && t.Address == target).Select(t => t.Value).ToList();
        Assert.That(fetches, Is.EqualTo(new byte[] { 0xE8, 0xCA, 0xCA }));
    }

    // ------------------------------------------------------------------ interrupts

    [Test]
    public void IrqHandler_PushesStateAndReturns()
    {
        var host = CpuTestHost.FromAssembly("""
                    * = $1000
            start:  ldx #$ff
                    txs
                    lda #0
                    sta $20         ; main loop counter
                    sta $21         ; irq counter
                    cli
            main:   inc $20         ; 5 cycles (zp RMW)
                    jmp main        ; 3 cycles
            irq:    pha
                    inc $21
                    pla
                    rti
                    * = $fffe
                    .word irq
            """);
        ushort main = host.Label("main");
        ushort irq = host.Label("irq");
        host.RunUntilLabel("main");
        host.RunInstructions(4);
        host.RunUntilLabel("main");
        Assert.That(host.Cpu.FlagInterrupt, Is.False);
        byte pBefore = host.Cpu.P;
        long cyclesBefore = host.Cpu.Cycles;

        // Assert IRQ at an instruction boundary: the next instruction (inc $20, 5 cycles) completes, then the
        // 7-cycle interrupt sequence runs (64doc: the line is sampled during the last cycle of the instruction).
        host.Cpu.Irq = true;
        int cycles = host.RunUntilLabel("irq");
        Assert.That(cycles, Is.EqualTo(5 + 7));
        Assert.That(host.Cpu.Cycles - cyclesBefore, Is.EqualTo(12));
        host.Cpu.Irq = false; // the device acknowledges

        // Pushed: PCH, PCL of the "jmp main" (main + 2), then P with B clear and bit 5 set.
        ushort ret = (ushort)(main + 2);
        Assert.That(host.Cpu.S, Is.EqualTo(0xFC));
        Assert.That(host.Ram[0x1FF], Is.EqualTo(ret >> 8));
        Assert.That(host.Ram[0x1FE], Is.EqualTo(ret & 0xFF));
        Assert.That(host.Ram[0x1FD] & Cpu6502.FlagB, Is.EqualTo(0), "IRQ pushes B clear");
        Assert.That(host.Ram[0x1FD] & Cpu6502.FlagU, Is.Not.EqualTo(0));
        Assert.That(host.Ram[0x1FD] & ~(Cpu6502.FlagB | Cpu6502.FlagU), Is.EqualTo(pBefore & ~(Cpu6502.FlagB | Cpu6502.FlagU)));
        Assert.That(host.Cpu.FlagInterrupt, Is.True, "I is set while in the handler");
        Assert.That(host.Cpu.PC, Is.EqualTo(irq));

        // Handler runs, RTI returns to "jmp main" with I cleared again, and the main loop continues.
        byte mainCount = host.Ram[0x20];
        host.RunUntilPc(ret);
        Assert.That(host.Cpu.FlagInterrupt, Is.False);
        Assert.That(host.Cpu.S, Is.EqualTo(0xFF));
        Assert.That(host.Ram[0x21], Is.EqualTo(1));
        host.RunUntil(c => host.Ram[0x20] == (byte)(mainCount + 3), 1000);
        Assert.That(host.Ram[0x21], Is.EqualTo(1), "no second interrupt without the line being asserted");
    }

    [Test]
    public void IrqIsMaskedWhileIFlagIsSet()
    {
        var host = CpuTestHost.FromAssembly("""
            start:  sei
                    lda #0
                    sta $21
            main:   inc $20
                    jmp main
            irq:    inc $21
                    rti
                    * = $fffe
                    .word irq
            """);
        host.RunUntilLabel("main");
        host.Cpu.Irq = true;
        host.RunInstructions(20);
        Assert.That(host.Ram[0x21], Is.EqualTo(0), "masked");
        Assert.That(host.Cpu.PC, Is.AnyOf(host.Label("main"), host.Label("main") + 2));
    }

    [Test]
    public void NmiHandler_EdgeTriggered()
    {
        var host = CpuTestHost.FromAssembly("""
                    * = $1000
            start:  ldx #$ff
                    txs
                    lda #0
                    sta $20
                    sta $21
                    sei             ; NMI ignores the I flag
            main:   inc $20         ; 5
                    jmp main        ; 3
            nmi:    inc $21
                    rti
                    * = $fffa
                    .word nmi
            """);
        ushort main = host.Label("main");
        ushort nmi = host.Label("nmi");
        host.RunUntilLabel("main");
        long before = host.Cpu.Cycles;

        host.Cpu.Nmi = true;                        // rising edge
        int cycles = host.RunUntilLabel("nmi");
        Assert.That(cycles, Is.EqualTo(5 + 7), "current instruction completes, then the 7 cycle sequence");
        Assert.That(host.Cpu.Cycles - before, Is.EqualTo(12));
        ushort ret = (ushort)(main + 2);
        Assert.That(host.Ram[0x1FF], Is.EqualTo(ret >> 8));
        Assert.That(host.Ram[0x1FE], Is.EqualTo(ret & 0xFF));
        Assert.That(host.Ram[0x1FD] & Cpu6502.FlagB, Is.EqualTo(0), "NMI pushes B clear");
        Assert.That(host.Ram[0x1FD] & Cpu6502.FlagI, Is.Not.EqualTo(0), "I was set and is pushed as such");
        Assert.That(host.Cpu.PC, Is.EqualTo(nmi));

        // Holding the line asserted does not retrigger (edge sensitive).
        host.RunInstructions(40);
        Assert.That(host.Ram[0x21], Is.EqualTo(1));

        // Release and assert again: a new edge, a second interrupt.
        host.Cpu.Nmi = false;
        host.RunInstructions(4);
        host.Cpu.Nmi = true;
        host.RunUntilLabel("nmi");
        host.RunInstructions(2);
        Assert.That(host.Ram[0x21], Is.EqualTo(2));
        Assert.That(host.Cpu.S, Is.EqualTo(0xFF));
    }

    [Test]
    public void BrkHandler_PushesBFlagAndReturnsPastPaddingByte()
    {
        var host = CpuTestHost.FromAssembly("""
            start:  ldx #$ff
                    txs
                    lda #0
                    sta $21
            brkins: brk
                    .byte $ea       ; padding byte skipped by RTI (BRK pushes PC + 2)
            after:  lda #1
                    sta $20
            done:   jmp done
            brkh:   inc $21
                    tsx
                    lda $0103,x     ; the pushed P
                    sta $22
                    rti
                    * = $fffe
                    .word brkh
            """);
        host.RunUntilLabel("brkins");
        byte pBefore = host.Cpu.P;
        int cycles = host.RunUntilLabel("brkh");
        Assert.That(cycles, Is.EqualTo(7), "BRK takes 7 cycles (64doc)");
        ushort after = host.Label("after");
        Assert.That(host.Cpu.S, Is.EqualTo(0xFC));
        Assert.That(host.Ram[0x1FF], Is.EqualTo(after >> 8));
        Assert.That(host.Ram[0x1FE], Is.EqualTo(after & 0xFF));
        Assert.That(host.Ram[0x1FD], Is.EqualTo((byte)(pBefore | Cpu6502.FlagB | Cpu6502.FlagU)), "BRK pushes P with B and bit 5 set");
        Assert.That(host.Cpu.FlagInterrupt, Is.True);

        host.RunUntilLabel("done");
        Assert.That(host.Ram[0x20], Is.EqualTo(1), "execution resumed at 'after'");
        Assert.That(host.Ram[0x21], Is.EqualTo(1));
        Assert.That(host.Ram[0x22] & Cpu6502.FlagB, Is.Not.EqualTo(0));
        Assert.That(host.Cpu.P & Cpu6502.FlagB, Is.EqualTo(0), "RTI does not store B");
        Assert.That(host.Cpu.P & Cpu6502.FlagU, Is.Not.EqualTo(0));
        Assert.That(host.Cpu.S, Is.EqualTo(0xFF));
    }

    // ------------------------------------------------------------------ JMP (ind) page wrap bug

    [Test]
    public void JmpIndirect_PageWrapBug()
    {
        // 64doc: "JMP ($xxFF)" fetches the high byte from $xx00, not from the next page.
        var host = CpuTestHost.FromAssembly("""
                    * = $2000
            start:  jmp ($10ff)
                    * = $10ff
                    .byte $34       ; low byte of the target
                    * = $1000
                    .byte $12       ; high byte actually used ($10FF + 1 wraps to $1000)
                    * = $1100
                    .byte $13       ; high byte a bug-free CPU would use
                    * = $1234
                    lda #$aa
                    sta $20
            ok:     jmp ok
                    * = $1334
                    lda #$bb
                    sta $20
            bad:    jmp bad
            """);
        int cycles = host.RunUntilPc(0x1234);
        Assert.That(cycles, Is.EqualTo(5), "JMP (ind) is 5 cycles (64doc)");
        Assert.That(host.Trace.Select(t => t.Address).ToArray(), Is.EqualTo(new ushort[] { 0x2000, 0x2001, 0x2002, 0x10FF, 0x1000 }));
        host.RunUntilJamOrTrap(1000);
        Assert.That(host.TrappedPc, Is.EqualTo(host.Label("ok")));
        Assert.That(host.Ram[0x20], Is.EqualTo(0xAA));
    }

    // ------------------------------------------------------------------ undocumented opcodes

    [Test]
    public void IllegalOpcodes_LaxSaxDcpIsc()
    {
        var host = CpuTestHost.FromAssembly("""
            start:  lax $10         ; A = X = $5A            (3 cycles, like LDA zp)
                    sta $20
                    stx $21
                    ldx #$0f
                    sax $22         ; [$22] = A & X = $0A    (3 cycles, like STA zp)
                    lda #$10
                    dcp $11         ; [$11] = $11 - 1 = $10; CMP A -> Z=1 C=1   (5 cycles, like DEC zp)
                    php
                    pla
                    sta $23
                    lda #$05
                    sec
                    isc $12         ; [$12] = $02 + 1 = $03; A = $05 - $03 = $02  (5 cycles, like INC zp)
                    sta $24
                    lda $12
                    sta $25
                    lda $11
                    sta $26
                    lax #$00        ; LAX #imm (unstable): A = X = (A | magic) & 0 = 0
                    stx $27
            done:   jmp done
            """);
        host.Ram[0x10] = 0x5A;
        host.Ram[0x11] = 0x11;
        host.Ram[0x12] = 0x02;
        int cycles = host.RunUntilLabel("done");
        Assert.That(host.Ram[0x20], Is.EqualTo(0x5A));
        Assert.That(host.Ram[0x21], Is.EqualTo(0x5A));
        Assert.That(host.Ram[0x22], Is.EqualTo(0x0A));
        Assert.That(host.Ram[0x23] & (Cpu6502.FlagZ | Cpu6502.FlagC), Is.EqualTo(Cpu6502.FlagZ | Cpu6502.FlagC));
        Assert.That(host.Ram[0x24], Is.EqualTo(0x02));
        Assert.That(host.Ram[0x25], Is.EqualTo(0x03));
        Assert.That(host.Ram[0x26], Is.EqualTo(0x10));
        Assert.That(host.Ram[0x27], Is.EqualTo(0x00));
        // 3+3+3+2+3+2+5+3+4+3+2+2+5+3+3+3+3+3+2+3 = 60
        Assert.That(cycles, Is.EqualTo(60));
    }

    // ------------------------------------------------------------------ bus level details

    [Test]
    public void ReadModifyWrite_WritesOldValueThenNewValue()
    {
        // 64doc: RMW instructions write the unmodified value back before writing the result.
        var host = CpuTestHost.FromAssembly("""
            start:  inc $1234
            done:   jmp done
            """);
        host.Ram[0x1234] = 0x41;
        int cycles = host.RunInstructions(1);
        Assert.That(cycles, Is.EqualTo(6));
        Assert.That(host.Trace.Select(t => t.ToString()).ToArray(), Is.EqualTo(new[]
        {
            "R $1000 = $EE", "R $1001 = $34", "R $1002 = $12", "R $1234 = $41", "W $1234 = $41", "W $1234 = $42",
        }));
    }

    [Test]
    public void AbsoluteXStore_DummyReadsUnfixedAddress()
    {
        // 64doc: abs,X stores always take 5 cycles; cycle 4 reads from the address with the un-incremented
        // high byte ($1210 here) before the write to the correct address ($1310).
        var host = CpuTestHost.FromAssembly("""
            start:  ldx #$20
                    sta $12f0,x
            done:   jmp done
            """);
        host.Cpu.A = 0x99;
        host.RunInstructions(1);
        host.Trace.Clear();
        int cycles = host.RunInstructions(1);
        Assert.That(cycles, Is.EqualTo(5));
        Assert.That(host.Trace.Select(t => t.ToString()).ToArray(), Is.EqualTo(new[]
        {
            "R $1002 = $9D", "R $1003 = $F0", "R $1004 = $12", "R $1210 = $00", "W $1310 = $99",
        }));
    }

    // ------------------------------------------------------------------ RDY

    [Test]
    public void RdyStall_MidInstruction_AddsExactlyTheStalledCycles()
    {
        const string source = """
            start:  lda $1234
                    sta $20
                    adc #1
                    sta $21
            done:   jmp done
            """;
        var reference = CpuTestHost.FromAssembly(source);
        reference.Ram[0x1234] = 0x77;
        int referenceCycles = reference.RunUntilLabel("done");
        Assert.That(referenceCycles, Is.EqualTo(4 + 3 + 2 + 3));

        var host = CpuTestHost.FromAssembly(source);
        host.Ram[0x1234] = 0x77;
        host.RunCycles(2);                        // opcode fetch + low address byte of "lda $1234"
        Assert.That(host.Cpu.AtInstructionBoundary, Is.False);
        int traceBefore = host.Trace.Count;

        // RDY low: the CPU halts before its next read cycle (MOS 6500 hardware manual / 64doc: RDY is only
        // honoured on read cycles). Nothing happens on the bus while stalled.
        host.Cpu.Rdy = false;
        host.RunCycles(5);
        Assert.That(host.Cpu.Cycles, Is.EqualTo(2));
        Assert.That(host.Cpu.StallCycles, Is.EqualTo(5));
        Assert.That(host.Trace.Count, Is.EqualTo(traceBefore), "no bus activity while stalled");
        host.Cpu.Rdy = true;

        int rest = host.RunUntilLabel("done");
        Assert.That(2 + 5 + rest, Is.EqualTo(referenceCycles + 5), "total Clock() calls grew by exactly the stall");
        Assert.That(host.Cpu.Cycles, Is.EqualTo(referenceCycles), "executed cycles are unchanged");
        Assert.That(host.Ram[0x20], Is.EqualTo(0x77));
        Assert.That(host.Ram[0x21], Is.EqualTo(0x78));
        Assert.That(host.Trace, Is.EqualTo(reference.Trace), "identical bus trace");
    }

    [Test]
    public void RdyLow_DoesNotStallWriteCycles()
    {
        // "inc $1234": fetch, adl, adh, read, write old, write new. Pull RDY low after the read cycle: both
        // write cycles still execute (the 6502 ignores RDY during writes), then the next opcode fetch stalls.
        var host = CpuTestHost.FromAssembly("""
            start:  inc $1234
            done:   jmp done
            """);
        host.Ram[0x1234] = 0x10;
        host.RunCycles(4);
        host.Cpu.Rdy = false;
        host.RunCycles(2);
        Assert.That(host.Cpu.Cycles, Is.EqualTo(6));
        Assert.That(host.Cpu.StallCycles, Is.EqualTo(0));
        Assert.That(host.Ram[0x1234], Is.EqualTo(0x11));
        Assert.That(host.Cpu.AtInstructionBoundary, Is.True);
        host.RunCycles(3);
        Assert.That(host.Cpu.Cycles, Is.EqualTo(6), "opcode fetch is a read: stalled");
        Assert.That(host.Cpu.StallCycles, Is.EqualTo(3));
        host.Cpu.Rdy = true;
        host.RunUntilLabel("done");
        Assert.That(host.Cpu.Cycles, Is.EqualTo(9));
    }

    // ------------------------------------------------------------------ misc

    [Test]
    public void Fibonacci_16Bit()
    {
        var host = CpuTestHost.FromAssembly("""
            start:  lda #0
                    sta $10         ; a lo
                    sta $11         ; a hi
                    sta $13         ; b hi
                    lda #1
                    sta $12         ; b lo
                    ldx $20         ; n
            loop:   clc
                    lda $10
                    adc $12
                    tay
                    lda $11
                    adc $13
                    pha
                    lda $12
                    sta $10
                    lda $13
                    sta $11
                    sty $12
                    pla
                    sta $13
                    dex
                    bne loop
            done:   jmp done
            """);
        host.Ram[0x20] = 23;   // fib(24) = 46368
        host.RunUntilLabel("done");
        Assert.That(host.Ram[0x12] | (host.Ram[0x13] << 8), Is.EqualTo(46368));
    }

    [Test]
    public void StackWrapsWithinPage1()
    {
        var host = CpuTestHost.FromAssembly("""
            start:  ldx #$00
                    txs
                    lda #$aa
                    pha             ; -> $0100, S = $FF
                    pha             ; -> $01FF, S = $FE
                    pla
                    pla
                    tsx
                    stx $20
            done:   jmp done
            """);
        host.RunUntilLabel("done");
        Assert.That(host.Ram[0x100], Is.EqualTo(0xAA));
        Assert.That(host.Ram[0x1FF], Is.EqualTo(0xAA));
        Assert.That(host.Ram[0x20], Is.EqualTo(0x00));
    }

    [Test]
    public void RunUntilJamOrTrap_DetectsJamOpcode()
    {
        var host = CpuTestHost.FromAssembly("""
            start:  nop
                    jam
                    nop
            """);
        host.RunUntilJamOrTrap(100);
        Assert.That(host.Cpu.Jammed, Is.True);
        Assert.That(host.TrappedPc, Is.EqualTo(-1));
    }
}
