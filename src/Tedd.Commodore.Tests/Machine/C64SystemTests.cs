using System;
using System.IO;
using NUnit.Framework;
using Tedd.Commodore.Emulator.C64;
using Tedd.Commodore.Emulator.Drive;
using Tedd.Commodore.Emulator.Media;
using Tedd.Commodore.Emulator.Tools;
using Tedd.Commodore.Emulator.Video;

namespace Tedd.Commodore.Tests.Machine;

/// <summary>
/// Whole-machine tests that boot the real ROMs. They are skipped when the ROM images are not available.
/// Rendered frames are written to TestResults/ next to the test assembly for visual inspection.
/// </summary>
[TestFixture]
public class C64SystemTests
{
    private static RomSet Roms()
    {
        var roms = RomSet.TryLoadDefault();
        if (roms is null) Assert.Ignore("ROM images not available");
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

    public static void SaveFrame(C64 c64, string name)
    {
        // Text written after the raster passed is only visible in the next frame.
        c64.RunFrame();
        var vis = VicII.VisibleArea;
        var crop = new uint[vis.Width * vis.Height];
        for (int y = 0; y < vis.Height; y++)
            Array.Copy(c64.Vic.Frame, (vis.Y + y) * VicII.FrameWidth + vis.X, crop, y * vis.Width, vis.Width);
        var path = Path.Combine(ResultsDir, name + ".png");
        PngWriter.Save(path, vis.Width, vis.Height, crop);
        TestContext.Out.WriteLine("Saved " + path);
    }

    private static C64 Boot(bool drive = false)
    {
        var roms = Roms();
        var c64 = new C64(roms);
        if (drive)
        {
            if (roms.Drive1541 is null) Assert.Ignore("1541 ROM not available");
            c64.AttachDrive(8);
        }
        Assert.That(c64.WaitForBasicReady(400), Is.True, "BASIC did not come up:\n" + c64.GetScreenText());
        return c64;
    }

    [Test]
    public void Frame_Is_19656_Cycles()
    {
        var c64 = new C64(Roms());
        Assert.That(c64.RunFrame(), Is.EqualTo(C64.CyclesPerFrame));
        Assert.That(c64.RunFrame(), Is.EqualTo(19656));
        Assert.That(c64.Cycles, Is.EqualTo(2 * 19656));
    }

    [Test]
    public void Boots_To_Basic_Prompt()
    {
        var c64 = Boot();
        var screen = c64.GetScreenText();
        TestContext.Out.WriteLine(screen);
        SaveFrame(c64, "c64_boot");
        Assert.That(screen, Does.Contain("**** COMMODORE 64 BASIC V2 ****"));
        Assert.That(screen, Does.Contain("64K RAM SYSTEM  38911 BASIC BYTES FREE"));
        Assert.That(screen, Does.Contain("READY."));
        // Light blue border, blue background, light blue text: check a border pixel and a background pixel.
        Assert.That(c64.Vic.Frame[10 * VicII.FrameWidth + 10], Is.EqualTo(VicII.Palette[14]), "border is light blue");
        var vis = VicII.VisibleArea;
        int bgX = vis.X + 32 + 160, bgY = vis.Y + 35 + 100;
        Assert.That(c64.Vic.Frame[bgY * VicII.FrameWidth + bgX], Is.EqualTo(VicII.Palette[6]), "background is blue");
        Assert.That(c64.Frames, Is.LessThan(200), "boot should take well under 4 seconds");
    }

    [Test]
    public void Basic_Print_Works()
    {
        var c64 = Boot();
        c64.TypeText("PRINT 2+2\n");
        for (int i = 0; i < 30; i++) c64.RunFrame();
        var screen = c64.GetScreenText();
        Assert.That(screen, Does.Contain("PRINT 2+2\n 4\n"));
    }

    [Test]
    public void Basic_Program_Runs()
    {
        var c64 = Boot();
        c64.TypeText("10 FOR I=1 TO 5:PRINT \"HELLO\";I:NEXT\n20 PRINT \"DONE\"\nRUN\n");
        for (int i = 0; i < 100; i++) c64.RunFrame();
        var screen = c64.GetScreenText();
        TestContext.Out.WriteLine(screen);
        SaveFrame(c64, "c64_basic_program");
        Assert.That(screen, Does.Contain("HELLO 1\nHELLO 2\nHELLO 3\nHELLO 4\nHELLO 5\nDONE\n\nREADY."));
    }

    [Test]
    public void Keyboard_Matrix_Typing()
    {
        var c64 = Boot();
        foreach (var key in new[] { C64Key.H, C64Key.I })
        {
            c64.Keyboard.Press(key);
            for (int i = 0; i < 4; i++) c64.RunFrame();
            c64.Keyboard.Release(key);
            for (int i = 0; i < 4; i++) c64.RunFrame();
        }
        c64.Keyboard.Press(C64Key.LeftShift);
        c64.Keyboard.Press(C64Key.D1);
        for (int i = 0; i < 4; i++) c64.RunFrame();
        c64.Keyboard.ReleaseAll();
        for (int i = 0; i < 4; i++) c64.RunFrame();
        var screen = c64.GetScreenText();
        Assert.That(screen, Does.Contain("READY.\nHI!"));
    }

    [Test]
    public void Diagnostic_Cartridge_Starts()
    {
        var roms = Roms();
        var path = Path.Combine(roms.Directory, "c64_diag_rev4.1.1.bin");
        if (!File.Exists(path)) Assert.Ignore("diagnostic cartridge not available");
        var c64 = new C64(roms);
        c64.AttachCartridge(Cartridge.Load(path));
        c64.Reset(hard: false);
        for (int i = 0; i < 250; i++) c64.RunFrame();
        var screen = c64.GetScreenText();
        TestContext.Out.WriteLine(screen);
        SaveFrame(c64, "c64_diag_cartridge");
        Assert.That(screen, Does.Not.Contain("COMMODORE 64 BASIC"), "the cartridge must take over from BASIC");
        Assert.That(screen.Replace("\n", "").Trim(), Is.Not.Empty, "the cartridge should draw something");
    }

    [Test]
    public void T64_Program_Injection_Runs()
    {
        var roms = Roms();
        var path = Path.Combine(roms.Directory, "Frogger 64 (Europe).T64");
        if (!File.Exists(path)) Assert.Ignore("T64 image not available");
        var t64 = T64Image.Load(path);
        var program = t64.GetProgram(0);
        Assert.That(program.LoadAddress, Is.EqualTo(0x0801));
        var c64 = Boot();
        c64.InjectProgram(program, run: true);
        for (int i = 0; i < 400; i++) c64.RunFrame();
        var screen = c64.GetScreenText();
        TestContext.Out.WriteLine(screen);
        SaveFrame(c64, "c64_t64_frogger");
        Assert.That(screen, Does.Not.Contain("READY."), "the program should be running, not back at the prompt");
    }

    [Test]
    [Category("Slow")]
    public void Directory_Listing_From_1541()
    {
        var roms = Roms();
        var diskPath = Path.Combine(roms.Directory, "Frogger '93 (Europe).D64");
        if (!File.Exists(diskPath)) Assert.Ignore("D64 image not available");
        var c64 = Boot(drive: true);
        var d64 = D64Image.Load(diskPath);
        c64.Drive!.InsertDisk(GcrDisk.FromD64(d64));
        c64.TypeText("LOAD\"$\",8\n");
        bool loaded = false;
        for (int i = 0; i < 1500 && !loaded; i++)
        {
            c64.RunFrame();
            var s = c64.GetScreenText();
            if (s.Contains("?", StringComparison.Ordinal) && s.Contains("ERROR", StringComparison.Ordinal))
                Assert.Fail("Error while loading:\n" + s);
            loaded = s.Contains("LOADING", StringComparison.Ordinal) && s.IndexOf("READY.", s.IndexOf("LOADING", StringComparison.Ordinal), StringComparison.Ordinal) >= 0 && c64.Memory.Ram[0xC6] == 0;
        }
        TestContext.Out.WriteLine(c64.GetScreenText());
        Assert.That(loaded, Is.True, "directory did not load:\n" + c64.GetScreenText());
        c64.TypeText("LIST\n");
        for (int i = 0; i < 100; i++) c64.RunFrame();
        var screen = c64.GetScreenText();
        TestContext.Out.WriteLine(screen);
        SaveFrame(c64, "c64_directory");
        Assert.That(screen, Does.Contain(d64.DiskName.Trim()).IgnoreCase);
        Assert.That(screen, Does.Contain("BLOCKS FREE"));
    }

    [Test]
    [Category("Slow")]
    public void Autostart_Game_From_1541()
    {
        var roms = Roms();
        var diskPath = Path.Combine(roms.Directory, "Frogger '93 (Europe).D64");
        if (!File.Exists(diskPath)) Assert.Ignore("D64 image not available");
        var c64 = Boot(drive: true);
        c64.Drive!.InsertDisk(GcrDisk.FromD64(D64Image.Load(diskPath)));
        c64.AutostartFromDisk("*");
        int frames = 0;
        while (c64.Autostart is C64.AutostartState.WaitingForBasic or C64.AutostartState.Loading or C64.AutostartState.WaitingForRun && frames < 4000)
        {
            c64.RunFrame();
            frames++;
        }
        TestContext.Out.WriteLine($"Autostart state {c64.Autostart} after {frames} frames\n{c64.GetScreenText()}");
        Assert.That(c64.Autostart, Is.EqualTo(C64.AutostartState.Done), c64.GetScreenText());
        for (int i = 0; i < 300; i++) c64.RunFrame();
        SaveFrame(c64, "c64_autostart_frogger93");
        Assert.That(c64.GetScreenText(), Does.Not.Contain("READY."));
    }
}
