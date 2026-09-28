using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Tedd.Commodore.Emulator.C64;
using Tedd.Commodore.Hosting;

namespace Tedd.Commodore.Tests.Hosting;

[TestFixture]
public class InputActionTests
{
    [TestCase("key:A", InputActionKind.Key)]
    [TestCase("key:D1+shift", InputActionKind.Key)]
    [TestCase("joy2:fire", InputActionKind.Joystick)]
    [TestCase("joy1:up", InputActionKind.Joystick)]
    [TestCase("sys:restore", InputActionKind.System)]
    [TestCase("SYS:Reset", InputActionKind.System)]
    public void Parse_RoundTrips(string text, InputActionKind kind)
    {
        Assert.That(InputAction.TryParse(text, out var a), Is.True);
        Assert.That(a.Kind, Is.EqualTo(kind));
        Assert.That(InputAction.Parse(a.ToString()), Is.EqualTo(a));
    }

    [TestCase("")]
    [TestCase("key")]
    [TestCase("key:NoSuchKey")]
    [TestCase("joy3:fire")]
    [TestCase("sys:explode")]
    public void Parse_Rejects_Garbage(string text)
    {
        Assert.That(InputAction.TryParse(text, out _), Is.False);
    }

    [Test]
    public void Shift_Is_Part_Of_The_Action()
    {
        var plain = InputAction.ForKey(C64Key.D1);
        var shifted = InputAction.ForKey(C64Key.D1, shift: true);
        Assert.That(plain, Is.Not.EqualTo(shifted));
        Assert.That(shifted.ToString(), Is.EqualTo("key:D1+shift"));
        Assert.That(shifted.Describe(), Does.StartWith("SHIFT + "));
    }
}

[TestFixture]
public class KeyBindingsTests
{
    [Test]
    public void Default_Has_Letters_Digits_And_Joystick()
    {
        var b = KeyBindings.CreateDefault();
        Assert.That(b.Get("KeyA"), Is.EqualTo(InputAction.ForKey(C64Key.A)));
        Assert.That(b.Get("Digit5"), Is.EqualTo(InputAction.ForKey(C64Key.D5)));
        Assert.That(b.Get("Numpad8"), Is.EqualTo(InputAction.ForJoystick(2, JoystickInput.Up)));
        Assert.That(b.Get("Escape"), Is.EqualTo(InputAction.ForKey(C64Key.RunStop)));
        Assert.That(b.Get("PageUp"), Is.EqualTo(InputAction.ForSystem(SystemCommand.Restore)));
        Assert.That(b.Get("NoSuchKey"), Is.Null);
    }

    [Test]
    public void Json_RoundTrip()
    {
        var b = KeyBindings.CreateDefault();
        b.Set("KeyQ", InputAction.ForJoystick(1, JoystickInput.Fire));
        var json = b.ToJson();
        var c = KeyBindings.FromJson(json);
        Assert.That(c.Count, Is.EqualTo(b.Count));
        foreach (var (code, action) in b.All)
            Assert.That(c.Get(code), Is.EqualTo(action), code);
    }

    [Test]
    public void Json_Is_Indented_Sorted_And_Handles_Escapes()
    {
        var b = new KeyBindings();
        b.Set("KeyA", InputAction.ForKey(C64Key.A));
        b.Set("ArrowUp", InputAction.ForKey(C64Key.CursorDown, shift: true));
        Assert.That(b.ToJson(), Is.EqualTo(
            "{" + Environment.NewLine +
            "  \"ArrowUp\": \"key:CursorDown+shift\"," + Environment.NewLine +
            "  \"KeyA\": \"key:A\"" + Environment.NewLine +
            "}"));
        Assert.That(new KeyBindings().ToJson(), Is.EqualTo("{}"));

        // Files written by System.Text.Json (which escapes '+' as +), a BOM, odd whitespace, escaped keys, null values.
        var c = KeyBindings.FromJson((char)0xFEFF + " {\r\n\t\"ArrowUp\" : \"key:CursorDown\\u002Bshift\" ,\n \"Key\\\"Odd\\\\\": \"joy2:fire\", \"Ignored\": null, \"KeyA\":\"key:A\"}\n");
        Assert.That(c.Count, Is.EqualTo(3));
        Assert.That(c.Get("ArrowUp"), Is.EqualTo(InputAction.ForKey(C64Key.CursorDown, shift: true)));
        Assert.That(c.Get("Key\"Odd\\"), Is.EqualTo(InputAction.ForJoystick(2, JoystickInput.Fire)));
        Assert.That(c.Get("KeyA"), Is.EqualTo(InputAction.ForKey(C64Key.A)));
        var d = KeyBindings.FromJson(c.ToJson());
        Assert.That(d.Get("Key\"Odd\\"), Is.EqualTo(InputAction.ForJoystick(2, JoystickInput.Fire)), "escapes survive a round trip");
        Assert.That(KeyBindings.FromJson("{}").Count, Is.EqualTo(0));

        Assert.Throws<FormatException>(() => KeyBindings.FromJson("[1, 2]"));
        Assert.Throws<FormatException>(() => KeyBindings.FromJson("{\"KeyA\": 1}"));
        Assert.Throws<FormatException>(() => KeyBindings.FromJson("{\"KeyA\": \"key:A\"} x"));
        Assert.Throws<FormatException>(() => KeyBindings.FromJson("{\"KeyA\": \"key:A\""));
        Assert.Throws<FormatException>(() => KeyBindings.FromJson("{\"KeyA\": \"bad \\q escape\"}"));
    }

    [Test]
    public void Save_And_Load()
    {
        var path = Path.Combine(Path.GetTempPath(), "commodore-keys-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var b = new KeyBindings();
            b.Set("KeyZ", InputAction.ForKey(C64Key.Z));
            b.Save(path);
            var c = KeyBindings.Load(path);
            Assert.That(c.Get("KeyZ"), Is.EqualTo(InputAction.ForKey(C64Key.Z)));
            Assert.That(KeyBindings.LoadOrDefault(path + ".missing").Count, Is.GreaterThan(50));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void Changed_Event_And_CodesFor()
    {
        var b = new KeyBindings();
        int changes = 0;
        b.Changed += () => changes++;
        b.Set("Numpad0", InputAction.ForJoystick(2, JoystickInput.Fire));
        b.Set("Numpad5", InputAction.ForJoystick(2, JoystickInput.Fire));
        Assert.That(b.CodesFor(InputAction.ForJoystick(2, JoystickInput.Fire)), Is.EquivalentTo(new[] { "Numpad0", "Numpad5" }));
        b.Remove("Numpad0");
        Assert.That(changes, Is.EqualTo(3));
    }

    [Test]
    public void KeyCodes_Display_Names()
    {
        Assert.That(KeyCodes.Display("KeyA"), Is.EqualTo("A"));
        Assert.That(KeyCodes.Display("Digit1"), Is.EqualTo("1"));
        Assert.That(KeyCodes.Display("Numpad8"), Is.EqualTo("Numpad 8"));
        Assert.That(KeyCodes.Display("ShiftLeft"), Is.EqualTo("Left Shift"));
        Assert.That(KeyCodes.All, Does.Contain("Enter"));
    }
}

[TestFixture]
public class EmulatorSessionTests
{
    private sealed class CapturingVideo : IVideoSink
    {
        public int Frames;
        public uint[]? Last;
        public void PresentFrame(in VideoFrame frame)
        {
            Frames++;
            Last = new uint[frame.Width * frame.Height];
            frame.CopyVisible(Last);
        }
    }

    private sealed class CapturingAudio : IAudioSink
    {
        public int Samples;
        public int Clears;
        public int SampleRate => 44100;
        public void Write(ReadOnlySpan<short> samples) => Samples += samples.Length;
        public void Clear() => Clears++;
    }

    private static RomSet Roms()
    {
        var roms = RomSet.TryLoadDefault();
        if (roms is null) Assert.Ignore("ROM images not available");
        return roms!;
    }

    [Test]
    public void RunFrame_Presents_Video_And_Audio()
    {
        var s = new EmulatorSession(Roms(), attachDrive: false);
        var video = new CapturingVideo();
        var audio = new CapturingAudio();
        s.Video = video;
        s.Audio = audio;
        for (int i = 0; i < 50; i++) s.RunFrame();
        Assert.That(video.Frames, Is.EqualTo(50));
        Assert.That(video.Last, Has.Length.EqualTo(384 * 272));
        Assert.That(audio.Samples, Is.EqualTo(43990).Within(5), "50 PAL frames = 982800 cycles = 43990 samples at 44.1 kHz");
        s.Paused = true;
        s.RunFrame();
        Assert.That(video.Frames, Is.EqualTo(50), "paused sessions do not run");
        s.Paused = false;
        s.Warp = true;
        s.RunFrame();
        Assert.That(audio.Clears, Is.GreaterThan(0), "warp drops audio");
    }

    [Test]
    public void Audio_Sink_Sample_Rate_Must_Match()
    {
        var s = new EmulatorSession(Roms(), sampleRate: 48000, attachDrive: false);
        Assert.Throws<ArgumentException>(() => s.Audio = new CapturingAudio());
    }

    [Test]
    public void KeyDown_Types_Through_Bindings()
    {
        var s = new EmulatorSession(Roms(), attachDrive: false);
        Assert.That(s.Machine.WaitForBasicReady(400), Is.True);
        foreach (var code in new[] { "KeyH", "KeyI" })
        {
            s.KeyDown(code);
            for (int i = 0; i < 4; i++) s.RunFrame();
            s.KeyUp(code);
            for (int i = 0; i < 4; i++) s.RunFrame();
        }
        s.KeyDown("Digit1");
        s.KeyDown("ShiftLeft");
        for (int i = 0; i < 4; i++) s.RunFrame();
        s.KeyUp("Digit1");
        s.KeyUp("ShiftLeft");
        for (int i = 0; i < 4; i++) s.RunFrame();
        Assert.That(s.Machine.GetScreenText(), Does.Contain("READY.\nHI!"));
    }

    [Test]
    public void Shifted_Binding_Holds_Shift_Only_While_Needed()
    {
        var s = new EmulatorSession(Roms(), attachDrive: false);
        s.KeyDown("ArrowUp"); // CursorDown + shift
        Assert.That(s.Machine.Keyboard.IsPressed(C64Key.LeftShift), Is.True);
        Assert.That(s.Machine.Keyboard.IsPressed(C64Key.CursorDown), Is.True);
        s.KeyDown("ArrowLeft"); // CursorRight + shift
        s.KeyUp("ArrowUp");
        Assert.That(s.Machine.Keyboard.IsPressed(C64Key.LeftShift), Is.True, "still held by ArrowLeft");
        s.KeyUp("ArrowLeft");
        Assert.That(s.Machine.Keyboard.IsPressed(C64Key.LeftShift), Is.False);
        s.KeyUp("NeverPressed");
    }

    [Test]
    public void Joystick_And_System_Bindings()
    {
        var s = new EmulatorSession(Roms(), attachDrive: false);
        var commands = new List<SystemCommand>();
        s.Command += commands.Add;
        s.KeyDown("Numpad8");
        s.KeyDown("Numpad0");
        Assert.That(s.Machine.Joystick2.Up && s.Machine.Joystick2.Fire, Is.True);
        Assert.That(s.Machine.Joystick2.Levels, Is.EqualTo(0xEE));
        s.KeyUp("Numpad8");
        Assert.That(s.Machine.Joystick2.Up, Is.False);
        s.KeyDown("PageUp");
        Assert.That(s.Machine.Keyboard.RestorePressed, Is.True);
        s.KeyUp("PageUp");
        Assert.That(s.Machine.Keyboard.RestorePressed, Is.False);
        s.KeyDown("F11");
        s.KeyDown("F12");
        Assert.That(commands, Is.EqualTo(new[] { SystemCommand.Reset, SystemCommand.Screenshot }));
        s.ReleaseAllInput();
        Assert.That(s.Machine.Joystick2.Fire, Is.False);
    }

    [Test]
    public void AttachAuto_Detects_Media_Types()
    {
        var roms = Roms();
        var s = new EmulatorSession(roms, attachDrive: false);
        var t64 = Path.Combine(roms.Directory, "Frogger 64 (Europe).T64");
        if (File.Exists(t64))
        {
            s.AttachAuto(File.ReadAllBytes(t64), Path.GetFileName(t64));
            Assert.That(s.MediaDescription, Does.StartWith("Program: FROGGER"));
        }
        var d64 = Path.Combine(roms.Directory, "Frogger '93 (Europe).D64");
        if (File.Exists(d64) && roms.Drive1541 is not null)
        {
            s.AttachAuto(File.ReadAllBytes(d64), Path.GetFileName(d64), autostart: false);
            Assert.That(s.MediaDescription, Does.StartWith("Disk: "));
            Assert.That(s.Machine.Drive, Is.Not.Null);
            s.EjectDisk();
        }
        Assert.Throws<NotSupportedException>(() => s.AttachAuto(new byte[10], "x.xyz"));
    }

    [Test]
    public void Frame_Copy_Helpers()
    {
        var pixels = new uint[504 * 312];
        var vis = Emulator.Video.VicII.VisibleArea;
        pixels[vis.Y * 504 + vis.X] = 0xFF112233;
        var frame = new VideoFrame(pixels, 7);
        var argb = new uint[384 * 272];
        frame.CopyVisible(argb);
        Assert.That(argb[0], Is.EqualTo(0xFF112233));
        var rgba = new byte[384 * 272 * 4];
        frame.CopyVisibleRgba(rgba);
        Assert.That(rgba[..4], Is.EqualTo(new byte[] { 0x11, 0x22, 0x33, 0xFF }));
        var bgra = new byte[384 * 272 * 4];
        frame.CopyVisibleBgra(bgra);
        Assert.That(bgra[..4], Is.EqualTo(new byte[] { 0x33, 0x22, 0x11, 0xFF }));
        Assert.That(frame.FrameNumber, Is.EqualTo(7));
    }

    [Test]
    public void AudioTap_Keeps_Latest_Samples()
    {
        var inner = new CapturingAudio();
        var tap = new AudioTap(inner, 44100, capacity: 8);
        tap.Write(new short[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 });
        var latest = new short[4];
        tap.CopyLatest(latest);
        Assert.That(latest, Is.EqualTo(new short[] { 7, 8, 9, 10 }));
        Assert.That(inner.Samples, Is.EqualTo(10));
        Assert.That(tap.TotalSamples, Is.EqualTo(10));
        var big = new short[12];
        tap.CopyLatest(big);
        Assert.That(big[..4], Is.EqualTo(new short[4]), "zero padded when fewer samples than requested");
        Assert.That(big[4..], Is.EqualTo(new short[] { 3, 4, 5, 6, 7, 8, 9, 10 }));
    }
}

[TestFixture]
public class EmulatorRunnerTests
{
    private static RomSet Roms()
    {
        var roms = RomSet.TryLoadDefault();
        if (roms is null) Assert.Ignore("ROM images not available");
        return roms!;
    }

    /// <summary>
    /// Regression: the value returning overload passed an expression lambda to Invoke, which binds to
    /// Invoke&lt;T&gt; again rather than to Invoke(Action) - so every call recursed until the stack ran out.
    /// </summary>
    [Test]
    public void Invoke_With_A_Result_Runs_The_Function_Once_And_Returns_It()
    {
        using var runner = new EmulatorRunner(new EmulatorSession(Roms(), attachDrive: false));
        int calls = 0;
        int pc = runner.Invoke(() => { calls++; return (int)runner.Session.Machine.Cpu.PC; });
        Assert.Multiple(() =>
        {
            Assert.That(calls, Is.EqualTo(1));
            Assert.That(pc, Is.EqualTo((int)runner.Session.Machine.Cpu.PC));
            Assert.That(runner.Invoke(() => "value"), Is.EqualTo("value"));
        });
    }

    [Test]
    public void Invoke_Without_A_Result_Runs_The_Action_Once()
    {
        using var runner = new EmulatorRunner(new EmulatorSession(Roms(), attachDrive: false));
        int calls = 0;
        runner.Invoke(() => { calls++; });
        Assert.That(calls, Is.EqualTo(1));
    }
}
