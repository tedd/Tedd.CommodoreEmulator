using System;
using System.Collections.Generic;
using NUnit.Framework;
using Tedd.Commodore.Emulator.C64;
using Tedd.Commodore.Hosting;
using Tedd.Commodore.Unity;

namespace Tedd.Commodore.Tests.Unity;

[TestFixture]
public class C64BridgeTests
{
    private const double Frame = 1.0 / C64.FrameRate;

    private static RomSet Roms()
    {
        var roms = RomSet.TryLoadDefault();
        if (roms is null) Assert.Ignore("ROM images not available");
        return roms!;
    }

    private static C64Bridge Bridge(int sampleRate = 44100)
    {
        var roms = Roms();
        return C64Bridge.Create(roms.Basic, roms.Kernal, roms.Char, roms.Drive1541, sampleRate);
    }

    [Test]
    public void Create_Validates_Rom_Sizes()
    {
        Assert.Throws<ArgumentException>(() => C64Bridge.Create(new byte[100], new byte[8192], new byte[4096], null, 44100));
    }

    [Test]
    public void Update_Runs_Frames_For_Elapsed_Time_With_A_Cap()
    {
        var b = Bridge();
        Assert.That(b.FrameWidth, Is.EqualTo(384));
        Assert.That(b.FrameHeight, Is.EqualTo(272));
        Assert.That(b.MaxFramesPerUpdate, Is.EqualTo(3));

        Assert.That(b.Update(0), Is.False, "no time elapsed");
        Assert.That(b.Update(Frame / 2), Is.False, "half a frame is not enough");
        Assert.That(b.Update(Frame / 2), Is.True, "the two halves add up");
        Assert.That(b.FramesRun, Is.EqualTo(1));

        Assert.That(b.Update(2 * Frame), Is.True);
        Assert.That(b.FramesRun, Is.EqualTo(3), "two frames when two frames of time elapsed");

        Assert.That(b.Update(1.0), Is.True);
        Assert.That(b.FramesRun, Is.EqualTo(6), "a huge delta runs at most MaxFramesPerUpdate frames");
        Assert.That(b.Update(0), Is.False, "and the backlog is dropped, not caught up");

        b.Paused = true;
        Assert.That(b.Update(1.0), Is.False, "paused");
        Assert.That(b.FramesRun, Is.EqualTo(6));
        Assert.That(b.RunFrame(), Is.False);

        b.Paused = false;
        b.Warp = true;
        Assert.That(b.Update(0), Is.True, "warp runs the cap every call regardless of time");
        Assert.That(b.FramesRun, Is.EqualTo(9));
        b.Warp = false;

        Assert.That(b.RunFrame(), Is.True);
        Assert.That(b.FramesRun, Is.EqualTo(10));
        Assert.That(b.FrameNumber, Is.EqualTo(b.Session.Machine.Frames));
    }

    [Test]
    public void CopyFrameRgba_Fills_Width_Height_4_With_Opaque_Pixels()
    {
        var b = Bridge();
        b.RunFrame();
        var rgba = new byte[b.FrameWidth * b.FrameHeight * 4];
        b.CopyFrameRgba(rgba);
        for (int i = 3; i < rgba.Length; i += 4)
            if (rgba[i] != 0xFF) Assert.Fail($"alpha at pixel {i / 4} is {rgba[i]}");
        Assert.Throws<ArgumentException>(() => b.CopyFrameRgba(new byte[10]));
        Assert.Throws<ArgumentNullException>(() => b.CopyFrameRgba(null!));

        var argb = new uint[b.FrameWidth * b.FrameHeight];
        b.CopyFrameArgb(argb);
        Assert.Throws<ArgumentException>(() => b.CopyFrameArgb(new uint[10]));

        // RGBA is a byte-wise expansion of ARGB.
        for (int i = 0; i < 100; i++)
        {
            Assert.That(rgba[i * 4], Is.EqualTo((byte)(argb[i] >> 16)));
            Assert.That(rgba[i * 4 + 1], Is.EqualTo((byte)(argb[i] >> 8)));
            Assert.That(rgba[i * 4 + 2], Is.EqualTo((byte)argb[i]));
        }

        // Flipped copies put the bottom row first.
        var flipped = new uint[argb.Length];
        b.CopyFrameArgb(flipped, flipVertically: true);
        int w = b.FrameWidth, h = b.FrameHeight;
        Assert.That(flipped[..w], Is.EqualTo(argb[((h - 1) * w)..]));
        Assert.That(flipped[((h - 1) * w)..], Is.EqualTo(argb[..w]));
        var flippedRgba = new byte[rgba.Length];
        b.CopyFrameRgba(flippedRgba, flipVertically: true);
        Assert.That(flippedRgba[..(w * 4)], Is.EqualTo(rgba[((h - 1) * w * 4)..]));
    }

    [Test]
    public void ReadAudio_Duplicates_Mono_To_Every_Channel_And_Pads_With_Silence()
    {
        var b = Bridge(48000);
        Assert.That(b.AudioSampleRate, Is.EqualTo(48000));

        var stereo = new float[64 * 2];
        Array.Fill(stereo, 0.5f);
        Assert.That(b.ReadAudio(stereo, 2), Is.EqualTo(0), "nothing produced yet");
        Assert.That(stereo, Is.All.EqualTo(0f), "underrun is silence");
        Assert.That(b.AudioUnderruns, Is.EqualTo(64));

        for (int i = 0; i < 10; i++) b.RunFrame();
        int buffered = b.AudioBuffered;
        Assert.That(buffered, Is.EqualTo(9576).Within(10), "10 PAL frames at 48 kHz");

        Array.Fill(stereo, 0.5f);
        Assert.That(b.ReadAudio(stereo, 2), Is.EqualTo(64));
        for (int i = 0; i < 64; i++)
        {
            Assert.That(stereo[i * 2 + 1], Is.EqualTo(stereo[i * 2]), "both channels carry the same sample");
            Assert.That(stereo[i * 2], Is.InRange(-1f, 1f));
        }
        Assert.That(b.AudioBuffered, Is.EqualTo(buffered - 64));

        var quad = new float[100 * 4];
        Assert.That(b.ReadAudio(quad, 4), Is.EqualTo(100));
        for (int i = 0; i < 100; i++)
            Assert.That(quad[i * 4 + 3], Is.EqualTo(quad[i * 4]));

        var mono = new float[b.AudioBuffered + 50];
        Array.Fill(mono, 0.5f);
        Assert.That(b.ReadAudio(mono, 1), Is.EqualTo(mono.Length - 50), "partial read is padded");
        Assert.That(mono[^50..], Is.All.EqualTo(0f));
        Assert.Throws<ArgumentOutOfRangeException>(() => b.ReadAudio(mono, 0));

        b.RunFrame();
        Assert.That(b.AudioBuffered, Is.GreaterThan(0));
        b.Paused = true;
        Assert.That(b.ReadAudio(mono, 1), Is.EqualTo(0), "pausing flushes the queue");
    }

    [Test]
    public void Key_Input_Through_Bindings_Types_On_The_Basic_Screen()
    {
        var b = Bridge();
        Assert.That(b.Session.Machine.WaitForBasicReady(400), Is.True);
        foreach (var code in new[] { "KeyH", "KeyI" })
        {
            b.KeyDown(code);
            for (int i = 0; i < 4; i++) b.RunFrame();
            b.KeyUp(code);
            for (int i = 0; i < 4; i++) b.RunFrame();
        }
        b.KeyDown(UnityKeyCodes.ToWebCode("LeftShift")!);
        b.KeyDown(UnityKeyCodes.ToWebCode("Alpha1")!);
        for (int i = 0; i < 4; i++) b.RunFrame();
        b.ReleaseAll();
        for (int i = 0; i < 4; i++) b.RunFrame();
        Assert.That(b.ScreenText, Does.Contain("READY.\nHI!"));

        // The picture now has at least border and background colours.
        var argb = new uint[b.FrameWidth * b.FrameHeight];
        b.CopyFrameArgb(argb);
        Assert.That(new HashSet<uint>(argb).Count, Is.GreaterThanOrEqualTo(2));

        b.PressKey(C64Key.Q, true);
        Assert.That(b.Session.Machine.Keyboard.IsPressed(C64Key.Q), Is.True);
        b.PressKey(C64Key.Q, false);
        Assert.That(b.Session.Machine.Keyboard.IsPressed(C64Key.Q), Is.False);
        b.SetJoystick(2, JoystickInput.Fire, true);
        Assert.That(b.Session.Machine.Joystick2.Fire, Is.True);
        b.ReleaseAll();
        Assert.That(b.Session.Machine.Joystick2.Fire, Is.False);
    }

    [Test]
    public void TypeText_And_Reset()
    {
        var b = Bridge();
        Assert.That(b.Session.Machine.WaitForBasicReady(400), Is.True);
        b.TypeText("PRINT 6*7\n");
        for (int i = 0; i < 10; i++) b.RunFrame();
        Assert.That(b.ScreenText, Does.Contain(" 42"));
        b.Reset(hard: true);
        Assert.That(b.Session.Machine.WaitForBasicReady(400), Is.True);
        Assert.That(b.ScreenText, Does.Not.Contain(" 42"));
    }

    [Test]
    public void Command_Event_Forwards_Bound_System_Commands()
    {
        var b = Bridge();
        var commands = new List<SystemCommand>();
        b.Command += commands.Add;
        b.KeyDown("F11");
        b.KeyUp("F11");
        Assert.That(commands, Is.EqualTo(new[] { SystemCommand.Reset }));
        Assert.That(b.Bindings.Get("F11"), Is.EqualTo(InputAction.ForSystem(SystemCommand.Reset)));
        Assert.Throws<ArgumentNullException>(() => b.Bindings = null!);
    }

    [Test]
    public void Media_Attach_Rejects_Unknown_Types()
    {
        var b = Bridge();
        Assert.Throws<NotSupportedException>(() => b.AttachMedia(new byte[10], "x.xyz", autostart: false));
        Assert.That(b.MediaDescription, Is.EqualTo(""));
        b.EjectDisk();
        b.DetachCartridge();
    }
}

[TestFixture]
public class UnityKeyCodesTests
{
    [TestCase("A", "KeyA")]
    [TestCase("z", "KeyZ")]
    [TestCase("Alpha1", "Digit1")]
    [TestCase("Alpha0", "Digit0")]
    [TestCase("Return", "Enter")]
    [TestCase("LeftShift", "ShiftLeft")]
    [TestCase("RightControl", "ControlRight")]
    [TestCase("Keypad8", "Numpad8")]
    [TestCase("KeypadEnter", "NumpadEnter")]
    [TestCase("UpArrow", "ArrowUp")]
    [TestCase("F1", "F1")]
    [TestCase("F12", "F12")]
    [TestCase("BackQuote", "Backquote")]
    [TestCase("Minus", "Minus")]
    [TestCase("Equals", "Equal")]
    [TestCase("LeftBracket", "BracketLeft")]
    [TestCase("RightBracket", "BracketRight")]
    [TestCase("Backslash", "Backslash")]
    [TestCase("Semicolon", "Semicolon")]
    [TestCase("Quote", "Quote")]
    [TestCase("Comma", "Comma")]
    [TestCase("Period", "Period")]
    [TestCase("Slash", "Slash")]
    [TestCase("Escape", "Escape")]
    [TestCase("Space", "Space")]
    [TestCase("Backspace", "Backspace")]
    [TestCase("PageUp", "PageUp")]
    [TestCase("Tab", "Tab")]
    public void Maps_Unity_KeyCode_Names_To_Web_Codes(string unity, string web)
    {
        Assert.That(UnityKeyCodes.ToWebCode(unity), Is.EqualTo(web));
        Assert.That(UnityKeyCodes.FromWebCode(web), Is.Not.Null);
        Assert.That(UnityKeyCodes.ToWebCode(UnityKeyCodes.FromWebCode(web)), Is.EqualTo(web));
    }

    [TestCase("Mouse0")]
    [TestCase("JoystickButton3")]
    [TestCase("None")]
    [TestCase("")]
    [TestCase(null)]
    public void Unmapped_Names_Return_Null(string? unity)
    {
        Assert.That(UnityKeyCodes.ToWebCode(unity), Is.Null);
    }

    [Test]
    public void Mapped_Codes_Are_Known_To_The_Default_Bindings()
    {
        var bindings = KeyBindings.CreateDefault();
        foreach (var unity in new[] { "A", "Alpha1", "Return", "LeftShift", "Keypad8", "UpArrow", "F1", "Escape", "Space", "Comma" })
            Assert.That(bindings.Get(UnityKeyCodes.ToWebCode(unity)!), Is.Not.Null, unity);
        Assert.That(UnityKeyCodes.All.Count, Is.GreaterThan(100));
        Assert.That(UnityKeyCodes.UnityKeyCodeNames, Does.Contain("Keypad0"));
    }
}
