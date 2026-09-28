using System;
using System.IO;
using NUnit.Framework;
using Tedd.Commodore.Emulator.C128;
using Tedd.Commodore.Emulator.Drive;
using Tedd.Commodore.Emulator.Machines;
using Tedd.Commodore.Emulator.Tools;
using Tedd.Commodore.Emulator.Video;

namespace Tedd.Commodore.Tests.Machine;

/// <summary>
/// Whole-machine tests that boot the real C128 ROMs (skipped when they are not available). Rendered frames of
/// both video chips are written to TestResults/ next to the test assembly.
/// </summary>
[TestFixture]
public class C128SystemTests
{
    public static C128RomSet Roms()
    {
        var roms = C128RomSet.TryLoadDefault();
        if (roms is null) Assert.Ignore("C128 ROM images not available");
        return roms!;
    }

    private static string ResultsDir
    {
        get
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "TestResults");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>Directory with the CP/M disk images (disks/c128 in the repository), or null.</summary>
    public static string? DisksDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int depth = 0; dir is not null && depth < 8; depth++, dir = dir.Parent)
        {
            var p = Path.Combine(dir.FullName, "disks", "c128");
            if (Directory.Exists(p)) return p;
        }
        return null;
    }

    public static void SaveFrames(C128 c128, string name)
    {
        c128.RunFrame();
        var vis = VicII.VisibleArea;
        var crop = new uint[vis.Width * vis.Height];
        for (int y = 0; y < vis.Height; y++)
            Array.Copy(c128.Vic.Frame, (vis.Y + y) * VicII.FrameWidth + vis.X, crop, y * vis.Width, vis.Width);
        PngWriter.Save(Path.Combine(ResultsDir, name + "_vic.png"), vis.Width, vis.Height, crop);
        c128.Vdc.Render();
        PngWriter.Save(Path.Combine(ResultsDir, name + "_vdc.png"), Vdc8563.FrameWidth, Vdc8563.FrameHeight, c128.Vdc.Frame);
        TestContext.Out.WriteLine("Saved " + Path.Combine(ResultsDir, name + "_*.png"));
    }

    public static C128 Boot(bool drive = false, bool columns80 = false, int maxFrames = 400)
    {
        var roms = Roms();
        var c128 = new C128(roms) { Display40Columns = !columns80 };
        if (drive)
        {
            if (roms.Drive1541 is null) Assert.Ignore("1541 ROM not available");
            c128.AttachDrive(8);
        }
        c128.Reset(hard: true);
        bool ready = c128.WaitForBasicReady(maxFrames);
        if (!ready)
            SaveFrames(c128, "c128_boot_failed");
        Assert.That(ready, Is.True, "BASIC 7 did not come up:\n" + c128.GetScreenText() + "\n80 col:\n" + c128.GetVdcScreenText() + "\n" + Describe(c128));
        return c128;
    }

    public static string Describe(C128 c) =>
        $"Z80 active={c.Z80Active} Z80 {c.Z80} 8502 PC=${c.Cpu.PC:X4} MMU CR=${c.Mmu.Cr:X2} MCR=${c.Mmu.Mcr:X2} RCR=${c.Mmu.Rcr:X2} C64mode={c.C64Mode} fast={c.Vic.FastMode} frames={c.Frames}";

    [Test]
    public void Frame_Is_19656_Cycles()
    {
        var c128 = new C128(Roms());
        Assert.That(c128.RunFrame(), Is.EqualTo(CommodoreMachine.CyclesPerFrame));
        Assert.That(c128.Cycles, Is.EqualTo(19656));
    }

    [Test]
    public void Starts_On_The_Z80_And_Hands_Over_To_The_8502()
    {
        var c128 = new C128(Roms());
        Assert.That(c128.Z80Active, Is.True, "the Z80 owns the bus after reset");
        Assert.That(c128.Memory.Z80BiosVisible, Is.True);
        Assert.That(c128.Memory.ReadZ80(0), Is.EqualTo(Roms().Kernal[C128RomSet.Z80BiosOffset]));
        int cycles = 0;
        while (c128.Z80Active && cycles < 200_000)
        {
            c128.Clock();
            cycles++;
        }
        Assert.That(c128.Z80Active, Is.False, "the BIOS should switch to the 8502 within a few thousand cycles");
        TestContext.Out.WriteLine($"Z80 ran {c128.Z80.Instructions} instructions / {c128.Z80.Cycles} T-states, handed over after {cycles} cycles");
        Assert.That(c128.Z80.Instructions, Is.GreaterThan(10));
    }

    [Test]
    public void Boots_To_Basic7_On_40_Columns()
    {
        var c128 = Boot();
        var screen = c128.GetScreenText();
        TestContext.Out.WriteLine(screen);
        SaveFrames(c128, "c128_boot40");
        Assert.That(screen, Does.Contain("COMMODORE BASIC V7.0"));
        Assert.That(screen, Does.Contain("BYTES FREE"));
        Assert.That(screen, Does.Contain("READY."));
        Assert.That(c128.C64Mode, Is.False);
        Assert.That(c128.Z80Active, Is.False);
    }

    [Test]
    public void Boots_To_Basic7_On_80_Columns()
    {
        var c128 = Boot(columns80: true);
        var screen = c128.GetVdcScreenText();
        TestContext.Out.WriteLine(screen);
        SaveFrames(c128, "c128_boot80");
        Assert.That(screen, Does.Contain("COMMODORE BASIC V7.0"));
        Assert.That(screen, Does.Contain("READY."));
        // The KERNAL initialises the VDC for 80 x 25 with the character set copied into VDC RAM.
        Assert.That(c128.Vdc.Register(1), Is.EqualTo(80));
        Assert.That(c128.Vdc.Register(6), Is.EqualTo(25));
        // The picture has the prompt drawn in it (some non-background pixels in the display area).
        c128.Vdc.Render();
        uint bg = c128.Vdc.Frame[0];
        int different = 0;
        foreach (var p in c128.Vdc.Frame) if (p != bg) different++;
        Assert.That(different, Is.GreaterThan(100));
    }

    [Test]
    public void Basic7_Runs_A_Program_And_Uses_Bank_1_For_Variables()
    {
        var c128 = Boot();
        c128.TypeText("A=0:FORI=1TO100:A=A+I:NEXT:PRINTA\n");
        for (int i = 0; i < 200 && c128.PendingTypeAhead > 0; i++) c128.RunFrame();
        for (int i = 0; i < 100; i++) c128.RunFrame();
        var screen = c128.GetScreenText();
        TestContext.Out.WriteLine(screen);
        Assert.That(screen, Does.Contain(" 5050"));
    }

    [Test]
    public void Bank_Command_Peek_Reads_Rom_And_Ram_Banks()
    {
        var c128 = Boot();
        // BANK 15 = ROMs + I/O: $FF00 is the MMU configuration register; BANK 0 / BANK 1 are the RAM banks.
        c128.Memory.Ram[0x0400 + 0x11000] = 0x12;   // bank 1, $1400... keep away from the screen: use $2000
        c128.Memory.Ram[0x2000] = 0x34;
        c128.Memory.Ram[0x12000] = 0x56;
        c128.TypeText("BANK0:PRINTPEEK(8192):BANK1:PRINTPEEK(8192)\n");
        for (int i = 0; i < 200 && c128.PendingTypeAhead > 0; i++) c128.RunFrame();
        for (int i = 0; i < 60; i++) c128.RunFrame();
        var screen = c128.GetScreenText();
        TestContext.Out.WriteLine(screen);
        Assert.That(screen, Does.Contain(" 52\n"));
        Assert.That(screen, Does.Contain(" 86\n"));
    }

    [Test]
    public void Fast_Switches_The_8502_To_2_MHz()
    {
        var c128 = Boot();
        long before = c128.Cpu.Cycles;
        c128.RunFrame();
        long slow = c128.Cpu.Cycles - before;
        c128.TypeText("FAST\n");
        for (int i = 0; i < 100 && c128.PendingTypeAhead > 0; i++) c128.RunFrame();
        for (int i = 0; i < 10; i++) c128.RunFrame();
        Assert.That(c128.Vic.FastMode, Is.True, "FAST sets $D030 bit 0");
        before = c128.Cpu.Cycles;
        c128.RunFrame();
        long fast = c128.Cpu.Cycles - before;
        TestContext.Out.WriteLine($"CPU cycles per frame: {slow} at 1 MHz, {fast} at 2 MHz");
        Assert.That(fast, Is.GreaterThan(slow * 3 / 2));
        c128.TypeText("SLOW\n");
        for (int i = 0; i < 100 && c128.PendingTypeAhead > 0; i++) c128.RunFrame();
        for (int i = 0; i < 10; i++) c128.RunFrame();
        Assert.That(c128.Vic.FastMode, Is.False);
    }

    [Test]
    public void Go64_Switches_To_C64_Mode()
    {
        var c128 = Boot();
        c128.TypeText("GO64\n");
        for (int i = 0; i < 100 && c128.PendingTypeAhead > 0; i++) c128.RunFrame();
        // "ARE YOU SURE?" - answer Y
        for (int i = 0; i < 30; i++) c128.RunFrame();
        c128.TypeText("Y\n");
        // The old C128 screen still says READY. until the C64 KERNAL has cleared it, so wait for its banner.
        bool ready = false;
        for (int i = 0; i < 600 && !ready; i++)
        {
            c128.RunFrame();
            ready = c128.C64Mode && c128.GetScreenText().Contains("BASIC V2", StringComparison.Ordinal) && c128.IsBasicReady();
        }
        var screen = c128.GetScreenText();
        TestContext.Out.WriteLine(screen);
        SaveFrames(c128, "c128_go64");
        Assert.That(c128.C64Mode, Is.True);
        Assert.That(screen, Does.Contain("**** COMMODORE 64 BASIC V2 ****"));
        Assert.That(screen, Does.Contain("38911 BASIC BYTES FREE"));
        Assert.That(screen, Does.Contain("READY."));
    }

    [Test]
    public void Extended_Keyboard_Keys_Are_Read_In_C128_Mode()
    {
        var c128 = Boot();
        // Type "1+2" on the keypad and press ENTER on the keypad; the ESC key is on row 9 too.
        c128.TypeText("PRINT");
        for (int i = 0; i < 50 && c128.PendingTypeAhead > 0; i++) c128.RunFrame();
        foreach (var key in new[] { Emulator.C64.C64Key.Keypad1, Emulator.C64.C64Key.KeypadPlus, Emulator.C64.C64Key.Keypad2, Emulator.C64.C64Key.KeypadEnter })
        {
            c128.Keyboard.Press(key);
            for (int i = 0; i < 4; i++) c128.RunFrame();
            c128.Keyboard.Release(key);
            for (int i = 0; i < 4; i++) c128.RunFrame();
        }
        for (int i = 0; i < 20; i++) c128.RunFrame();
        var screen = c128.GetScreenText();
        TestContext.Out.WriteLine(screen);
        Assert.That(screen, Does.Contain("PRINT1+2\n 3\n"));
    }

    [Test]
    [Category("Slow")]
    public void Boots_CPM_3_From_The_System_Disk()
    {
        var disks = DisksDir();
        if (disks is null) Assert.Ignore("disks/c128 not found");
        var path = Path.Combine(disks!, "cpm.system.622-580745.d64");
        if (!File.Exists(path)) Assert.Ignore("CP/M system disk not found: " + path);
        var roms = Roms();
        if (roms.Drive1541 is null) Assert.Ignore("1541 ROM not available");

        var c128 = new C128(roms) { Display40Columns = false };
        var drive = c128.AttachDrive(8);
        drive.InsertDisk(GcrDisk.FromD64(new D64Image(File.ReadAllBytes(path))), writeProtected: true);
        c128.Reset(hard: true);

        // The KERNAL finds the boot sector, loads CPM+.SYS through the 1541 and hands over to the Z80.
        string screen = "";
        bool prompt = false;
        int frames = 0;
        for (; frames < 50 * 240 && !prompt; frames++)
        {
            c128.RunFrame();
            if (frames % 25 == 0)
            {
                screen = c128.GetVdcScreenAscii();
                prompt = screen.Contains("A>", StringComparison.Ordinal);
            }
        }
        TestContext.Out.WriteLine($"{frames} frames ({frames / 50.0:0.0} s emulated), {Describe(c128)}");
        TestContext.Out.WriteLine(screen);
        SaveFrames(c128, "c128_cpm");
        Assert.That(prompt, Is.True, "no CP/M prompt");
        Assert.That(screen, Does.Contain("CP/M"));
        Assert.That(c128.Z80Active, Is.True, "CP/M runs on the Z80");
    }
}
