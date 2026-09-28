using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using NUnit.Framework;
using Tedd.Commodore.Emulator.Cpu;

namespace Tedd.Commodore.Tests.Cpu;

/// <summary>
/// Runs the SingleStepTests/z80 JSON vectors (1000 randomised tests per opcode: registers, memory, I/O port
/// reads and the total T-state count) if they are available. Point the environment variable HARTE_Z80_TESTS at
/// a directory containing the v1 files ("00.json", "cb 00.json", "ed 40.json", "dd cb __ 00.json" ...,
/// https://github.com/SingleStepTests/z80/tree/main/v1). The tests are skipped (Inconclusive) when the data is
/// not present, so the suite still runs offline.
/// </summary>
[TestFixture]
[Category("Harte")]
public class HarteZ80Tests
{
    private static string? Directory()
    {
        var dir = Environment.GetEnvironmentVariable("HARTE_Z80_TESTS");
        if (!string.IsNullOrEmpty(dir) && System.IO.Directory.Exists(dir))
            return dir;
        return null;
    }

    public static IEnumerable<TestCaseData> Files()
    {
        for (int i = 0; i < 256; i++)
            yield return new TestCaseData($"{i:x2}").SetName($"Main_{i:X2}");
        for (int i = 0; i < 256; i++)
            yield return new TestCaseData($"cb {i:x2}").SetName($"CB_{i:X2}");
        for (int i = 0x40; i < 0xC0; i++)
            yield return new TestCaseData($"ed {i:x2}").SetName($"ED_{i:X2}");
        foreach (var prefix in new[] { "dd", "fd" })
        {
            for (int i = 0; i < 256; i++)
            {
                if (i is 0xCB or 0xDD or 0xED or 0xFD) continue;
                yield return new TestCaseData($"{prefix} {i:x2}").SetName($"{prefix.ToUpperInvariant()}_{i:X2}");
            }
            for (int i = 0; i < 256; i++)
                yield return new TestCaseData($"{prefix} cb __ {i:x2}").SetName($"{prefix.ToUpperInvariant()}CB_{i:X2}");
        }
    }

    [TestCaseSource(nameof(Files))]
    public void Vectors(string file)
    {
        var dir = Directory();
        if (dir is null)
            Assert.Inconclusive("HARTE_Z80_TESTS not set; skipping SingleStepTests/z80 vectors.");
        var path = Path.Combine(dir!, file + ".json");
        if (!File.Exists(path))
            Assert.Inconclusive($"Missing {path}");

        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        var failures = new StringBuilder();
        int count = 0, failed = 0;
        var bus = new Z80TestBus();
        foreach (var test in doc.RootElement.EnumerateArray())
        {
            count++;
            var error = RunOne(test, bus);
            if (error is not null)
            {
                failed++;
                if (failed <= 5)
                    failures.AppendLine(error);
            }
        }
        Assert.That(failed, Is.EqualTo(0), $"{failed}/{count} tests failed in {file}.json:\n{failures}");
    }

    /// <summary>64K RAM plus the scripted port reads of one test.</summary>
    public sealed class Z80TestBus : IZ80Bus
    {
        public readonly byte[] Ram = new byte[65536];
        public readonly List<(ushort Port, byte Value)> Reads = new();
        public readonly List<(ushort Port, byte Value)> Writes = new();
        private int _nextRead;

        public void Clear()
        {
            Array.Clear(Ram, 0, Ram.Length);
            Reads.Clear();
            Writes.Clear();
            _nextRead = 0;
        }

        public byte Read(ushort address) => Ram[address];
        public void Write(ushort address, byte value) => Ram[address] = value;

        public byte In(ushort port)
        {
            if (_nextRead < Reads.Count)
            {
                var (p, v) = Reads[_nextRead++];
                if (p == port) return v;
            }
            return 0xFF;
        }

        public void Out(ushort port, byte value) => Writes.Add((port, value));
    }

    private static string? RunOne(JsonElement test, Z80TestBus bus)
    {
        var name = test.GetProperty("name").GetString();
        var init = test.GetProperty("initial");
        var final = test.GetProperty("final");
        bus.Clear();
        foreach (var pair in init.GetProperty("ram").EnumerateArray())
            bus.Ram[pair[0].GetInt32()] = (byte)pair[1].GetInt32();
        if (test.TryGetProperty("ports", out var ports))
        {
            foreach (var p in ports.EnumerateArray())
                if (p[2].GetString() == "r")
                    bus.Reads.Add(((ushort)p[0].GetInt32(), (byte)p[1].GetInt32()));
        }

        var cpu = new Z80(bus);
        Load(cpu, init);

        int expectedCycles = test.GetProperty("cycles").GetArrayLength();
        int cycles = 0;
        int steps = 0;
        while (cycles < expectedCycles && steps < 4)
        {
            cycles += cpu.Step();
            steps++;
        }

        var sb = new StringBuilder();
        if (cycles != expectedCycles)
            sb.Append($" cycles {cycles} != {expectedCycles};");
        Check(sb, "pc", final.GetProperty("pc").GetInt32(), cpu.PC);
        Check(sb, "sp", final.GetProperty("sp").GetInt32(), cpu.SP);
        Check(sb, "a", final.GetProperty("a").GetInt32(), cpu.A);
        Check(sb, "f", final.GetProperty("f").GetInt32(), cpu.F);
        Check(sb, "b", final.GetProperty("b").GetInt32(), cpu.B);
        Check(sb, "c", final.GetProperty("c").GetInt32(), cpu.C);
        Check(sb, "d", final.GetProperty("d").GetInt32(), cpu.D);
        Check(sb, "e", final.GetProperty("e").GetInt32(), cpu.E);
        Check(sb, "h", final.GetProperty("h").GetInt32(), cpu.H);
        Check(sb, "l", final.GetProperty("l").GetInt32(), cpu.L);
        Check(sb, "i", final.GetProperty("i").GetInt32(), cpu.I);
        Check(sb, "r", final.GetProperty("r").GetInt32(), cpu.R);
        Check(sb, "ix", final.GetProperty("ix").GetInt32(), cpu.IX);
        Check(sb, "iy", final.GetProperty("iy").GetInt32(), cpu.IY);
        Check(sb, "af_", final.GetProperty("af_").GetInt32(), cpu.AF2);
        Check(sb, "bc_", final.GetProperty("bc_").GetInt32(), cpu.BC2);
        Check(sb, "de_", final.GetProperty("de_").GetInt32(), cpu.DE2);
        Check(sb, "hl_", final.GetProperty("hl_").GetInt32(), cpu.HL2);
        Check(sb, "wz", final.GetProperty("wz").GetInt32(), cpu.WZ);
        Check(sb, "q", final.GetProperty("q").GetInt32(), cpu.Q);
        Check(sb, "im", final.GetProperty("im").GetInt32(), cpu.InterruptMode);
        Check(sb, "iff1", final.GetProperty("iff1").GetInt32(), cpu.Iff1 ? 1 : 0);
        Check(sb, "iff2", final.GetProperty("iff2").GetInt32(), cpu.Iff2 ? 1 : 0);
        foreach (var pair in final.GetProperty("ram").EnumerateArray())
        {
            int addr = pair[0].GetInt32();
            int val = pair[1].GetInt32();
            if (bus.Ram[addr] != val)
                sb.Append($" ram[{addr:X4}] expected {val:X2} got {bus.Ram[addr]:X2};");
        }
        if (test.TryGetProperty("ports", out ports))
        {
            int w = 0;
            foreach (var p in ports.EnumerateArray())
            {
                if (p[2].GetString() != "w") continue;
                var expected = ((ushort)p[0].GetInt32(), (byte)p[1].GetInt32());
                if (w >= bus.Writes.Count || bus.Writes[w] != expected)
                    sb.Append($" port write {w}: expected {expected.Item1:X4}={expected.Item2:X2} got {(w < bus.Writes.Count ? $"{bus.Writes[w].Port:X4}={bus.Writes[w].Value:X2}" : "nothing")};");
                w++;
            }
        }
        return sb.Length == 0 ? null : $"{name}:{sb}";
    }

    private static void Load(Z80 cpu, JsonElement s)
    {
        cpu.PC = (ushort)s.GetProperty("pc").GetInt32();
        cpu.SP = (ushort)s.GetProperty("sp").GetInt32();
        cpu.A = (byte)s.GetProperty("a").GetInt32();
        cpu.F = (byte)s.GetProperty("f").GetInt32();
        cpu.B = (byte)s.GetProperty("b").GetInt32();
        cpu.C = (byte)s.GetProperty("c").GetInt32();
        cpu.D = (byte)s.GetProperty("d").GetInt32();
        cpu.E = (byte)s.GetProperty("e").GetInt32();
        cpu.H = (byte)s.GetProperty("h").GetInt32();
        cpu.L = (byte)s.GetProperty("l").GetInt32();
        cpu.I = (byte)s.GetProperty("i").GetInt32();
        cpu.R = (byte)s.GetProperty("r").GetInt32();
        cpu.IX = (ushort)s.GetProperty("ix").GetInt32();
        cpu.IY = (ushort)s.GetProperty("iy").GetInt32();
        cpu.AF2 = (ushort)s.GetProperty("af_").GetInt32();
        cpu.BC2 = (ushort)s.GetProperty("bc_").GetInt32();
        cpu.DE2 = (ushort)s.GetProperty("de_").GetInt32();
        cpu.HL2 = (ushort)s.GetProperty("hl_").GetInt32();
        cpu.WZ = (ushort)s.GetProperty("wz").GetInt32();
        cpu.Q = (byte)s.GetProperty("q").GetInt32();
        cpu.InterruptMode = s.GetProperty("im").GetInt32();
        cpu.Iff1 = s.GetProperty("iff1").GetInt32() != 0;
        cpu.Iff2 = s.GetProperty("iff2").GetInt32() != 0;
    }

    private static void Check(StringBuilder sb, string what, int expected, int actual)
    {
        if (expected != actual)
            sb.Append($" {what} expected {expected:X} got {actual:X};");
    }
}
