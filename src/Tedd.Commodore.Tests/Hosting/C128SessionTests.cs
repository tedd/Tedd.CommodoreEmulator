using System.IO;
using NUnit.Framework;
using Tedd.Commodore.Emulator.C128;
using Tedd.Commodore.Emulator.C64;
using Tedd.Commodore.Emulator.Drive;
using Tedd.Commodore.Emulator.Machines;
using Tedd.Commodore.Emulator.Video;
using Tedd.Commodore.Hosting;
using Tedd.Commodore.Tests.Machine;
using Tedd.Commodore.Tests.Support;

namespace Tedd.Commodore.Tests.Hosting;

/// <summary>The hosting layer with a Commodore 128 (skipped without the C128 ROMs).</summary>
[TestFixture]
public class C128SessionTests
{
    private sealed class CaptureSink : IVideoSink
    {
        public int Width, Height;
        public VideoSource Source;
        public int Frames;
        public void PresentFrame(in VideoFrame frame)
        {
            Width = frame.Width;
            Height = frame.Height;
            Source = frame.Source;
            Frames++;
        }
    }

    private static C128RomSet Roms() => C128SystemTests.Roms();

    [Test]
    public void Session_Boots_A_C128_And_Reports_Its_Model()
    {
        var s = new EmulatorSession(Roms(), attachDrive: false);
        Assert.That(s.Model, Is.EqualTo(MachineModel.C128));
        Assert.That(s.C128, Is.Not.Null);
        Assert.That(s.C64, Is.Null);
        Assert.That(s.Roms.Basic.Length, Is.EqualTo(RomSet.BasicSize), "the C64 mode ROMs are exposed as the C64 set");
        Assert.That(s.Machine.WaitForBasicReady(400), Is.True);
        Assert.That(s.Machine.GetScreenText(), Does.Contain("BASIC V7.0"));
    }

    [Test]
    public void Basic_7_Talks_To_A_Device_In_The_Expansion_Port()
    {
        var s = new EmulatorSession(Roms(), attachDrive: false);
        var device = new TestExpansionDevice();
        s.AttachExpansion(device);
        Assert.That(s.Machine.WaitForBasicReady(400), Is.True);
        s.TypeText("POKE 57088,65:PRINT PEEK(57088)+1\n");   // bank 15: I/O at $D000
        for (int i = 0; i < 100; i++) s.RunFrame();
        Assert.That(device.IoRam[0], Is.EqualTo(65));
        Assert.That(s.Machine.GetScreenText(), Does.Contain("\n 66"));
    }

    [Test]
    public void Display_Follows_The_40_80_Key_And_Can_Be_Forced()
    {
        var s = new EmulatorSession(Roms(), attachDrive: false);
        var sink = new CaptureSink();
        s.Video = sink;
        s.RunFrame();
        Assert.That(sink.Source, Is.EqualTo(VideoSource.VicII));
        Assert.That((sink.Width, sink.Height), Is.EqualTo((384, 272)));

        s.Display40Columns = false;                      // press the 40/80 key
        Assert.That(s.ShowingVdc, Is.True);
        s.RunFrame();
        Assert.That(sink.Source, Is.EqualTo(VideoSource.Vdc));
        Assert.That((sink.Width, sink.Height), Is.EqualTo((Vdc8563.FrameWidth, Vdc8563.FrameHeight)));

        s.Display = DisplayOutput.VicII;
        s.RunFrame();
        Assert.That(sink.Source, Is.EqualTo(VideoSource.VicII));
        s.Display = DisplayOutput.Vdc;
        s.Display40Columns = true;
        s.RunFrame();
        Assert.That(sink.Source, Is.EqualTo(VideoSource.Vdc), "forced VDC ignores the key");
        Assert.That(s.CurrentFrame.Width, Is.EqualTo(Vdc8563.FrameWidth));
    }

    [Test]
    public void Columns80_Constructor_Boots_On_The_Vdc()
    {
        var s = new EmulatorSession(Roms(), attachDrive: false, columns80: true);
        Assert.That(s.Display40Columns, Is.False);
        Assert.That(s.Machine.WaitForBasicReady(400), Is.True);
        Assert.That(s.C128!.GetVdcScreenText(), Does.Contain("READY."));
    }

    [Test]
    public void Bound_Keys_Toggle_The_40_80_And_Caps_Lock_Keys()
    {
        var s = new EmulatorSession(Roms(), attachDrive: false);
        Assert.That(s.Bindings.Get("F9"), Is.EqualTo(InputAction.ForSystem(SystemCommand.ToggleColumns)), "C128 default layout");
        Assert.That(s.Bindings.Get("Escape"), Is.EqualTo(InputAction.ForKey(C64Key.Escape)));
        SystemCommand? reported = null;
        s.Command += c => reported = c;
        s.KeyDown("F9");
        Assert.That(s.Display40Columns, Is.False);
        Assert.That(reported, Is.EqualTo(SystemCommand.ToggleColumns));
        s.KeyUp("F9");
        s.KeyDown("F9");
        Assert.That(s.Display40Columns, Is.True);
        s.KeyUp("F9");
        s.KeyDown("CapsLock");
        Assert.That(s.CapsLock, Is.True);
        Assert.That(s.C128!.Memory.CapsLock, Is.True);
        s.KeyUp("CapsLock");
        s.KeyDown("Numpad7");
        Assert.That(s.Machine.Keyboard.IsPressed(C64Key.Keypad7), Is.True);
        s.KeyUp("Numpad7");
    }

    [Test]
    public void C64_Session_Ignores_The_C128_Toggles()
    {
        var roms = RomSet.TryLoadDefault();
        if (roms is null) Assert.Ignore("C64 ROMs not available");
        var s = new EmulatorSession(roms!, attachDrive: false);
        Assert.That(s.Model, Is.EqualTo(MachineModel.C64));
        Assert.That(s.ShowingVdc, Is.False);
        s.Display = DisplayOutput.Vdc;
        Assert.That(s.ShowingVdc, Is.False, "a C64 has no VDC");
        s.Display40Columns = false;
        Assert.That(s.Display40Columns, Is.True);
        s.CapsLock = true;
        Assert.That(s.CapsLock, Is.False);
    }

    [Test]
    public void Boot_Disk_Autostart_Resets_Instead_Of_Typing()
    {
        var disks = C128SystemTests.DisksDir();
        if (disks is null) Assert.Ignore("disks/c128 not found");
        var roms = Roms();
        if (roms.Drive1541 is null) Assert.Ignore("1541 ROM not available");
        var data = File.ReadAllBytes(Path.Combine(disks!, "cpm.system.622-580745.d64"));
        Assert.That(EmulatorSession.IsBootDisk(new D64Image(data)), Is.True);

        var s = new EmulatorSession(roms, attachDrive: true);
        Assert.That(s.Machine.WaitForBasicReady(1500), Is.True, "with a drive attached the KERNAL's boot check waits for it");
        s.AttachDisk(data, "cpm.d64", autostart: true, writeProtected: true);
        Assert.That(s.MediaDescription, Does.Contain("[boot disk]"));
        Assert.That(s.Machine.Autostart, Is.EqualTo(CommodoreMachine.AutostartState.Idle), "no LOAD/RUN typing for a boot disk");
        Assert.That(s.Machine.PendingTypeAhead, Is.EqualTo(0));
        // The reset put the Z80 in charge again; its boot BIOS hands over to the 8502 within a few frames.
        for (int i = 0; i < 20 && s.C128!.Z80Active; i++) s.RunFrame();
        Assert.That(s.C128!.Z80Active, Is.False);
    }
}
