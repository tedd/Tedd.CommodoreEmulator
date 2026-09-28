using System;

namespace Tedd.Commodore.Emulator.Tools;

/// <summary>
/// Thrown by <see cref="Assembler"/> for syntax errors, undefined symbols, out-of-range operands and other
/// assembly-time errors. <see cref="Line"/> is the 1-based source line the error was detected on (0 when the
/// error is not tied to a line), and <see cref="Exception.Message"/> always starts with "Line N: ".
/// </summary>
public sealed class AssemblerException : Exception
{
    /// <summary>1-based source line number, or 0 if the error is not associated with a line.</summary>
    public int Line { get; }

    /// <summary>The error text without the "Line N: " prefix.</summary>
    public string Detail { get; }

    public AssemblerException(int line, string detail)
        : base(line > 0 ? $"Line {line}: {detail}" : detail)
    {
        Line = line;
        Detail = detail;
    }
}
