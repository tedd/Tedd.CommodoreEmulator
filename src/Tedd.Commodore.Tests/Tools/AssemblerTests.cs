using System;
using System.Collections.Generic;
using NUnit.Framework;
using Tedd.Commodore.Emulator.Cpu;
using Tedd.Commodore.Emulator.Tools;

namespace Tedd.Commodore.Tests.Tools;

[TestFixture]
public class AssemblerTests
{
    private static byte[] Asm(string source, ushort origin = 0x1000) => Assembler.Assemble(source, origin).Bytes;

    private static string Hex(byte[] bytes) => BitConverter.ToString(bytes);

    // ------------------------------------------------------------------ opcode table round trip

    public static IEnumerable<TestCaseData> AllOpcodes()
    {
        for (int i = 0; i < 256; i++)
        {
            var info = Cpu6502.GetOpcodeInfo((byte)i);
            yield return new TestCaseData((byte)i).SetName($"RoundTrip_{i:X2}_{info.Mnemonic}_{info.Mode}");
        }
    }

    /// <summary>
    /// For every opcode: build a source line from its mnemonic and addressing mode, assemble it and check that the
    /// bytes disassemble back to the same text. Documented opcodes must assemble to exactly their opcode byte;
    /// undocumented duplicates (e.g. the seven NOP #imm variants) assemble to the canonical opcode with the same
    /// mnemonic and mode.
    /// </summary>
    [TestCaseSource(nameof(AllOpcodes))]
    public void EveryOpcode_RoundTripsThroughDisassembler(byte opcode)
    {
        var info = Cpu6502.GetOpcodeInfo(opcode);
        string operand = info.Mode switch
        {
            AddressingMode.Implied => "",
            AddressingMode.Accumulator => "A",
            AddressingMode.Immediate => "#$12",
            AddressingMode.ZeroPage => "$12",
            AddressingMode.ZeroPageX => "$12,X",
            AddressingMode.ZeroPageY => "$12,Y",
            AddressingMode.Absolute => "$1234",
            AddressingMode.AbsoluteX => "$1234,X",
            AddressingMode.AbsoluteY => "$1234,Y",
            AddressingMode.Indirect => "($1234)",
            AddressingMode.IndirectX => "($12,X)",
            AddressingMode.IndirectY => "($12),Y",
            AddressingMode.Relative => "$1010",
            _ => throw new ArgumentOutOfRangeException(),
        };
        string text = operand.Length == 0 ? info.Mnemonic : info.Mnemonic + " " + operand;

        var bytes = Asm(text, 0x1000);

        Assert.That(bytes, Has.Length.EqualTo(info.Length), text);
        var assembled = Cpu6502.GetOpcodeInfo(bytes[0]);
        Assert.That(assembled.Mnemonic, Is.EqualTo(info.Mnemonic), text);
        Assert.That(assembled.Mode, Is.EqualTo(info.Mode), text);
        if (!info.Illegal)
            Assert.That(bytes[0], Is.EqualTo(opcode), $"{text}: documented opcode must assemble to itself");

        switch (info.Length)
        {
            case 2:
                Assert.That(bytes[1], Is.EqualTo(info.Mode == AddressingMode.Relative ? 0x0E : 0x12), text);
                break;
            case 3:
                Assert.That(bytes[1], Is.EqualTo(0x34), text);
                Assert.That(bytes[2], Is.EqualTo(0x12), text);
                break;
        }

        var (disassembled, length) = Disassembler.Disassemble(a => a - 0x1000 < bytes.Length ? bytes[a - 0x1000] : (byte)0, 0x1000);
        Assert.That(disassembled, Is.EqualTo(text));
        Assert.That(length, Is.EqualTo(bytes.Length));
    }

    [TestCase("NOP", AddressingMode.Implied, 0xEA)]      // documented $EA beats $1A/$3A/...
    [TestCase("SBC", AddressingMode.Immediate, 0xE9)]    // documented $E9 beats $EB
    [TestCase("NOP", AddressingMode.ZeroPage, 0x04)]     // lowest of $04/$44/$64
    [TestCase("NOP", AddressingMode.Immediate, 0x80)]
    [TestCase("ANC", AddressingMode.Immediate, 0x0B)]
    [TestCase("JAM", AddressingMode.Implied, 0x02)]
    [TestCase("LAX", AddressingMode.Immediate, 0xAB)]
    public void DuplicateOpcodes_PreferDocumentedThenLowest(string mnemonic, AddressingMode mode, int expected)
    {
        Assert.That(Assembler.TryGetOpcode(mnemonic, mode, out byte opcode), Is.True);
        Assert.That(opcode, Is.EqualTo((byte)expected));
    }

    [TestCase("XAA #$12", 0x8B)]
    [TestCase("AXS #$12", 0xCB)]
    [TestCase("AHX $1234,Y", 0x9F)]
    [TestCase("SHS $1234,Y", 0x9B)]
    [TestCase("KIL", 0x02)]
    [TestCase("ISB $12", 0xE7)]
    [TestCase("DCM $12", 0xC7)]
    [TestCase("INS $12", 0xE7)]
    [TestCase("LSE $12", 0x47)]
    [TestCase("ASO $12", 0x07)]
    [TestCase("lse ($12),y", 0x53)]
    public void UndocumentedAliases_MapToCanonicalOpcode(string source, int expected)
    {
        Assert.That(Asm(source)[0], Is.EqualTo((byte)expected));
    }

    [TestCase("ASL", 0x0A)]
    [TestCase("ASL A", 0x0A)]
    [TestCase("asl a", 0x0A)]
    [TestCase("LSR A", 0x4A)]
    [TestCase("ROL", 0x2A)]
    [TestCase("ror a", 0x6A)]
    public void AccumulatorMode_WithOrWithoutA(string source, int expected)
    {
        var bytes = Asm(source);
        Assert.That(bytes, Is.EqualTo(new[] { (byte)expected }));
    }

    [Test]
    public void Mnemonics_AreCaseInsensitive()
    {
        Assert.That(Asm("LDA #1"), Is.EqualTo(new byte[] { 0xA9, 0x01 }));
        Assert.That(Asm("lda #1"), Is.EqualTo(new byte[] { 0xA9, 0x01 }));
        Assert.That(Asm("Lda #1"), Is.EqualTo(new byte[] { 0xA9, 0x01 }));
        Assert.That(Asm(".BYTE 1"), Is.EqualTo(new byte[] { 0x01 }));
        Assert.That(Asm("lda $10,x"), Is.EqualTo(new byte[] { 0xB5, 0x10 }));
    }

    // ------------------------------------------------------------------ labels

    [Test]
    public void Labels_AreCaseSensitive()
    {
        var result = Assembler.Assemble("""
            Loop: nop
            loop: nop
                  jmp Loop
                  jmp loop
            """);
        Assert.That(result.Labels["Loop"], Is.EqualTo(0x1000));
        Assert.That(result.Labels["loop"], Is.EqualTo(0x1001));
        Assert.That(result.Bytes, Is.EqualTo(new byte[] { 0xEA, 0xEA, 0x4C, 0x00, 0x10, 0x4C, 0x01, 0x10 }));

        var ex = Assert.Throws<AssemblerException>(() => Assembler.Assemble("loop: nop\n jmp LOOP"));
        Assert.That(ex!.Message, Does.Contain("LOOP"));
    }

    [Test]
    public void BackwardLabelReference()
    {
        var bytes = Asm("""
            loop:   inx
                    jmp loop
            """);
        Assert.That(bytes, Is.EqualTo(new byte[] { 0xE8, 0x4C, 0x00, 0x10 }));
    }

    [Test]
    public void ForwardLabelReference_AssemblesAsAbsolute()
    {
        var result = Assembler.Assemble("""
                    lda data      ; forward reference -> absolute even though the value ends up < 256? no: data is at $1006
                    jmp end
            data:   .byte 5, 6
            end:    nop
            """);
        Assert.That(result.Bytes, Is.EqualTo(new byte[] { 0xAD, 0x06, 0x10, 0x4C, 0x08, 0x10, 0x05, 0x06, 0xEA }));
        Assert.That(result.Labels["data"], Is.EqualTo(0x1006));
        Assert.That(result.Labels["end"], Is.EqualTo(0x1008));
    }

    [Test]
    public void LabelWithoutColon_AtLineStart()
    {
        var result = Assembler.Assemble("""
            start   ldx #0
            loop    dex
                    bne loop
            done
                    jmp done
            """);
        Assert.That(result.Labels["start"], Is.EqualTo(0x1000));
        Assert.That(result.Labels["loop"], Is.EqualTo(0x1002));
        Assert.That(result.Labels["done"], Is.EqualTo(0x1005));
        Assert.That(result.Bytes, Is.EqualTo(new byte[] { 0xA2, 0x00, 0xCA, 0xD0, 0xFD, 0x4C, 0x05, 0x10 }));
    }

    [Test]
    public void DuplicateLabel_ReportsLine()
    {
        var ex = Assert.Throws<AssemblerException>(() => Assembler.Assemble("a: nop\nb: nop\na: nop"));
        Assert.That(ex!.Line, Is.EqualTo(3));
        Assert.That(ex.Message, Does.StartWith("Line 3:").And.Contain("'a'"));
    }

    // ------------------------------------------------------------------ branches

    [Test]
    public void BranchToLabel_BackwardAndForward()
    {
        var bytes = Asm("""
            top:    dex          ; $1000
                    bne top      ; $1001: offset = $1000 - $1003 = -3 = $FD
                    beq skip     ; $1003: offset = $1007 - $1005 = 2
                    nop          ; $1005
                    nop          ; $1006
            skip:   rts          ; $1007
            """);
        Assert.That(bytes, Is.EqualTo(new byte[] { 0xCA, 0xD0, 0xFD, 0xF0, 0x02, 0xEA, 0xEA, 0x60 }));
    }

    [Test]
    public void Branch_ExtremeOffsetsAreAccepted()
    {
        // offset is relative to the byte after the branch: * + 2 + 127 and * + 2 - 128
        Assert.That(Asm("bne *+129"), Is.EqualTo(new byte[] { 0xD0, 0x7F }));
        Assert.That(Asm("bne *-126"), Is.EqualTo(new byte[] { 0xD0, 0x80 }));
        Assert.That(Asm("bne *"), Is.EqualTo(new byte[] { 0xD0, 0xFE }));
        Assert.That(Asm("bne *+2"), Is.EqualTo(new byte[] { 0xD0, 0x00 }));
    }

    [Test]
    public void BranchOutOfRange_ReportsLine()
    {
        var source = """
            start:  nop
                    .res 200, $ea
                    bne start       ; line 3: too far back
            """;
        var ex = Assert.Throws<AssemblerException>(() => Assembler.Assemble(source));
        Assert.That(ex!.Line, Is.EqualTo(3));
        Assert.That(ex.Message, Does.Contain("Branch out of range"));

        var ex2 = Assert.Throws<AssemblerException>(() => Assembler.Assemble("bne *+130"));
        Assert.That(ex2!.Line, Is.EqualTo(1));
        var ex3 = Assert.Throws<AssemblerException>(() => Assembler.Assemble("nop\nbne *-127"));
        Assert.That(ex3!.Line, Is.EqualTo(2));
    }

    // ------------------------------------------------------------------ expressions

    [TestCase(".byte 2+3*4", 14)]
    [TestCase(".byte (2+3)*4", 20)]
    [TestCase(".byte 10-2-3", 5)]
    [TestCase(".byte 16/2/2", 4)]
    [TestCase(".byte 1<<4|1", 17)]          // shift binds tighter than |
    [TestCase(".byte 7&3^1", 2)]            // & binds tighter than ^
    [TestCase(".byte 1|2^3", 1 | (2 ^ 3))]  // ^ binds tighter than |
    [TestCase(".byte $ff>>4", 15)]
    [TestCase(".byte ~0&$0f", 15)]
    [TestCase(".byte -1", 0xFF)]
    [TestCase(".byte -(3-5)", 2)]
    [TestCase(".byte %1010+$10+'A'-65", 26)]
    [TestCase(".byte 0x1F", 31)]
    [TestCase(".byte 2*(3+4)", 14)]
    public void Expressions_AndPrecedence(string source, int expected)
    {
        Assert.That(Asm(source), Is.EqualTo(new[] { (byte)expected }));
    }

    [Test]
    public void LowHighByteOperators()
    {
        Assert.That(Asm("lda #<$1234"), Is.EqualTo(new byte[] { 0xA9, 0x34 }));
        Assert.That(Asm("lda #>$1234"), Is.EqualTo(new byte[] { 0xA9, 0x12 }));

        // The operators apply to the whole expression to their right: <label+1 is the low byte of (label+1).
        var bytes = Asm("""
            label = $12ff
                    lda #<label+1
                    lda #>label+1
                    .word (<label)+1
                    .byte <label, >label
            """);
        Assert.That(bytes, Is.EqualTo(new byte[] { 0xA9, 0x00, 0xA9, 0x13, 0x00, 0x01, 0xFF, 0x12 }));
    }

    [Test]
    public void CurrentAddress_Star_InExpressions()
    {
        var bytes = Asm("""
                    * = $2000
                    jmp *           ; 4C 00 20
                    .word *         ; 03 20
                    lda #<*         ; A9 05
                    lda #>*+1       ; A9 20  (high byte of $2008)
                    lda *+2         ; AD 0B 20
            """);
        Assert.That(Hex(bytes), Is.EqualTo(Hex(new byte[] { 0x4C, 0x00, 0x20, 0x03, 0x20, 0xA9, 0x05, 0xA9, 0x20, 0xAD, 0x0B, 0x20 })));
    }

    [Test]
    public void NumberFormats()
    {
        Assert.That(Asm(".byte $ff, %11111111, 255, 0xff, 'A', '0'"), Is.EqualTo(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x41, 0x30 }));
    }

    // ------------------------------------------------------------------ directives

    [Test]
    public void ByteDirective_NumbersStringsAndChars()
    {
        Assert.That(Asm(".byte 1, $02, %11, 'A', \"Hi\""), Is.EqualTo(new byte[] { 1, 2, 3, 0x41, 0x48, 0x69 }));
        Assert.That(Asm(".db 1,2"), Is.EqualTo(new byte[] { 1, 2 }));
    }

    [Test]
    public void WordDirective_IsLittleEndian()
    {
        var bytes = Asm("""
                    .word $1234, label, -1
            label:  .dw $abcd
            """);
        Assert.That(bytes, Is.EqualTo(new byte[] { 0x34, 0x12, 0x06, 0x10, 0xFF, 0xFF, 0xCD, 0xAB }));
    }

    [Test]
    public void TextDirective()
    {
        Assert.That(Asm(".text \"AB\", 0"), Is.EqualTo(new byte[] { 0x41, 0x42, 0x00 }));
        Assert.That(Asm(".text \"a;b\"  ; comment after string"), Is.EqualTo(new byte[] { 0x61, 0x3B, 0x62 }));
        Assert.That(Asm(".text \"q\\\"x\""), Is.EqualTo(new byte[] { 0x71, 0x22, 0x78 }));
    }

    [Test]
    public void ResDirective_WithAndWithoutFill()
    {
        var result = Assembler.Assemble("""
                    .res 3
                    .res 2, $ff
            after:  nop
            """);
        Assert.That(result.Bytes, Is.EqualTo(new byte[] { 0, 0, 0, 0xFF, 0xFF, 0xEA }));
        Assert.That(result.Labels["after"], Is.EqualTo(0x1005));
    }

    [Test]
    public void SymbolAssignment()
    {
        var result = Assembler.Assemble("""
            count = 5
            ptr = $fb
            screen = $0400
            big = $12345
                    lda #count
                    sta ptr
                    sta screen,x
                    lda #<big
            """);
        Assert.That(result.Bytes, Is.EqualTo(new byte[] { 0xA9, 0x05, 0x85, 0xFB, 0x9D, 0x00, 0x04, 0xA9, 0x45 }));
        Assert.That(result.Labels["count"], Is.EqualTo(5));
        Assert.That(result.Labels["screen"], Is.EqualTo(0x0400));
        Assert.That(result.Symbols["big"], Is.EqualTo(0x12345));
    }

    [Test]
    public void SymbolAssignment_ForwardReference_ResolvedInPass2()
    {
        var bytes = Asm("""
                    lda #val
                    lda #<later+1
            val = 7
            later = end
            end:    nop
            """);
        Assert.That(bytes, Is.EqualTo(new byte[] { 0xA9, 0x07, 0xA9, 0x05, 0xEA }));
    }

    // ------------------------------------------------------------------ zero page selection

    [TestCase("lda $10", "A5-10")]
    [TestCase("lda $0010", "A5-10")]          // the value is what matters, not how it is written
    [TestCase("lda $1000", "AD-00-10")]
    [TestCase("lda $ff", "A5-FF")]
    [TestCase("lda $100", "AD-00-01")]
    [TestCase("sta $10,x", "95-10")]
    [TestCase("ldx $10,y", "B6-10")]
    [TestCase("lda $10,y", "B9-10-00")]       // LDA has no zp,Y form
    [TestCase("stx $10,y", "96-10")]
    [TestCase("jmp $10", "4C-10-00")]         // JMP has no zero page form
    [TestCase("cpx $10", "E4-10")]
    [TestCase("bit $10", "24-10")]
    [TestCase("lda ($10,x)", "A1-10")]
    [TestCase("lda ($10),y", "B1-10")]
    [TestCase("jmp ($1234)", "6C-34-12")]
    [TestCase("lda ($10)+1,y", "B9-11-00")]   // parenthesised expression, not indirect
    public void ZeroPage_AutoSelection(string source, string expected)
    {
        Assert.That(Hex(Asm(source)), Is.EqualTo(expected));
    }

    [Test]
    public void ZeroPage_NotUsedForForwardReferences_ButUsedForBackwardOnes()
    {
        var bytes = Asm("""
            before = $20
                    lda later      ; forward reference -> absolute
                    lda before     ; known and < 256 -> zero page
            later = $10
                    lda later      ; known now -> zero page
            """);
        Assert.That(Hex(bytes), Is.EqualTo("AD-10-00-A5-20-A5-10"));
    }

    [Test]
    public void ForcedModes_WithSuffix()
    {
        Assert.That(Hex(Asm("lda.a $10")), Is.EqualTo("AD-10-00"));
        Assert.That(Hex(Asm("LDA.W $10")), Is.EqualTo("AD-10-00"));
        Assert.That(Hex(Asm("sta.a $10,x")), Is.EqualTo("9D-10-00"));
        Assert.That(Hex(Asm("lda.z later\nlater = $10")), Is.EqualTo("A5-10"));
        Assert.That(Hex(Asm("lda.b later\nlater = $10")), Is.EqualTo("A5-10"));

        var ex = Assert.Throws<AssemblerException>(() => Assembler.Assemble("nop\nlda.z $1234"));
        Assert.That(ex!.Line, Is.EqualTo(2));
        Assert.That(ex.Message, Does.Contain("out of range"));

        var ex2 = Assert.Throws<AssemblerException>(() => Assembler.Assemble("jmp.z $10"));
        Assert.That(ex2!.Message, Does.Contain("JMP"));

        var ex3 = Assert.Throws<AssemblerException>(() => Assembler.Assemble("stx.a $10,y"));
        Assert.That(ex3!.Message, Does.Contain("STX"));
    }

    // ------------------------------------------------------------------ source layout

    [Test]
    public void Comments_And_BlankLines()
    {
        var result = Assembler.Assemble("""
            ; a comment-only line

                    ; indented comment
            start:  lda #1    ; trailing comment with ; another ; semicolon
                    ; comment between instructions

            end:    nop;no space before the comment
            """);
        Assert.That(result.Bytes, Is.EqualTo(new byte[] { 0xA9, 0x01, 0xEA }));
        Assert.That(result.Labels["end"], Is.EqualTo(0x1002));
    }

    [Test]
    public void Origin_StarEquals_And_DotOrg()
    {
        var a = Assembler.Assemble("* = $c000\nstart: nop");
        Assert.That(a.Origin, Is.EqualTo(0xC000));
        Assert.That(a.Labels["start"], Is.EqualTo(0xC000));

        var b = Assembler.Assemble("  .org $2000\n  *=$2000+2\nhere: nop");
        Assert.That(b.Origin, Is.EqualTo(0x2002));
        Assert.That(b.Labels["here"], Is.EqualTo(0x2002));
        Assert.That(b.Bytes, Is.EqualTo(new byte[] { 0xEA }));
    }

    [Test]
    public void DefaultOrigin_Parameter()
    {
        var result = Assembler.Assemble("start: nop", 0x0801);
        Assert.That(result.Origin, Is.EqualTo(0x0801));
        Assert.That(result.Labels["start"], Is.EqualTo(0x0801));
        Assert.That(result.StartAddress, Is.EqualTo(0x0801));
    }

    [Test]
    public void MultipleOrigins_ProduceSegmentsAndAContiguousImage()
    {
        var result = Assembler.Assemble("""
                    * = $1000
            start:  lda data
                    jmp start
                    * = $2000
            data:   .byte $42
                    * = $fffc
                    .word start
            """);
        Assert.That(result.Segments, Has.Count.EqualTo(3));
        Assert.That(result.Segments[0].Address, Is.EqualTo(0x1000));
        Assert.That(result.Segments[1].Address, Is.EqualTo(0x2000));
        Assert.That(result.Segments[2].Address, Is.EqualTo(0xFFFC));
        Assert.That(result.Origin, Is.EqualTo(0x1000));
        Assert.That(result.StartAddress, Is.EqualTo(0x1000));
        Assert.That(result.EndAddress, Is.EqualTo(0xFFFE));
        Assert.That(result.Bytes, Has.Length.EqualTo(0xFFFE - 0x1000));
        Assert.That(result.Bytes[0], Is.EqualTo(0xAD));
        Assert.That(result.Bytes[1], Is.EqualTo(0x00));
        Assert.That(result.Bytes[2], Is.EqualTo(0x20));
        Assert.That(result.Bytes[0x2000 - 0x1000], Is.EqualTo(0x42));
        Assert.That(result.Bytes[0x1FFF - 0x1000], Is.EqualTo(0), "gap is zero filled");
        Assert.That(result.Bytes[0xFFFC - 0x1000], Is.EqualTo(0x00));
        Assert.That(result.Bytes[0xFFFD - 0x1000], Is.EqualTo(0x10));

        // CopyTo only touches emitted bytes.
        var memory = new byte[0x10000];
        Array.Fill(memory, (byte)0xAA);
        result.CopyTo(memory);
        Assert.That(memory[0x1000], Is.EqualTo(0xAD));
        Assert.That(memory[0x1FFF], Is.EqualTo(0xAA));
        Assert.That(memory[0x2000], Is.EqualTo(0x42));
        Assert.That(memory[0x2001], Is.EqualTo(0xAA));
        Assert.That(memory[0xFFFD], Is.EqualTo(0x10));
    }

    [Test]
    public void OverlappingOrigins_AreAnError()
    {
        var ex = Assert.Throws<AssemblerException>(() => Assembler.Assemble("* = $1000\n.res 4\n* = $1002\nnop"));
        Assert.That(ex!.Message, Does.Contain("overlap"));
    }

    // ------------------------------------------------------------------ errors

    [Test]
    public void UndefinedSymbol_ReportsLine()
    {
        var ex = Assert.Throws<AssemblerException>(() => Assembler.Assemble("nop\nnop\n lda missing\nnop"));
        Assert.That(ex!.Line, Is.EqualTo(3));
        Assert.That(ex.Message, Is.EqualTo("Line 3: Undefined symbol 'missing'."));
    }

    [Test]
    public void UnknownMnemonic_ReportsLine()
    {
        var ex = Assert.Throws<AssemblerException>(() => Assembler.Assemble("nop\n ldaa #1"));
        Assert.That(ex!.Line, Is.EqualTo(2));
        Assert.That(ex.Message, Does.Contain("ldaa"));

        var ex2 = Assert.Throws<AssemblerException>(() => Assembler.Assemble("\n\n .bytes 1"));
        Assert.That(ex2!.Line, Is.EqualTo(3));
        Assert.That(ex2.Message, Does.Contain(".bytes"));
    }

    [Test]
    public void ImmediateOutOfRange_ReportsLine()
    {
        var ex = Assert.Throws<AssemblerException>(() => Assembler.Assemble("lda #$100"));
        Assert.That(ex!.Line, Is.EqualTo(1));
        Assert.That(ex.Message, Does.Contain("does not fit in a byte"));
        Assert.That(Asm("lda #-1"), Is.EqualTo(new byte[] { 0xA9, 0xFF }));
        Assert.That(Asm("lda #-128"), Is.EqualTo(new byte[] { 0xA9, 0x80 }));
    }

    [Test]
    public void UnsupportedAddressingMode_ReportsLine()
    {
        var ex = Assert.Throws<AssemblerException>(() => Assembler.Assemble("nop\nnop\nsta #1"));
        Assert.That(ex!.Line, Is.EqualTo(3));
        Assert.That(ex.Message, Does.Contain("STA").And.Contain("immediate"));

        var ex2 = Assert.Throws<AssemblerException>(() => Assembler.Assemble("lda"));
        Assert.That(ex2!.Message, Does.Contain("requires an operand"));

        var ex3 = Assert.Throws<AssemblerException>(() => Assembler.Assemble("bne #1"));
        Assert.That(ex3!.Message, Does.Contain("BNE"));

        var ex4 = Assert.Throws<AssemblerException>(() => Assembler.Assemble("inx #1"));
        Assert.That(ex4!.Message, Does.Contain("INX"));
    }

    [Test]
    public void SyntaxErrors_ReportLine()
    {
        Assert.That(Assert.Throws<AssemblerException>(() => Assembler.Assemble("\nlda ($10,y)"))!.Line, Is.EqualTo(2));
        Assert.That(Assert.Throws<AssemblerException>(() => Assembler.Assemble("\n\nlda (1"))!.Line, Is.EqualTo(3));
        Assert.That(Assert.Throws<AssemblerException>(() => Assembler.Assemble(".byte 1 2"))!.Line, Is.EqualTo(1));
        Assert.That(Assert.Throws<AssemblerException>(() => Assembler.Assemble(".byte $"))!.Line, Is.EqualTo(1));
        Assert.That(Assert.Throws<AssemblerException>(() => Assembler.Assemble(".text \"open"))!.Line, Is.EqualTo(1));
        Assert.That(Assert.Throws<AssemblerException>(() => Assembler.Assemble("lda #1/0"))!.Message, Does.Contain("Division by zero"));
        Assert.That(Assert.Throws<AssemblerException>(() => Assembler.Assemble("* = later\nlater: nop"))!.Message, Does.Contain("pass 1"));
    }

    [Test]
    public void Result_LabelsContainAllSymbols_And_CopyToBus()
    {
        var result = Assembler.Assemble("""
            v = 3
            start:  nop
            end:
            """);
        Assert.That(result.Labels.Keys, Is.EquivalentTo(new[] { "v", "start", "end" }));
        Assert.That(result.Labels["end"], Is.EqualTo(0x1001));

        var written = new List<(ushort, byte)>();
        result.CopyTo((a, b) => written.Add((a, b)));
        Assert.That(written, Is.EqualTo(new[] { ((ushort)0x1000, (byte)0xEA) }));
    }

    [Test]
    public void IndirectForms_AndRegisterNamesAreCaseInsensitive()
    {
        Assert.That(Hex(Asm("lda ($10,X)")), Is.EqualTo("A1-10"));
        Assert.That(Hex(Asm("lda ($10 , x )")), Is.EqualTo("A1-10"));
        Assert.That(Hex(Asm("sta ($10),Y")), Is.EqualTo("91-10"));
        Assert.That(Hex(Asm("jmp (vec)\nvec: .word 0")), Is.EqualTo("6C-03-10-00-00"));
        Assert.That(Hex(Asm("ldy $1234,X")), Is.EqualTo("BC-34-12"));
    }

    [Test]
    public void CompleteProgram_MatchesHandAssembly()
    {
        // A typical test program: zero page pointer, loop, subroutine, data.
        var result = Assembler.Assemble("""
                    * = $c000
            ptr     = $fb
            start:  lda #<msg
                    sta ptr
                    lda #>msg
                    sta ptr+1
                    ldy #0
            loop:   lda (ptr),y
                    beq done
                    jsr out
                    iny
                    bne loop
            done:   jmp done
            out:    sta $0400,y
                    rts
            msg:    .text "HI", 0
            """);
        Assert.That(Hex(result.Bytes), Is.EqualTo(Hex(new byte[]
        {
            0xA9, 0x1B,             // lda #<msg   ($C01B)
            0x85, 0xFB,             // sta ptr
            0xA9, 0xC0,             // lda #>msg
            0x85, 0xFC,             // sta ptr+1
            0xA0, 0x00,             // ldy #0
            0xB1, 0xFB,             // loop: lda (ptr),y
            0xF0, 0x06,             // beq done  ($C014)
            0x20, 0x17, 0xC0,       // jsr out   ($C017)
            0xC8,                   // iny
            0xD0, 0xF6,             // bne loop  ($C00A)
            0x4C, 0x14, 0xC0,       // done: jmp done
            0x99, 0x00, 0x04,       // out: sta $0400,y
            0x60,                   // rts
            0x48, 0x49, 0x00,       // msg
        })));
    }
}
