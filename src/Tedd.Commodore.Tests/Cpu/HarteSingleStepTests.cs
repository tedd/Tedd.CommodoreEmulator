using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using NUnit.Framework;
using Tedd.Commodore.Emulator.Cpu;
using Tedd.Commodore.Tests.Support;

namespace Tedd.Commodore.Tests.Cpu;

/// <summary>
/// Runs the SingleStepTests/65x02 "6502" JSON test vectors (10 000 randomised tests per opcode with the exact
/// bus activity of every cycle) if they are available. Point the environment variable HARTE_6502_TESTS at a
/// directory containing 00.json .. ff.json (https://github.com/SingleStepTests/65x02/tree/main/6502/v1).
/// The tests are skipped (Inconclusive) when the data is not present, so the suite still runs offline.
/// </summary>
[TestFixture]
[Category("Harte")]
public class HarteSingleStepTests
{
    private static string? Directory()
    {
        var dir = Environment.GetEnvironmentVariable("HARTE_6502_TESTS");
        if (!string.IsNullOrEmpty(dir) && System.IO.Directory.Exists(dir))
            return dir;
        return null;
    }

    public static IEnumerable<TestCaseData> Opcodes()
    {
        for (int i = 0; i < 256; i++)
            yield return new TestCaseData((byte)i).SetName($"Opcode_{i:X2}_{Cpu6502.GetOpcodeInfo((byte)i).Mnemonic}");
    }

    [TestCaseSource(nameof(Opcodes))]
    public void Opcode(byte opcode)
    {
        var dir = Directory();
        if (dir is null)
            Assert.Inconclusive("HARTE_6502_TESTS not set; skipping SingleStepTests vectors.");
        var path = Path.Combine(dir!, $"{opcode:x2}.json");
        if (!File.Exists(path))
            Assert.Inconclusive($"Missing {path}");

        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        var failures = new StringBuilder();
        int count = 0, failed = 0;
        foreach (var test in doc.RootElement.EnumerateArray())
        {
            count++;
            var error = RunOne(test);
            if (error is not null)
            {
                failed++;
                if (failed <= 5)
                    failures.AppendLine(error);
            }
        }
        Assert.That(failed, Is.EqualTo(0), $"{failed}/{count} tests failed for opcode {opcode:X2}:\n{failures}");
    }

    private static string? RunOne(JsonElement test)
    {
        var name = test.GetProperty("name").GetString();
        var init = test.GetProperty("initial");
        var final = test.GetProperty("final");
        var bus = new RecordingBus();
        foreach (var pair in init.GetProperty("ram").EnumerateArray())
            bus.Ram[pair[0].GetInt32()] = (byte)pair[1].GetInt32();

        var cpu = new Cpu6502(bus)
        {
            PC = (ushort)init.GetProperty("pc").GetInt32(),
            S = (byte)init.GetProperty("s").GetInt32(),
            A = (byte)init.GetProperty("a").GetInt32(),
            X = (byte)init.GetProperty("x").GetInt32(),
            Y = (byte)init.GetProperty("y").GetInt32(),
            P = (byte)init.GetProperty("p").GetInt32(),
        };

        var cycles = test.GetProperty("cycles");
        int n = cycles.GetArrayLength();
        for (int i = 0; i < n; i++)
            cpu.Clock();

        var sb = new StringBuilder();
        if (bus.Trace.Count != n)
            sb.Append($" trace length {bus.Trace.Count} != {n};");
        int idx = 0;
        foreach (var c in cycles.EnumerateArray())
        {
            if (idx >= bus.Trace.Count) break;
            var expected = new BusAccess((ushort)c[0].GetInt32(), (byte)c[1].GetInt32(), c[2].GetString() == "write");
            if (bus.Trace[idx] != expected)
                sb.Append($" cycle {idx + 1}: expected {expected} got {bus.Trace[idx]};");
            idx++;
        }

        Check(sb, "pc", final.GetProperty("pc").GetInt32(), cpu.PC);
        Check(sb, "s", final.GetProperty("s").GetInt32(), cpu.S);
        Check(sb, "a", final.GetProperty("a").GetInt32(), cpu.A);
        Check(sb, "x", final.GetProperty("x").GetInt32(), cpu.X);
        Check(sb, "y", final.GetProperty("y").GetInt32(), cpu.Y);
        Check(sb, "p", final.GetProperty("p").GetInt32(), cpu.P);
        foreach (var pair in final.GetProperty("ram").EnumerateArray())
        {
            int addr = pair[0].GetInt32();
            int val = pair[1].GetInt32();
            if (bus.Ram[addr] != val)
                sb.Append($" ram[{addr:X4}] expected {val:X2} got {bus.Ram[addr]:X2};");
        }
        if (!cpu.AtInstructionBoundary && !cpu.Jammed)
            sb.Append(" instruction not complete after listed cycles;");

        return sb.Length == 0 ? null : $"{name}:{sb}";
    }

    private static void Check(StringBuilder sb, string what, int expected, int actual)
    {
        if (expected != actual)
            sb.Append($" {what} expected {expected:X2} got {actual:X2};");
    }
}
