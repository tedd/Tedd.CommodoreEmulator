using System;
using System.Collections.Generic;
using System.Text;

namespace Tedd.Commodore.Emulator.Tools;

internal enum TokenKind
{
    /// <summary>Label, symbol, mnemonic or register name. May contain '.' after the first character (lda.z).</summary>
    Identifier,
    /// <summary>A directive such as ".byte" (Text holds the lower-case name including the dot).</summary>
    Directive,
    /// <summary>Numeric literal ($hex, %bin, decimal, 0x hex, 'c'). Value holds the number.</summary>
    Number,
    /// <summary>Quoted string (Text holds the unquoted contents).</summary>
    String,
    /// <summary>Operator or punctuation: + - * / &amp; | ^ ~ ( ) , # &lt; &gt; &lt;&lt; &gt;&gt; = :</summary>
    Symbol,
}

/// <summary>One lexical token of an assembler source line.</summary>
internal readonly record struct Token(TokenKind Kind, string Text, long Value, int Column)
{
    public bool Is(string symbol) => Kind == TokenKind.Symbol && Text == symbol;

    public override string ToString() => Kind == TokenKind.String ? "\"" + Text + "\"" : Text;
}

/// <summary>
/// Splits one source line into tokens. Comments start with ';' and extend to the end of the line.
/// </summary>
internal static class AssemblerTokenizer
{
    private static readonly string[] TwoCharSymbols = { "<<", ">>" };
    private const string OneCharSymbols = "+-*/&|^~(),#<>=:";

    public static List<Token> Tokenize(string line, int lineNumber)
    {
        var tokens = new List<Token>();
        int i = 0;
        while (i < line.Length)
        {
            char c = line[i];
            if (c == ';')
                break;
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            int start = i;
            if (c == '$')
            {
                i++;
                int digits = i;
                while (i < line.Length && IsHexDigit(line[i])) i++;
                if (i == digits)
                    throw new AssemblerException(lineNumber, $"Expected hexadecimal digits after '$' at column {start + 1}.");
                tokens.Add(new Token(TokenKind.Number, line.Substring(start, i - start), ParseNumber(line.AsSpan(digits, i - digits), 16, lineNumber), start));
            }
            else if (c == '%')
            {
                i++;
                int digits = i;
                while (i < line.Length && (line[i] == '0' || line[i] == '1')) i++;
                if (i == digits)
                    throw new AssemblerException(lineNumber, $"Expected binary digits after '%' at column {start + 1}.");
                tokens.Add(new Token(TokenKind.Number, line.Substring(start, i - start), ParseNumber(line.AsSpan(digits, i - digits), 2, lineNumber), start));
            }
            else if (c == '0' && i + 1 < line.Length && (line[i + 1] == 'x' || line[i + 1] == 'X'))
            {
                i += 2;
                int digits = i;
                while (i < line.Length && IsHexDigit(line[i])) i++;
                if (i == digits)
                    throw new AssemblerException(lineNumber, $"Expected hexadecimal digits after '0x' at column {start + 1}.");
                tokens.Add(new Token(TokenKind.Number, line.Substring(start, i - start), ParseNumber(line.AsSpan(digits, i - digits), 16, lineNumber), start));
            }
            else if (char.IsDigit(c))
            {
                while (i < line.Length && char.IsDigit(line[i])) i++;
                tokens.Add(new Token(TokenKind.Number, line.Substring(start, i - start), ParseNumber(line.AsSpan(start, i - start), 10, lineNumber), start));
            }
            else if (c == '\'')
            {
                // Character literal: 'c' (the closing quote is required).
                if (i + 2 >= line.Length || line[i + 2] != '\'')
                    throw new AssemblerException(lineNumber, $"Malformed character literal at column {start + 1}; expected 'c'.");
                tokens.Add(new Token(TokenKind.Number, line.Substring(start, 3), line[i + 1], start));
                i += 3;
            }
            else if (c == '"')
            {
                i++;
                var sb = new StringBuilder();
                bool closed = false;
                while (i < line.Length)
                {
                    char s = line[i++];
                    if (s == '"')
                    {
                        closed = true;
                        break;
                    }
                    if (s == '\\' && i < line.Length)
                    {
                        char e = line[i++];
                        sb.Append(e switch
                        {
                            'n' => '\n',
                            'r' => '\r',
                            't' => '\t',
                            '0' => '\0',
                            _ => e,
                        });
                    }
                    else
                    {
                        sb.Append(s);
                    }
                }
                if (!closed)
                    throw new AssemblerException(lineNumber, $"Unterminated string starting at column {start + 1}.");
                tokens.Add(new Token(TokenKind.String, sb.ToString(), 0, start));
            }
            else if (c == '.' && i + 1 < line.Length && IsIdentifierStart(line[i + 1]))
            {
                i++;
                while (i < line.Length && IsIdentifierPart(line[i])) i++;
                tokens.Add(new Token(TokenKind.Directive, line.Substring(start, i - start).ToLowerInvariant(), 0, start));
            }
            else if (IsIdentifierStart(c))
            {
                i++;
                // Allow '.' inside identifiers so that "lda.z" / "sta.a" are single tokens.
                while (i < line.Length && (IsIdentifierPart(line[i]) || (line[i] == '.' && i + 1 < line.Length && IsIdentifierStart(line[i + 1])))) i++;
                tokens.Add(new Token(TokenKind.Identifier, line.Substring(start, i - start), 0, start));
            }
            else
            {
                bool matched = false;
                foreach (var sym in TwoCharSymbols)
                {
                    if (string.CompareOrdinal(line, i, sym, 0, sym.Length) == 0)
                    {
                        tokens.Add(new Token(TokenKind.Symbol, sym, 0, start));
                        i += sym.Length;
                        matched = true;
                        break;
                    }
                }
                if (!matched)
                {
                    if (OneCharSymbols.IndexOf(c) < 0)
                        throw new AssemblerException(lineNumber, $"Unexpected character '{c}' at column {start + 1}.");
                    tokens.Add(new Token(TokenKind.Symbol, c.ToString(), 0, start));
                    i++;
                }
            }
        }
        return tokens;
    }

    private static long ParseNumber(ReadOnlySpan<char> digits, int radix, int lineNumber)
    {
        long value = 0;
        foreach (char d in digits)
        {
            int digit = d switch
            {
                >= '0' and <= '9' => d - '0',
                >= 'a' and <= 'f' => d - 'a' + 10,
                >= 'A' and <= 'F' => d - 'A' + 10,
                _ => 99,
            };
            if (digit >= radix)
                throw new AssemblerException(lineNumber, $"Invalid digit '{d}' in number.");
            value = value * radix + digit;
            if (value > 0xFFFF_FFFFL)
                throw new AssemblerException(lineNumber, "Numeric literal is too large.");
        }
        return value;
    }

    private static bool IsHexDigit(char c) => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
    private static bool IsIdentifierStart(char c) => char.IsLetter(c) || c == '_' || c == '@';
    private static bool IsIdentifierPart(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '@';
}
