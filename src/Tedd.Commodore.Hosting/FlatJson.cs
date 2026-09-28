using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Tedd.Commodore.Hosting;

/// <summary>
/// Minimal JSON reader/writer for a flat object of string values (<c>{ "key": "value", ... }</c>), which is all
/// the hosting layer persists. Hand-written so that the assembly has no dependency on System.Text.Json, which is
/// not part of the netstandard2.1 BCL that Unity ships.
/// </summary>
internal static class FlatJson
{
    /// <summary>Serialises the pairs as an indented JSON object (two-space indent, one pair per line).</summary>
    public static string Write(IEnumerable<KeyValuePair<string, string>> pairs)
    {
        var sb = new StringBuilder();
        sb.Append('{');
        bool first = true;
        foreach (var pair in pairs)
        {
            if (!first) sb.Append(',');
            sb.Append(Environment.NewLine).Append("  ");
            first = false;
            WriteString(sb, pair.Key);
            sb.Append(": ");
            WriteString(sb, pair.Value);
        }
        if (!first) sb.Append(Environment.NewLine);
        sb.Append('}');
        return sb.ToString();
    }

    private static void WriteString(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (c < 0x20)
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else
                        sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }

    /// <summary>
    /// Parses a JSON object whose values are strings (or <c>null</c>, which is skipped). Duplicate keys: the last
    /// one wins. Anything else (arrays, numbers, nested objects, trailing garbage) raises a
    /// <see cref="FormatException"/>.
    /// </summary>
    public static Dictionary<string, string> Read(string json)
    {
        if (json is null) throw new ArgumentNullException(nameof(json));
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var p = new Parser(json);
        p.SkipWhitespace();
        p.Expect('{');
        p.SkipWhitespace();
        if (p.Peek() == '}')
        {
            p.Next();
        }
        else
        {
            while (true)
            {
                p.SkipWhitespace();
                string key = p.ReadString();
                p.SkipWhitespace();
                p.Expect(':');
                p.SkipWhitespace();
                if (p.TryReadNull())
                    result.Remove(key);
                else
                    result[key] = p.ReadString();
                p.SkipWhitespace();
                char c = p.Next();
                if (c == ',') continue;
                if (c == '}') break;
                throw p.Error("expected ',' or '}'");
            }
        }
        p.SkipWhitespace();
        if (!p.AtEnd) throw p.Error("unexpected content after the object");
        return result;
    }

    private struct Parser
    {
        private readonly string _s;
        private int _i;

        public Parser(string s)
        {
            _s = s;
            _i = 0;
        }

        public bool AtEnd => _i >= _s.Length;

        public char Peek() => AtEnd ? '\0' : _s[_i];

        public char Next()
        {
            if (AtEnd) throw Error("unexpected end of input");
            return _s[_i++];
        }

        public void Expect(char c)
        {
            if (Next() != c) throw Error("expected '" + c + "'");
        }

        public void SkipWhitespace()
        {
            while (!AtEnd)
            {
                char c = _s[_i];
                if (c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == (char)0xFEFF) _i++; // 0xFEFF: byte order mark
                else break;
            }
        }

        public bool TryReadNull()
        {
            if (string.CompareOrdinal(_s, _i, "null", 0, 4) != 0) return false;
            _i += 4;
            return true;
        }

        public string ReadString()
        {
            Expect('"');
            var sb = new StringBuilder();
            while (true)
            {
                char c = Next();
                if (c == '"') return sb.ToString();
                if (c != '\\')
                {
                    sb.Append(c);
                    continue;
                }
                char e = Next();
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                    {
                        if (_i + 4 > _s.Length) throw Error("truncated \\u escape");
                        if (!int.TryParse(_s.Substring(_i, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int code))
                            throw Error("invalid \\u escape");
                        _i += 4;
                        sb.Append((char)code);
                        break;
                    }
                    default:
                        throw Error("invalid escape '\\" + e + "'");
                }
            }
        }

        public FormatException Error(string message) => new FormatException("Invalid JSON at position " + _i + ": " + message);
    }
}
