using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using Tedd.Commodore.Emulator.C64;

namespace Tedd.Commodore.Tests.Machine;

/// <summary>
/// Runs Wolfgang Lorenz's "C64 Emulator Test Suite" (v2.15, public domain) on the full machine with the real
/// ROMs. Every test program is a PRG that prints its name, runs, prints " - ok" and then asks the KERNAL to load
/// the next test; on a failure it prints the details and waits for a key. The harness injects each program
/// itself, starts it at the SYS entry ($080E, or $0801 for "start") like the official "testsuite stub" does, and traps the KERNAL LOAD entry ($FFD5)
/// to detect success and GETIN ($FFE4) to detect failure.
///
/// The PRG files are not part of the repository: point LORENZ_TESTS at a directory with the *.prg files (from
/// the VICE source tree, testprogs/general/Lorenz-2.15/src). Tests are skipped when the directory is missing.
/// Run with `dotnet test -c Release --filter Category=Lorenz` (the whole suite is ~4.7 billion cycles).
/// </summary>
[TestFixture]
[Category("Lorenz")]
[Category("Slow")]
public class LorenzTestSuiteTests
{
    private const ushort LoadTrap = 0xFFD5;   // KERNAL LOAD jump table entry (the tests JSR $FFD5 after SETNAM/SETLFS)
    private const ushort GetinTrap = 0xFFE4;  // KERNAL GETIN jump table entry
    private const ushort ExitTrap1 = 0xA474;  // BASIC warm start (the tests jump here after STOP)
    private const ushort ExitTrap2 = 0x8000;
    private const long CycleBudgetPerTest = 400_000_000; // about 400 emulated seconds

    /// <summary>The CPU part of the suite in the official chain order ("start" .. "sbcb-eb").</summary>
    private static readonly string[] CpuTests =
    (
        "start ldab ldaz ldazx ldaa ldaax ldaay ldaix ldaiy staz stazx staa staax staay staix staiy ldxb ldxz ldxzy ldxa ldxay " +
        "stxz stxzy stxa ldyb ldyz ldyzx ldya ldyax styz styzx stya taxn tayn txan tyan tsxn txsn phan plan phpn plpn inxn inyn " +
        "dexn deyn incz inczx inca incax decz deczx deca decax asln aslz aslzx asla aslax lsrn lsrz lsrzx lsra lsrax roln rolz " +
        "rolzx rola rolax rorn rorz rorzx rora rorax andb andz andzx anda andax anday andix andiy orab oraz orazx oraa oraax " +
        "oraay oraix oraiy eorb eorz eorzx eora eorax eoray eorix eoriy clcn secn cldn sedn clin sein clvn adcb adcz adczx adca " +
        "adcax adcay adcix adciy sbcb sbcz sbczx sbca sbcax sbcay sbcix sbciy cmpb cmpz cmpzx cmpa cmpax cmpay cmpix cmpiy cpxb " +
        "cpxz cpxa cpyb cpyz cpya bitz bita brkn rtin jsrw rtsn jmpw jmpi beqr bner bmir bplr bcsr bccr bvsr bvcr nopn nopb nopz " +
        "nopzx nopa nopax asoz asozx asoa asoax asoay asoix asoiy rlaz rlazx rlaa rlaax rlaay rlaix rlaiy lsez lsezx lsea lseax " +
        "lseay lseix lseiy rraz rrazx rraa rraax rraay rraix rraiy dcmz dcmzx dcma dcmax dcmay dcmix dcmiy insz inszx insa insax " +
        "insay insix insiy laxz laxzy laxa laxay laxix laxiy axsz axszy axsa axsix alrb arrb sbxb shaay shaiy shxay shyax shsay " +
        "ancb lasay sbcb-eb"
    ).Split(' ', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Interrupt/trap behaviour and the unstable opcode variants.</summary>
    private static readonly string[] TrapTests =
    (
        "trap1 trap2 trap3 trap4 trap5 trap6 trap7 trap8 trap9 trap10 trap11 trap12 trap13 trap14 trap15 trap16 trap17 " +
        "branchwrap mmufetch mmu cpuport"
    ).Split(' ', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Cycle timing of the CPU and the CIAs (old 6526 variants).</summary>
    private static readonly string[] TimingTests =
    (
        "cputiming irq nmi cia1tb123 cia2tb123 cia1pb6 cia1pb7 cia2pb6 cia2pb7 cia1tab cia1ta cia1tb cia2ta cia2tb " +
        "icr01 imr flipos oneshot cntdef cnto2 loadth"
    ).Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private static string? Directory()
    {
        var dir = Environment.GetEnvironmentVariable("LORENZ_TESTS");
        if (!string.IsNullOrEmpty(dir) && System.IO.Directory.Exists(dir)) return dir;
        var local = Path.Combine(AppContext.BaseDirectory, "TestData", "lorenz");
        return System.IO.Directory.Exists(local) ? local : null;
    }

    public enum Outcome { Passed, Failed, Timeout, Missing }

    public sealed record Result(string Name, Outcome Outcome, long Cycles, string Screen, string? NextRequested);

    [Test]
    public void Cpu_Opcodes() => Run(CpuTests, "lorenz_cpu");

    [Test]
    public void Traps_And_Unstable_Opcodes() => Run(TrapTests, "lorenz_traps");

    [Test]
    public void Cpu_And_Cia_Timing() => Run(TimingTests, "lorenz_timing");

    private static void Run(string[] names, string reportName)
    {
        var dir = Directory();
        if (dir is null) Assert.Ignore("LORENZ_TESTS not set; Lorenz test programs not available");
        var roms = RomSet.TryLoadDefault();
        if (roms is null) Assert.Ignore("ROM images not available");
        var only = Environment.GetEnvironmentVariable("LORENZ_ONLY");
        if (!string.IsNullOrEmpty(only))
            names = names.Where(n => only.Split(',').Contains(n, StringComparer.OrdinalIgnoreCase)).ToArray();

        var c64 = new C64(roms!);
        Assert.That(c64.WaitForBasicReady(400), Is.True, "BASIC did not boot");

        var results = new List<Result>();
        bool needReset = false;
        foreach (var name in names)
        {
            var path = Path.Combine(dir!, name + ".prg");
            if (!File.Exists(path))
            {
                results.Add(new Result(name, Outcome.Missing, 0, "", null));
                continue;
            }
            var result = RunOne(c64, name, File.ReadAllBytes(path), needReset);
            needReset = result.Outcome != Outcome.Passed;
            results.Add(result);
            TestContext.Out.WriteLine($"{result.Outcome,-8} {name,-12} {result.Cycles,12:N0} cycles" +
                                      (result.Outcome == Outcome.Passed ? "" : "\n" + Indent(result.Screen)));
        }

        var report = new StringBuilder();
        foreach (var r in results)
            report.AppendLine($"{r.Outcome,-8} {r.Name}");
        var reportDir = Path.Combine(AppContext.BaseDirectory, "TestResults");
        System.IO.Directory.CreateDirectory(reportDir);
        File.WriteAllText(Path.Combine(reportDir, reportName + ".txt"), report.ToString());

        var failed = results.Where(r => r.Outcome != Outcome.Passed).Select(r => $"{r.Name} ({r.Outcome})").ToList();
        Assert.That(failed, Is.Empty, "Failed Lorenz tests: " + string.Join(", ", failed));
    }

    private static string Indent(string text) => "    " + text.TrimEnd().Replace("\n", "\n    ");

    /// <summary>
    /// Injects the program like LOAD would, then starts it the way the official stub does (S=$FD, I set, PC at the
    /// SYS entry) and runs until it loads the next test (pass), waits
    /// for a key (fail), exits or exhausts its cycle budget.
    /// </summary>
    private static Result RunOne(C64 c64, string name, byte[] prg, bool resetFirst)
    {
        // Like the real chain, programs follow each other without a reset. Only a failed program leaves the
        // machine in an arbitrary state (own vectors, CIA setup...), so the next one starts from a power cycle.
        if (resetFirst)
        {
            c64.Reset(hard: true);
            if (!c64.WaitForBasicReady(400))
                return new Result(name, Outcome.Failed, 0, "BASIC did not come up after reset: " + c64.GetScreenText(), null);
        }

        int load = prg[0] | (prg[1] << 8);
        for (int i = 2; i < prg.Length; i++)
            c64.Memory.Ram[(load + i - 2) & 0xFFFF] = prg[i];
        int end = load + prg.Length - 2;
        c64.Memory.Ram[0x2D] = c64.Memory.Ram[0x2F] = c64.Memory.Ram[0x31] = c64.Memory.Ram[0xAE] = (byte)end;
        c64.Memory.Ram[0x2E] = c64.Memory.Ram[0x30] = c64.Memory.Ram[0x32] = c64.Memory.Ram[0xAF] = (byte)(end >> 8);

        // Clear the screen so the report only shows this test's output.
        int screen = c64.ScreenAddress;
        for (int i = 0; i < 1000; i++) c64.Memory.Ram[screen + i] = 0x20;
        c64.Memory.Ram[0xD3] = 0; // cursor column
        c64.Memory.Ram[0xD6] = 0; // cursor row
        c64.Memory.Ram[0xC6] = 0; // keyboard buffer empty

        // Most tests start with a BASIC stub "2016 SYS2062"; "start" is raw machine code at $0801.
        bool hasStub = prg.Length > 10 && prg[2] == 0x0B && prg[3] == 0x08 && prg[6] == 0x9E;
        ushort entry = hasStub ? (ushort)0x080E : (ushort)0x0801;

        // Run the CPU to an instruction boundary, then hijack it like the official stub does.
        while (!c64.Cpu.AtInstructionBoundary) c64.Clock();
        c64.Cpu.S = 0xFD;
        c64.Cpu.P = 0x04;
        c64.Cpu.PC = entry;

        long start = c64.Cycles;
        int getinCalls = 0;
        while (c64.Cycles - start < CycleBudgetPerTest)
        {
            c64.Clock();
            if (!c64.Cpu.AtInstructionBoundary) continue;
            ushort pc = c64.Cpu.PC;
            if (pc == LoadTrap)
            {
                int len = c64.Memory.Ram[0xB7];
                int ptr = c64.Memory.Ram[0xBB] | (c64.Memory.Ram[0xBC] << 8);
                var sb = new StringBuilder();
                for (int i = 0; i < len; i++) sb.Append((char)(c64.Memory.Ram[(ptr + i) & 0xFFFF] & 0x7F));
                return new Result(name, Outcome.Passed, c64.Cycles - start, c64.GetScreenText(), sb.ToString().ToLowerInvariant());
            }
            if (pc == GetinTrap)
            {
                // The test waits for a key after reporting an error. Answer with STOP (3) so it exits cleanly.
                if (++getinCalls == 1)
                    return new Result(name, Outcome.Failed, c64.Cycles - start, c64.GetScreenText(), null);
            }
            if (pc == ExitTrap1 || pc == ExitTrap2)
                return new Result(name, Outcome.Failed, c64.Cycles - start, c64.GetScreenText(), null);
        }
        return new Result(name, Outcome.Timeout, c64.Cycles - start, c64.GetScreenText(), null);
    }
}
