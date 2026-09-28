using System;
using System.Collections.Generic;
using Tedd.Commodore.Emulator.Cpu;

namespace Tedd.Commodore.Emulator.Tools;

/// <summary>
/// Two-pass 6502 assembler used by the test-suite to inject compiled programs.
///
/// <para>Syntax (one statement per line, mnemonics and directives are case-insensitive, labels are case-sensitive):</para>
/// <code>
/// * = $C000            ; or .org $C000 - sets the current address (must be known in pass 1)
/// label:  lda #&lt;value ; "name:" or "name" at the start of a line followed by a statement
/// value = $1234        ; symbol assignment (forward references allowed)
///         .byte 1, $02, %11, 'c', "text", &lt;label, &gt;label    ; .db is an alias
///         .word label, $1234                                   ; .dw is an alias, little-endian
///         .text "hello", 13, 0                                 ; same as .byte
///         .res 16[, fill]                                      ; reserve n bytes (count must be known in pass 1)
/// </code>
/// <para>Operands: <c>#expr</c> immediate, <c>expr</c>, <c>expr,X</c>, <c>expr,Y</c>, <c>(expr,X)</c>, <c>(expr),Y</c>,
/// <c>(expr)</c>, <c>A</c> (or nothing) for accumulator instructions. Expressions use
/// <c>+ - * / &amp; | ^ &lt;&lt; &gt;&gt; ( )</c>, unary <c>-</c> and <c>~</c>, numbers <c>$hex %bin decimal 'c'</c>,
/// <c>*</c> for the address of the current statement, and the low/high byte prefixes <c>&lt;expr</c> / <c>&gt;expr</c>
/// which apply to the whole expression to their right (<c>#&lt;label+1</c> is the low byte of label+1).</para>
/// <para>Zero page addressing is chosen automatically when the operand value is already known in pass 1
/// (i.e. defined before use), is below 256 and the instruction has a zero page form; otherwise the absolute
/// form is used. Append <c>.z</c> (or <c>.b</c>) to a mnemonic to force zero page, <c>.a</c> (or <c>.w</c>) to
/// force absolute: <c>lda.z data</c>, <c>sta.a $10</c>.</para>
/// <para>All 256 opcodes are supported; the opcode table is built from <see cref="Cpu6502.GetOpcodeInfo"/> so
/// assembler and CPU agree on every mnemonic. Undocumented mnemonics use the names of the CPU core (SLO RLA SRE
/// RRA SAX LAX DCP ISC ANC ALR ARR ANE SBX LAS SHA SHX SHY TAS JAM) plus the aliases XAA=ANE, AXS=SBX, AHX=SHA,
/// SHS=TAS, KIL=HLT=JAM, ISB=INS=ISC, DCM=DCP, LSE=SRE, ASO=SLO, LXA=LAX, USBC=SBC, DOP=TOP=SKB=SKW=NOP.
/// When several opcodes share a mnemonic and mode the documented one is used, otherwise the lowest opcode.</para>
/// </summary>
public static class Assembler
{
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["XAA"] = "ANE",
        ["AXS"] = "SBX",
        ["AHX"] = "SHA",
        ["SHS"] = "TAS",
        ["KIL"] = "JAM",
        ["HLT"] = "JAM",
        ["ISB"] = "ISC",
        ["INS"] = "ISC",
        ["DCM"] = "DCP",
        ["LSE"] = "SRE",
        ["ASO"] = "SLO",
        ["LXA"] = "LAX",
        ["USBC"] = "SBC",
        ["DOP"] = "NOP",
        ["TOP"] = "NOP",
        ["SKB"] = "NOP",
        ["SKW"] = "NOP",
    };

    private static readonly Dictionary<(string Mnemonic, AddressingMode Mode), byte> Opcodes = BuildOpcodeTable();
    private static readonly HashSet<string> MnemonicSet = BuildMnemonicSet();

    /// <summary>All mnemonics known to the assembler (canonical names, upper case, without aliases).</summary>
    public static IReadOnlyCollection<string> Mnemonics => MnemonicSet;

    /// <summary>Assembles <paramref name="source"/>. The first bytes go to <paramref name="defaultOrigin"/> unless the source sets an origin first.</summary>
    /// <exception cref="AssemblerException">On any syntax, range or symbol error; <see cref="AssemblerException.Line"/> tells where.</exception>
    public static AssemblyResult Assemble(string source, ushort defaultOrigin = 0x1000)
    {
        if (source is null) throw new ArgumentNullException(nameof(source));
        return new Session(source, defaultOrigin).Run();
    }

    /// <summary>Resolves aliases and upper-cases a mnemonic ("xaa" → "ANE"). Unknown names are returned upper-cased.</summary>
    public static string NormalizeMnemonic(string mnemonic)
    {
        if (mnemonic is null) throw new ArgumentNullException(nameof(mnemonic));
        return Aliases.TryGetValue(mnemonic, out var canonical) ? canonical : mnemonic.ToUpperInvariant();
    }

    /// <summary>True if <paramref name="mnemonic"/> (case-insensitive, aliases allowed) is an instruction.</summary>
    public static bool IsMnemonic(string mnemonic) => mnemonic is not null && MnemonicSet.Contains(NormalizeMnemonic(mnemonic));

    /// <summary>Looks up the opcode the assembler emits for a mnemonic/addressing mode pair.</summary>
    public static bool TryGetOpcode(string mnemonic, AddressingMode mode, out byte opcode)
    {
        if (mnemonic is null) throw new ArgumentNullException(nameof(mnemonic));
        return Opcodes.TryGetValue((NormalizeMnemonic(mnemonic), mode), out opcode);
    }

    private static bool HasMode(string mnemonic, AddressingMode mode) => Opcodes.ContainsKey((mnemonic, mode));

    private static Dictionary<(string, AddressingMode), byte> BuildOpcodeTable()
    {
        var table = new Dictionary<(string, AddressingMode), byte>();
        for (int i = 0; i < 256; i++)
        {
            var info = Cpu6502.GetOpcodeInfo((byte)i);
            var key = (info.Mnemonic, info.Mode);
            if (!table.TryGetValue(key, out var existing))
            {
                table[key] = (byte)i; // lowest opcode wins among equals
            }
            else if (Cpu6502.GetOpcodeInfo(existing).Illegal && !info.Illegal)
            {
                table[key] = (byte)i; // a documented opcode beats an undocumented one (NOP $EA vs $1A, SBC $E9 vs $EB)
            }
        }
        return table;
    }

    private static HashSet<string> BuildMnemonicSet()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in Opcodes.Keys)
            set.Add(key.Mnemonic);
        return set;
    }

    // ------------------------------------------------------------------------------------------------
    // Expressions
    // ------------------------------------------------------------------------------------------------

    private enum ExprKind { Number, Symbol, CurrentAddress, Unary, Binary }

    private sealed class Expr
    {
        public ExprKind Kind;
        public long Number;
        public string? Name;
        public string Op = "";
        public Expr? Left;
        public Expr? Right;

        public static Expr Num(long v) => new() { Kind = ExprKind.Number, Number = v };
        public static Expr Sym(string name) => new() { Kind = ExprKind.Symbol, Name = name };
        public static Expr Pc() => new() { Kind = ExprKind.CurrentAddress };
        public static Expr Unary(string op, Expr operand) => new() { Kind = ExprKind.Unary, Op = op, Left = operand };
        public static Expr Binary(string op, Expr l, Expr r) => new() { Kind = ExprKind.Binary, Op = op, Left = l, Right = r };
    }

    private enum OperandShape { None, Accumulator, Immediate, Direct, DirectX, DirectY, IndirectX, IndirectY, Indirect }

    private enum SizeForce { Auto, ZeroPage, Absolute }

    private enum StatementKind { Org, Assign, Instruction, Bytes, Words, Reserve }

    private readonly struct Arg
    {
        public readonly string? Text;
        public readonly Expr? Expr;
        public Arg(string text) { Text = text; Expr = null; }
        public Arg(Expr expr) { Text = null; Expr = expr; }
    }

    private sealed class Statement
    {
        public int Line;
        public StatementKind Kind;
        public int Address;
        public int Size;
        public string? Name;            // Assign
        public Expr? Expr;              // Assign value, instruction operand, reserve fill
        public string Mnemonic = "";    // Instruction
        public byte Opcode;             // Instruction
        public AddressingMode Mode;     // Instruction
        public List<Arg>? Args;         // Bytes / Words
    }

    // ------------------------------------------------------------------------------------------------
    // One assembly run
    // ------------------------------------------------------------------------------------------------

    private sealed class Session
    {
        private readonly string[] _lines;
        private readonly ushort _defaultOrigin;
        private readonly Dictionary<string, long> _symbols = new(StringComparer.Ordinal);
        private readonly List<Statement> _statements = new();
        private int _pc;

        public Session(string source, ushort defaultOrigin)
        {
            _lines = source.Replace("\r\n", "\n").Split('\n');
            _defaultOrigin = defaultOrigin;
        }

        public AssemblyResult Run()
        {
            Pass1();
            return Pass2();
        }

        // ---------------------------------------------------------------- pass 1: sizes and labels

        private void Pass1()
        {
            _pc = _defaultOrigin;
            for (int i = 0; i < _lines.Length; i++)
            {
                int line = i + 1;
                var tokens = AssemblerTokenizer.Tokenize(_lines[i], line);
                if (tokens.Count == 0)
                    continue;

                int pos = 0;
                bool implicitLabel = false;
                if (tokens[0].Kind == TokenKind.Identifier)
                {
                    if (tokens.Count > 1 && tokens[1].Is(":"))
                    {
                        DefineLabel(tokens[0].Text, line);
                        pos = 2;
                    }
                    else if (tokens.Count > 1 && tokens[1].Is("="))
                    {
                        // assignment, handled below
                    }
                    else if (TryParseMnemonic(tokens[0].Text, out _, out _))
                    {
                        // instruction, handled below
                    }
                    else
                    {
                        DefineLabel(tokens[0].Text, line);
                        pos = 1;
                        implicitLabel = true;
                    }
                }

                if (pos >= tokens.Count)
                    continue;

                var tok = tokens[pos];
                if (tok.Is("*"))
                {
                    if (pos + 1 >= tokens.Count || !tokens[pos + 1].Is("="))
                        throw new AssemblerException(line, "Expected '=' after '*' (origin assignment).");
                    ParseOrg(tokens, pos + 2, line);
                }
                else if (tok.Kind == TokenKind.Identifier && pos + 1 < tokens.Count && tokens[pos + 1].Is("="))
                {
                    ParseAssignment(tokens, pos, line);
                }
                else if (tok.Kind == TokenKind.Directive)
                {
                    ParseDirective(tokens, pos, line);
                }
                else if (tok.Kind == TokenKind.Identifier)
                {
                    ParseInstruction(tokens, pos, line);
                }
                else if (implicitLabel)
                {
                    throw new AssemblerException(line, $"Unknown mnemonic or directive '{tokens[0].Text}'.");
                }
                else
                {
                    throw new AssemblerException(line, $"Unexpected '{tok}'.");
                }
            }
        }

        private void DefineLabel(string name, int line)
        {
            if (_symbols.ContainsKey(name))
                throw new AssemblerException(line, $"Symbol '{name}' is already defined.");
            _symbols[name] = _pc;
        }

        private void Advance(Statement st, int line)
        {
            st.Address = _pc;
            _statements.Add(st);
            _pc += st.Size;
            if (_pc > 0x10000)
                throw new AssemblerException(line, "Output runs past $FFFF.");
        }

        private void ParseOrg(List<Token> tokens, int start, int line)
        {
            var expr = ParseOperandExpression(tokens, start, tokens.Count, line);
            if (!TryEvaluate(expr, _pc, line, out long value, out string? undefined))
                throw new AssemblerException(line, $"Origin must be known in pass 1; symbol '{undefined}' is not defined yet.");
            if (value < 0 || value > 0xFFFF)
                throw new AssemblerException(line, $"Origin ${value:X} is outside $0000-$FFFF.");
            _pc = (int)value;
            _statements.Add(new Statement { Line = line, Kind = StatementKind.Org, Address = _pc });
        }

        private void ParseAssignment(List<Token> tokens, int pos, int line)
        {
            string name = tokens[pos].Text;
            if (name.Contains('.'))
                throw new AssemblerException(line, $"Invalid symbol name '{name}'.");
            var expr = ParseOperandExpression(tokens, pos + 2, tokens.Count, line);
            if (_symbols.ContainsKey(name))
                throw new AssemblerException(line, $"Symbol '{name}' is already defined.");
            if (TryEvaluate(expr, _pc, line, out long value, out _))
                _symbols[name] = value;
            var st = new Statement { Line = line, Kind = StatementKind.Assign, Name = name, Expr = expr };
            Advance(st, line);
        }

        private void ParseDirective(List<Token> tokens, int pos, int line)
        {
            string name = tokens[pos].Text;
            switch (name)
            {
                case ".org":
                    ParseOrg(tokens, pos + 1, line);
                    return;

                case ".byte":
                case ".db":
                case ".text":
                case ".ascii":
                {
                    var args = SplitArgs(tokens, pos + 1, line, allowStrings: true);
                    if (args.Count == 0)
                        throw new AssemblerException(line, $"{name} needs at least one value.");
                    int size = 0;
                    foreach (var a in args)
                        size += a.Text?.Length ?? 1;
                    Advance(new Statement { Line = line, Kind = StatementKind.Bytes, Args = args, Size = size }, line);
                    return;
                }

                case ".word":
                case ".dw":
                {
                    var args = SplitArgs(tokens, pos + 1, line, allowStrings: false);
                    if (args.Count == 0)
                        throw new AssemblerException(line, $"{name} needs at least one value.");
                    Advance(new Statement { Line = line, Kind = StatementKind.Words, Args = args, Size = args.Count * 2 }, line);
                    return;
                }

                case ".res":
                case ".dsb":
                case ".fill":
                {
                    var args = SplitArgs(tokens, pos + 1, line, allowStrings: false);
                    if (args.Count is < 1 or > 2)
                        throw new AssemblerException(line, $"{name} takes a count and an optional fill value.");
                    if (!TryEvaluate(args[0].Expr!, _pc, line, out long count, out string? undefined))
                        throw new AssemblerException(line, $"{name} count must be known in pass 1; symbol '{undefined}' is not defined yet.");
                    if (count < 0 || count > 0x10000)
                        throw new AssemblerException(line, $"{name} count {count} is out of range.");
                    Advance(new Statement { Line = line, Kind = StatementKind.Reserve, Size = (int)count, Expr = args.Count == 2 ? args[1].Expr : null }, line);
                    return;
                }

                default:
                    throw new AssemblerException(line, $"Unknown directive '{name}'.");
            }
        }

        private void ParseInstruction(List<Token> tokens, int pos, int line)
        {
            if (!TryParseMnemonic(tokens[pos].Text, out string mnemonic, out SizeForce force))
                throw new AssemblerException(line, $"Unknown mnemonic '{tokens[pos].Text}'.");

            var (shape, expr) = ParseOperand(tokens, pos + 1, tokens.Count, mnemonic, line);
            bool known = true;
            long value = 0;
            if (expr is not null)
                known = TryEvaluate(expr, _pc, line, out value, out _);

            var mode = DecideMode(mnemonic, shape, known, value, force, line);
            byte opcode = Opcodes[(mnemonic, mode)];
            var st = new Statement
            {
                Line = line,
                Kind = StatementKind.Instruction,
                Mnemonic = mnemonic,
                Mode = mode,
                Opcode = opcode,
                Expr = expr,
                Size = Cpu6502.GetOpcodeInfo(opcode).Length,
            };
            Advance(st, line);
        }

        private static bool TryParseMnemonic(string text, out string mnemonic, out SizeForce force)
        {
            force = SizeForce.Auto;
            mnemonic = text;
            int dot = text.IndexOf('.');
            if (dot >= 0)
            {
                string suffix = text.Substring(dot + 1).ToLowerInvariant();
                force = suffix switch
                {
                    "z" or "b" => SizeForce.ZeroPage,
                    "a" or "w" => SizeForce.Absolute,
                    _ => SizeForce.Auto,
                };
                if (force == SizeForce.Auto)
                    return false;
                mnemonic = text.Substring(0, dot);
            }
            mnemonic = NormalizeMnemonic(mnemonic);
            return MnemonicSet.Contains(mnemonic);
        }

        private static (OperandShape Shape, Expr? Expr) ParseOperand(List<Token> tokens, int start, int end, string mnemonic, int line)
        {
            if (start >= end)
                return (OperandShape.None, null);

            var first = tokens[start];
            if (first.Is("#"))
                return (OperandShape.Immediate, ParseOperandExpression(tokens, start + 1, end, line));

            if (first.Is("("))
            {
                int close = FindMatchingParen(tokens, start, end, line);
                if (close == end - 1)
                {
                    // "(expr,X)" or "(expr)"
                    if (close - 2 > start && tokens[close - 2].Is(",") && IsRegister(tokens[close - 1], 'X'))
                        return (OperandShape.IndirectX, ParseOperandExpression(tokens, start + 1, close - 2, line));
                    return (OperandShape.Indirect, ParseOperandExpression(tokens, start + 1, close, line));
                }
                if (close == end - 3 && tokens[close + 1].Is(",") && IsRegister(tokens[close + 2], 'Y'))
                    return (OperandShape.IndirectY, ParseOperandExpression(tokens, start + 1, close, line));
                // otherwise the parenthesis is part of an expression, e.g. "(base+1),X" is NOT indirect: fall through
            }

            if (end - start == 1 && first.Kind == TokenKind.Identifier && first.Text.Equals("A", StringComparison.OrdinalIgnoreCase)
                && HasMode(mnemonic, AddressingMode.Accumulator))
                return (OperandShape.Accumulator, null);

            int comma = FindTopLevelComma(tokens, start, end);
            if (comma >= 0)
            {
                if (comma + 2 != end)
                    throw new AssemblerException(line, "Expected ',X' or ',Y' at the end of the operand.");
                var reg = tokens[comma + 1];
                if (IsRegister(reg, 'X'))
                    return (OperandShape.DirectX, ParseOperandExpression(tokens, start, comma, line));
                if (IsRegister(reg, 'Y'))
                    return (OperandShape.DirectY, ParseOperandExpression(tokens, start, comma, line));
                throw new AssemblerException(line, $"Expected index register X or Y after ',' but found '{reg}'.");
            }

            return (OperandShape.Direct, ParseOperandExpression(tokens, start, end, line));
        }

        private static bool IsRegister(Token t, char reg) =>
            t.Kind == TokenKind.Identifier && t.Text.Length == 1 && char.ToUpperInvariant(t.Text[0]) == reg;

        private static int FindMatchingParen(List<Token> tokens, int open, int end, int line)
        {
            int depth = 0;
            for (int i = open; i < end; i++)
            {
                if (tokens[i].Is("(")) depth++;
                else if (tokens[i].Is(")") && --depth == 0) return i;
            }
            throw new AssemblerException(line, "Missing ')'.");
        }

        private static int FindTopLevelComma(List<Token> tokens, int start, int end)
        {
            int depth = 0;
            for (int i = start; i < end; i++)
            {
                if (tokens[i].Is("(")) depth++;
                else if (tokens[i].Is(")")) depth--;
                else if (depth == 0 && tokens[i].Is(",")) return i;
            }
            return -1;
        }

        private static List<Arg> SplitArgs(List<Token> tokens, int start, int line, bool allowStrings)
        {
            var args = new List<Arg>();
            int end = tokens.Count;
            int pieceStart = start;
            while (pieceStart < end)
            {
                int comma = FindTopLevelComma(tokens, pieceStart, end);
                int pieceEnd = comma < 0 ? end : comma;
                if (pieceEnd == pieceStart)
                    throw new AssemblerException(line, "Expected a value.");
                if (pieceEnd - pieceStart == 1 && tokens[pieceStart].Kind == TokenKind.String)
                {
                    if (!allowStrings)
                        throw new AssemblerException(line, "Strings are not allowed here.");
                    args.Add(new Arg(tokens[pieceStart].Text));
                }
                else
                {
                    args.Add(new Arg(ParseOperandExpression(tokens, pieceStart, pieceEnd, line)));
                }
                if (comma < 0)
                    break;
                pieceStart = comma + 1;
                if (pieceStart >= end)
                    throw new AssemblerException(line, "Expected a value after ','.");
            }
            return args;
        }

        private static AddressingMode DecideMode(string m, OperandShape shape, bool known, long value, SizeForce force, int line)
        {
            switch (shape)
            {
                case OperandShape.None:
                    if (HasMode(m, AddressingMode.Implied)) return AddressingMode.Implied;
                    if (HasMode(m, AddressingMode.Accumulator)) return AddressingMode.Accumulator;
                    throw new AssemblerException(line, $"{m} requires an operand.");
                case OperandShape.Accumulator:
                    return AddressingMode.Accumulator;
                case OperandShape.Immediate:
                    return Require(m, AddressingMode.Immediate, "immediate", line);
                case OperandShape.IndirectX:
                    return Require(m, AddressingMode.IndirectX, "(zp,X)", line);
                case OperandShape.IndirectY:
                    return Require(m, AddressingMode.IndirectY, "(zp),Y", line);
                case OperandShape.Indirect:
                    return Require(m, AddressingMode.Indirect, "(abs)", line);
                case OperandShape.Direct:
                    if (HasMode(m, AddressingMode.Relative)) return AddressingMode.Relative;
                    return ChooseSize(m, AddressingMode.ZeroPage, AddressingMode.Absolute, known, value, force, line);
                case OperandShape.DirectX:
                    return ChooseSize(m, AddressingMode.ZeroPageX, AddressingMode.AbsoluteX, known, value, force, line);
                case OperandShape.DirectY:
                    return ChooseSize(m, AddressingMode.ZeroPageY, AddressingMode.AbsoluteY, known, value, force, line);
                default:
                    throw new AssemblerException(line, "Internal error: unknown operand shape.");
            }
        }

        private static AddressingMode Require(string m, AddressingMode mode, string description, int line)
        {
            if (!HasMode(m, mode))
                throw new AssemblerException(line, $"{m} does not support {description} addressing.");
            return mode;
        }

        private static AddressingMode ChooseSize(string m, AddressingMode zp, AddressingMode abs, bool known, long value, SizeForce force, int line)
        {
            bool hasZp = HasMode(m, zp);
            bool hasAbs = HasMode(m, abs);
            if (!hasZp && !hasAbs)
                throw new AssemblerException(line, $"{m} does not support {Describe(abs)} addressing.");
            switch (force)
            {
                case SizeForce.ZeroPage:
                    if (!hasZp) throw new AssemblerException(line, $"{m} has no {Describe(zp)} form.");
                    return zp;
                case SizeForce.Absolute:
                    if (!hasAbs) throw new AssemblerException(line, $"{m} has no {Describe(abs)} form.");
                    return abs;
            }
            if (hasZp && known && value >= 0 && value < 0x100)
                return zp;
            return hasAbs ? abs : zp;
        }

        private static string Describe(AddressingMode mode) => mode switch
        {
            AddressingMode.ZeroPage => "zero page",
            AddressingMode.ZeroPageX => "zero page,X",
            AddressingMode.ZeroPageY => "zero page,Y",
            AddressingMode.Absolute => "absolute",
            AddressingMode.AbsoluteX => "absolute,X",
            AddressingMode.AbsoluteY => "absolute,Y",
            _ => mode.ToString(),
        };

        // ---------------------------------------------------------------- pass 2: code generation

        private AssemblyResult Pass2()
        {
            // Resolve assignments that had forward references, in as many rounds as needed.
            bool progress = true;
            while (progress)
            {
                progress = false;
                foreach (var st in _statements)
                {
                    if (st.Kind != StatementKind.Assign || _symbols.ContainsKey(st.Name!))
                        continue;
                    if (TryEvaluate(st.Expr!, st.Address, st.Line, out long v, out _))
                    {
                        _symbols[st.Name!] = v;
                        progress = true;
                    }
                }
            }

            var segments = new List<(int Start, List<byte> Data)> { (_defaultOrigin, new List<byte>()) };
            foreach (var st in _statements)
            {
                var data = segments[^1].Data;
                switch (st.Kind)
                {
                    case StatementKind.Org:
                        segments.Add((st.Address, new List<byte>()));
                        break;

                    case StatementKind.Assign:
                        _symbols[st.Name!] = Evaluate(st.Expr!, st.Address, st.Line);
                        break;

                    case StatementKind.Instruction:
                        EmitInstruction(st, data);
                        break;

                    case StatementKind.Bytes:
                        foreach (var a in st.Args!)
                        {
                            if (a.Text is not null)
                            {
                                foreach (char c in a.Text)
                                {
                                    if (c > 0xFF)
                                        throw new AssemblerException(st.Line, $"Character '{c}' does not fit in a byte.");
                                    data.Add((byte)c);
                                }
                            }
                            else
                            {
                                data.Add(ToByte(Evaluate(a.Expr!, st.Address, st.Line), st.Line));
                            }
                        }
                        break;

                    case StatementKind.Words:
                        foreach (var a in st.Args!)
                        {
                            long v = Evaluate(a.Expr!, st.Address, st.Line);
                            if (v < -0x8000 || v > 0xFFFF)
                                throw new AssemblerException(st.Line, $"Value {v} does not fit in a word.");
                            data.Add((byte)(v & 0xFF));
                            data.Add((byte)((v >> 8) & 0xFF));
                        }
                        break;

                    case StatementKind.Reserve:
                    {
                        byte fill = st.Expr is null ? (byte)0 : ToByte(Evaluate(st.Expr, st.Address, st.Line), st.Line);
                        for (int i = 0; i < st.Size; i++)
                            data.Add(fill);
                        break;
                    }
                }
            }

            return BuildResult(segments);
        }

        private void EmitInstruction(Statement st, List<byte> data)
        {
            data.Add(st.Opcode);
            long value = st.Expr is null ? 0 : Evaluate(st.Expr, st.Address, st.Line);
            switch (st.Mode)
            {
                case AddressingMode.Implied:
                case AddressingMode.Accumulator:
                    break;

                case AddressingMode.Immediate:
                    data.Add(ToByte(value, st.Line));
                    break;

                case AddressingMode.ZeroPage:
                case AddressingMode.ZeroPageX:
                case AddressingMode.ZeroPageY:
                case AddressingMode.IndirectX:
                case AddressingMode.IndirectY:
                    if (value < 0 || value > 0xFF)
                        throw new AssemblerException(st.Line, $"Zero page address ${value:X} is out of range for {st.Mnemonic} {Describe(st.Mode)}.");
                    data.Add((byte)value);
                    break;

                case AddressingMode.Absolute:
                case AddressingMode.AbsoluteX:
                case AddressingMode.AbsoluteY:
                case AddressingMode.Indirect:
                    if (value < 0 || value > 0xFFFF)
                        throw new AssemblerException(st.Line, $"Address ${value:X} is outside $0000-$FFFF.");
                    data.Add((byte)(value & 0xFF));
                    data.Add((byte)(value >> 8));
                    break;

                case AddressingMode.Relative:
                {
                    if (value < 0 || value > 0xFFFF)
                        throw new AssemblerException(st.Line, $"Branch target ${value:X} is outside $0000-$FFFF.");
                    // The offset is relative to the address of the next instruction (PC after the 2-byte branch).
                    long offset = value - (st.Address + 2);
                    if (offset < -128 || offset > 127)
                        throw new AssemblerException(st.Line, $"Branch out of range: target ${value:X4} is {offset} bytes from ${st.Address + 2:X4} (allowed -128..127).");
                    data.Add((byte)(offset & 0xFF));
                    break;
                }

                default:
                    throw new AssemblerException(st.Line, $"Internal error: cannot encode mode {st.Mode}.");
            }
        }

        private static byte ToByte(long value, int line)
        {
            if (value < -128 || value > 0xFF)
                throw new AssemblerException(line, $"Value {value} (${value:X}) does not fit in a byte.");
            return (byte)(value & 0xFF);
        }

        private AssemblyResult BuildResult(List<(int Start, List<byte> Data)> raw)
        {
            var segments = new List<AssemblySegment>();
            foreach (var (start, data) in raw)
                if (data.Count > 0)
                    segments.Add(new AssemblySegment((ushort)start, data.ToArray()));

            var labels = new Dictionary<string, ushort>(StringComparer.Ordinal);
            var symbols = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var (name, value) in _symbols)
            {
                labels[name] = (ushort)(value & 0xFFFF);
                symbols[name] = value;
            }

            ushort firstOrigin = (ushort)raw[raw.Count > 1 ? 1 : 0].Start;
            if (segments.Count == 0)
                return new AssemblyResult(firstOrigin, Array.Empty<byte>(), firstOrigin, labels, symbols, segments);

            int lowest = int.MaxValue, highest = 0;
            foreach (var s in segments)
            {
                lowest = Math.Min(lowest, s.Address);
                highest = Math.Max(highest, s.End);
            }

            var image = new byte[highest - lowest];
            var written = new bool[0x10000];
            foreach (var s in segments)
            {
                for (int i = 0; i < s.Bytes.Length; i++)
                {
                    int address = s.Address + i;
                    if (written[address])
                        throw new AssemblerException(0, $"Output overlaps at ${address:X4} (two origins emit to the same address).");
                    written[address] = true;
                }
                Array.Copy(s.Bytes, 0, image, s.Address - lowest, s.Bytes.Length);
            }

            return new AssemblyResult((ushort)lowest, image, segments[0].Address, labels, symbols, segments);
        }

        // ---------------------------------------------------------------- expression parsing

        private static Expr ParseOperandExpression(List<Token> tokens, int start, int end, int line)
        {
            if (start >= end)
                throw new AssemblerException(line, "Expected an expression.");
            int pos = start;
            var expr = ParseExpression(tokens, ref pos, end, line);
            if (pos != end)
                throw new AssemblerException(line, $"Unexpected '{tokens[pos]}' in expression.");
            return expr;
        }

        private static Expr ParseExpression(List<Token> tokens, ref int pos, int end, int line) => ParseOr(tokens, ref pos, end, line);

        private static Expr ParseOr(List<Token> tokens, ref int pos, int end, int line)
        {
            var left = ParseXor(tokens, ref pos, end, line);
            while (pos < end && tokens[pos].Is("|"))
            {
                pos++;
                left = Expr.Binary("|", left, ParseXor(tokens, ref pos, end, line));
            }
            return left;
        }

        private static Expr ParseXor(List<Token> tokens, ref int pos, int end, int line)
        {
            var left = ParseAnd(tokens, ref pos, end, line);
            while (pos < end && tokens[pos].Is("^"))
            {
                pos++;
                left = Expr.Binary("^", left, ParseAnd(tokens, ref pos, end, line));
            }
            return left;
        }

        private static Expr ParseAnd(List<Token> tokens, ref int pos, int end, int line)
        {
            var left = ParseShift(tokens, ref pos, end, line);
            while (pos < end && tokens[pos].Is("&"))
            {
                pos++;
                left = Expr.Binary("&", left, ParseShift(tokens, ref pos, end, line));
            }
            return left;
        }

        private static Expr ParseShift(List<Token> tokens, ref int pos, int end, int line)
        {
            var left = ParseAdditive(tokens, ref pos, end, line);
            while (pos < end && (tokens[pos].Is("<<") || tokens[pos].Is(">>")))
            {
                string op = tokens[pos++].Text;
                left = Expr.Binary(op, left, ParseAdditive(tokens, ref pos, end, line));
            }
            return left;
        }

        private static Expr ParseAdditive(List<Token> tokens, ref int pos, int end, int line)
        {
            var left = ParseTerm(tokens, ref pos, end, line);
            while (pos < end && (tokens[pos].Is("+") || tokens[pos].Is("-")))
            {
                string op = tokens[pos++].Text;
                left = Expr.Binary(op, left, ParseTerm(tokens, ref pos, end, line));
            }
            return left;
        }

        private static Expr ParseTerm(List<Token> tokens, ref int pos, int end, int line)
        {
            var left = ParseUnary(tokens, ref pos, end, line);
            while (pos < end && (tokens[pos].Is("*") || tokens[pos].Is("/")))
            {
                string op = tokens[pos++].Text;
                left = Expr.Binary(op, left, ParseUnary(tokens, ref pos, end, line));
            }
            return left;
        }

        private static Expr ParseUnary(List<Token> tokens, ref int pos, int end, int line)
        {
            if (pos >= end)
                throw new AssemblerException(line, "Unexpected end of expression.");
            var t = tokens[pos];
            if (t.Is("-") || t.Is("~") || t.Is("+"))
            {
                pos++;
                var operand = ParseUnary(tokens, ref pos, end, line);
                return t.Text == "+" ? operand : Expr.Unary(t.Text, operand);
            }
            if (t.Is("<") || t.Is(">"))
            {
                // Low/high byte operators bind loosely: they apply to the whole expression to their right.
                pos++;
                return Expr.Unary(t.Text, ParseExpression(tokens, ref pos, end, line));
            }
            return ParsePrimary(tokens, ref pos, end, line);
        }

        private static Expr ParsePrimary(List<Token> tokens, ref int pos, int end, int line)
        {
            if (pos >= end)
                throw new AssemblerException(line, "Unexpected end of expression.");
            var t = tokens[pos];
            switch (t.Kind)
            {
                case TokenKind.Number:
                    pos++;
                    return Expr.Num(t.Value);
                case TokenKind.Identifier:
                    pos++;
                    return Expr.Sym(t.Text);
                case TokenKind.Symbol when t.Text == "*":
                    pos++;
                    return Expr.Pc();
                case TokenKind.Symbol when t.Text == "(":
                {
                    pos++;
                    var inner = ParseExpression(tokens, ref pos, end, line);
                    if (pos >= end || !tokens[pos].Is(")"))
                        throw new AssemblerException(line, "Missing ')'.");
                    pos++;
                    return inner;
                }
                case TokenKind.String:
                    throw new AssemblerException(line, "A string is not valid in an expression.");
                default:
                    throw new AssemblerException(line, $"Unexpected '{t}' in expression.");
            }
        }

        // ---------------------------------------------------------------- expression evaluation

        private long Evaluate(Expr expr, long pc, int line)
        {
            if (!TryEvaluate(expr, pc, line, out long value, out string? undefined))
                throw new AssemblerException(line, $"Undefined symbol '{undefined}'.");
            return value;
        }

        /// <summary>Evaluates an expression; returns false (and the offending name) if it references an undefined symbol.</summary>
        private bool TryEvaluate(Expr expr, long pc, int line, out long value, out string? undefined)
        {
            undefined = null;
            switch (expr.Kind)
            {
                case ExprKind.Number:
                    value = expr.Number;
                    return true;
                case ExprKind.CurrentAddress:
                    value = pc;
                    return true;
                case ExprKind.Symbol:
                    if (_symbols.TryGetValue(expr.Name!, out value))
                        return true;
                    undefined = expr.Name;
                    return false;
                case ExprKind.Unary:
                {
                    if (!TryEvaluate(expr.Left!, pc, line, out long operand, out undefined))
                    {
                        value = 0;
                        return false;
                    }
                    value = expr.Op switch
                    {
                        "-" => -operand,
                        "~" => ~operand,
                        "<" => operand & 0xFF,
                        ">" => (operand >> 8) & 0xFF,
                        _ => throw new AssemblerException(line, $"Internal error: unary operator '{expr.Op}'."),
                    };
                    return true;
                }
                case ExprKind.Binary:
                {
                    if (!TryEvaluate(expr.Left!, pc, line, out long l, out undefined) || !TryEvaluate(expr.Right!, pc, line, out long r, out undefined))
                    {
                        value = 0;
                        return false;
                    }
                    switch (expr.Op)
                    {
                        case "+": value = l + r; break;
                        case "-": value = l - r; break;
                        case "*": value = l * r; break;
                        case "/":
                            if (r == 0) throw new AssemblerException(line, "Division by zero.");
                            value = l / r;
                            break;
                        case "&": value = l & r; break;
                        case "|": value = l | r; break;
                        case "^": value = l ^ r; break;
                        case "<<": value = r >= 64 ? 0 : l << (int)r; break;
                        case ">>": value = r >= 64 ? (l < 0 ? -1 : 0) : l >> (int)r; break;
                        default: throw new AssemblerException(line, $"Internal error: binary operator '{expr.Op}'.");
                    }
                    return true;
                }
                default:
                    throw new AssemblerException(line, "Internal error: unknown expression node.");
            }
        }
    }
}
