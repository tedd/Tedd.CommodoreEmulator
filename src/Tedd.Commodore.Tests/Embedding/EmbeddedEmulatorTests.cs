using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using NUnit.Framework;
using Tedd.Commodore.Embedding;
using Tedd.Commodore.Emulator.C128;
using Tedd.Commodore.Emulator.C64;
using Tedd.Commodore.Emulator.Machines;
using Tedd.Commodore.Emulator.Tools;
using Tedd.Commodore.Emulator.Video;
using Tedd.Commodore.Hosting;
using Tedd.Commodore.Tests.Support;

namespace Tedd.Commodore.Tests.Embedding;

/// <summary>
/// The embedding facade on a C64 whose KERNAL is a few lines of assembly (so these run without the Commodore ROMs,
/// on CI too): it turns the screen on with a red border and a blue background, all characters blank, and plays a
/// sawtooth on SID voice 1.
/// </summary>
[TestFixture]
public class EmbeddedEmulatorTests
{
    private const int Border = 2, Background = 6;
    private const double Frame = 1.0 / EmbeddedEmulator.FrameRate;

    private static readonly byte[] Kernal = BuildKernal();

    private static byte[] BuildKernal()
    {
        var program = Assembler.Assemble($"""
                    * = $E000
            reset:  sei
                    cld
                    ldx #$FF
                    txs
                    lda #$1B        ; screen on, 25 rows, text mode
                    sta $D011
                    lda #$08        ; 40 columns
                    sta $D016
                    lda #$14        ; screen at $0400, characters from the (empty) character ROM
                    sta $D018
                    lda #{Border}
                    sta $D020
                    lda #{Background}
                    sta $D021
                    lda #$0F        ; SID volume 15
                    sta $D418
                    lda #$00        ; voice 1: attack 0, decay 0
                    sta $D405
                    lda #$F0        ; sustain 15, release 0
                    sta $D406
                    lda #$00        ; frequency $1000 (about 240 Hz)
                    sta $D400
                    lda #$10
                    sta $D401
                    lda #$21        ; sawtooth, gate on
                    sta $D404
            loop:   jmp loop
            nmi:    rti

                    * = $FFFA
                    .word nmi, reset, nmi
            """);
        var kernal = new byte[RomSet.KernalSize];
        program.CopyTo((address, value) => kernal[address - 0xE000] = value);
        return kernal;
    }

    /// <summary>The test machine: no BASIC, an empty character generator (every glyph blank), no drive.</summary>
    private static EmbeddedEmulator Create(EmbeddedEmulatorOptions? options = null) =>
        EmbeddedEmulator.CreateC64(new byte[RomSet.BasicSize], Kernal, new byte[RomSet.CharSize], null, options);

    private static byte[] Pixels(RawFrame frame, FramePixelFormat format)
    {
        var bytes = new byte[frame.GetSizeInBytes(format)];
        frame.CopyTo(bytes, format);
        return bytes;
    }

    [Test]
    public void RunFrame_Delivers_A_Picture_In_Every_Format()
    {
        using var emulator = Create();
        Assert.That(emulator.Model, Is.EqualTo(MachineModel.C64));
        Assert.That(emulator.Video.TryAcquire(out _), Is.False, "nothing before the first frame");
        emulator.RunFrame();
        emulator.RunFrame();

        Assert.That(emulator.Video.TryAcquire(out var frame), Is.True);
        Assert.That((frame.Width, frame.Height, frame.Source), Is.EqualTo((384, 272, VideoSource.VicII)));
        Assert.That(frame.FrameNumber, Is.EqualTo(2));
        Assert.That(emulator.Video.FramesSkipped, Is.EqualTo(1), "frame 1 was replaced before anyone took it");

        // The 320 x 200 display window sits at (32, 35) of the visible area; everything around it is border.
        int w = frame.Width;
        int center = (35 + 100) * w + 32 + 160, corner = 0, belowWindow = (35 + 200) * w + 32;
        var indexes = Pixels(frame, FramePixelFormat.Indexed8);
        Assert.That(indexes[corner], Is.EqualTo(Border));
        Assert.That(indexes[belowWindow], Is.EqualTo(Border));
        Assert.That(indexes[center], Is.EqualTo(Background));
        Assert.That(indexes.Count(i => i == Background), Is.EqualTo(320 * 200), "the whole display window is background");
        Assert.That(indexes.Count(i => i == Border), Is.EqualTo(384 * 272 - 320 * 200));

        uint border = VicII.Palette[Border], background = VicII.Palette[Background];
        var bgra = Pixels(frame, FramePixelFormat.Bgra8888);
        Assert.That(bgra.AsSpan(center * 4, 4).ToArray(), Is.EqualTo(new[] { (byte)background, (byte)(background >> 8), (byte)(background >> 16), (byte)255 }));
        var rgba = Pixels(frame, FramePixelFormat.Rgba8888);
        Assert.That(rgba.AsSpan(corner * 4, 4).ToArray(), Is.EqualTo(new[] { (byte)(border >> 16), (byte)(border >> 8), (byte)border, (byte)255 }));

        // The same picture straight from the emulator's raster (no triple buffer), inside the session.
        var direct = emulator.Invoke(s => Pixels(new RawFrame(s.CurrentFrame), FramePixelFormat.Indexed8));
        Assert.That(direct, Is.EqualTo(indexes));
    }

    [Test]
    public void The_Sid_Is_Heard_At_The_Requested_Rate()
    {
        using var emulator = Create(new EmbeddedEmulatorOptions { SampleRate = 44100, AudioPrimeSeconds = 0 });
        Assert.That(emulator.SampleRate, Is.EqualTo(44100));
        Assert.That(emulator.Audio.SampleRate, Is.EqualTo(44100));
        for (int i = 0; i < 5; i++) emulator.RunFrame();
        Assert.That(emulator.Audio.Available, Is.EqualTo((int)(5 * 44100 / EmbeddedEmulator.FrameRate)).Within(2));

        var stereo = new float[emulator.Audio.Available * 2];
        int frames = emulator.Audio.Fill(stereo, 2);
        Assert.That(frames, Is.EqualTo(stereo.Length / 2));
        Assert.That(stereo.Max(), Is.GreaterThan(0.1f), "a full volume sawtooth");
        Assert.That(stereo.Min(), Is.LessThan(-0.1f));
        for (int i = 0; i < frames; i++)
            Assert.That(stereo[2 * i + 1], Is.EqualTo(stereo[2 * i]));
    }

    [Test]
    public void Update_Runs_The_Frames_That_Are_Due()
    {
        using var emulator = Create();
        Assert.That(emulator.MaxFramesPerUpdate, Is.EqualTo(3));
        Assert.That(emulator.Update(0), Is.EqualTo(0));
        Assert.That(emulator.Update(Frame / 2), Is.EqualTo(0), "half a frame is not enough");
        Assert.That(emulator.Update(Frame / 2), Is.EqualTo(1), "the two halves add up");
        Assert.That(emulator.Update(2 * Frame), Is.EqualTo(2));
        Assert.That(emulator.Update(1.0), Is.EqualTo(3), "a long hitch runs at most MaxFramesPerUpdate");
        Assert.That(emulator.Update(0), Is.EqualTo(0), "and the backlog is dropped");
        Assert.That(emulator.Update(double.NaN), Is.EqualTo(0));
        Assert.That(emulator.Update(-1), Is.EqualTo(0));
        Assert.That(emulator.Video.FramesPresented, Is.EqualTo(6));

        emulator.Paused = true;
        Assert.That(emulator.Update(1.0), Is.EqualTo(0), "paused");
        Assert.That(emulator.RunFrame(), Is.False);
        emulator.Paused = false;

        emulator.Warp = true;
        Assert.That(emulator.Update(0), Is.EqualTo(3), "warp runs the cap on every call");
        emulator.Warp = false;
        emulator.MaxFramesPerUpdate = 0;
        Assert.That(emulator.MaxFramesPerUpdate, Is.EqualTo(1));
        Assert.That(emulator.Video.FramesPresented, Is.EqualTo(9));
        Assert.That(emulator.Invoke(s => s.Machine.Frames), Is.EqualTo(9));
    }

    [Test]
    public void Runs_On_Its_Own_Thread_At_Real_Time_Speed()
    {
        using var emulator = Create();
        var clock = Stopwatch.StartNew();   // before the runner's own clock starts, so time spent below is counted
        emulator.Start();
        emulator.Start();   // already running: nothing happens
        Assert.That(emulator.IsRunning, Is.True);
        Assert.Throws<InvalidOperationException>(() => emulator.Update(1.0));
        Assert.Throws<InvalidOperationException>(() => emulator.RunFrame());

        WaitFor(() => emulator.Video.FramesPresented >= 25, "25 frames");
        Assert.That(clock.Elapsed.TotalSeconds, Is.GreaterThan(0.4), "paced at 50 frames per second, not flat out");
        Assert.That(emulator.Video.TryAcquire(out var frame), Is.True);
        Assert.That(Pixels(frame, FramePixelFormat.Indexed8)[0], Is.EqualTo(Border));

        // Input and Invoke are serialised with the frames.
        emulator.KeyDown("KeyA");
        Assert.That(emulator.Invoke(s => s.Machine.Keyboard.IsPressed(C64Key.A)), Is.True);
        emulator.KeyUp("KeyA");
        Assert.That(emulator.Invoke(s => s.Machine.Keyboard.IsPressed(C64Key.A)), Is.False);

        emulator.Paused = true;
        long paused = emulator.Video.FramesPresented;
        Thread.Sleep(150);
        Assert.That(emulator.Video.FramesPresented, Is.LessThanOrEqualTo(paused + 1), "at most the frame in progress finishes");
        emulator.Paused = false;
        long resumed = emulator.Video.FramesPresented;
        WaitFor(() => emulator.Video.FramesPresented > resumed + 2, "frames after resuming");

        emulator.Stop();
        Assert.That(emulator.IsRunning, Is.False);
        long stopped = emulator.Video.FramesPresented;
        Thread.Sleep(100);
        Assert.That(emulator.Video.FramesPresented, Is.EqualTo(stopped));
        Assert.That(emulator.Update(Frame), Is.EqualTo(1), "back to being driven by the host");

        emulator.Start();
        WaitFor(() => emulator.Video.FramesPresented > stopped + 3, "frames after a restart");
    }

    [Test]
    public void Input_Goes_Through_The_Key_Bindings()
    {
        using var emulator = Create();
        var commands = new List<SystemCommand>();
        emulator.Command += commands.Add;

        emulator.KeyDown(GlfwKeyCodes.ToWebCode('Q'));
        emulator.KeyDown(AvaloniaKeyCodes.ToWebCode("W"));
        Assert.That(emulator.Invoke(s => s.Machine.Keyboard.IsPressed(C64Key.Q) && s.Machine.Keyboard.IsPressed(C64Key.W)), Is.True);
        emulator.KeyDown(null);
        emulator.KeyUp("");
        emulator.KeyDown("NoSuchKey");
        emulator.ReleaseAllInput();
        Assert.That(emulator.Invoke(s => s.Machine.Keyboard.IsPressed(C64Key.Q)), Is.False);

        emulator.SetKey(C64Key.RunStop, true);
        Assert.That(emulator.Invoke(s => s.Machine.Keyboard.IsPressed(C64Key.RunStop)), Is.True);
        emulator.SetJoystick(2, JoystickInput.Fire, true);
        Assert.That(emulator.Invoke(s => s.Machine.Joystick2.Fire), Is.True);
        emulator.Reset();
        Assert.That(emulator.Invoke(s => s.Machine.Joystick2.Fire || s.Machine.Keyboard.IsPressed(C64Key.RunStop)), Is.False, "a reset releases everything");

        emulator.KeyDown("F11");
        emulator.KeyUp("F11");
        Assert.That(commands, Is.EqualTo(new[] { SystemCommand.Reset }), "bound host commands are reported");

        emulator.Bindings = new KeyBindings();
        emulator.KeyDown("KeyA");
        Assert.That(emulator.Invoke(s => s.Machine.Keyboard.IsPressed(C64Key.A)), Is.False, "an empty table binds nothing");
        Assert.Throws<ArgumentNullException>(() => emulator.Bindings = null!);
    }

    [Test]
    public void Media_And_Lifetime()
    {
        var emulator = Create();
        Assert.Throws<NotSupportedException>(() => emulator.AttachMedia(new byte[10], "x.xyz"));
        Assert.That(emulator.MediaDescription, Is.EqualTo(""));
        emulator.EjectDisk();
        emulator.Dispose();
        Assert.Throws<ObjectDisposedException>(() => emulator.Update(1));
        Assert.Throws<ObjectDisposedException>(() => emulator.Start());
        emulator.Dispose();
        Assert.Throws<ArgumentNullException>(() => new EmbeddedEmulator((RomSet)null!));
        Assert.Throws<ArgumentNullException>(() => emulator.Invoke(null!));
    }

    [Test]
    public void A_Device_In_The_Expansion_Port_Talks_To_The_Program()
    {
        // A KERNAL that keeps storing the device's input register + 1 into the device's RAM at $DF00.
        var program = Assembler.Assemble("""
                    * = $E000
            reset:  lda $DE04
                    clc
                    adc #1
                    sta $DF00
                    jmp reset
            nmi:    rti

                    * = $FFFA
                    .word nmi, reset, nmi
            """);
        var kernal = new byte[RomSet.KernalSize];
        program.CopyTo((address, value) => kernal[address - 0xE000] = value);
        using var emulator = EmbeddedEmulator.CreateC64(new byte[RomSet.BasicSize], kernal, new byte[RomSet.CharSize]);
        var device = new TestExpansionDevice();
        emulator.AttachExpansion(device);
        Assert.That(emulator.Expansion, Is.SameAs(device));
        Assert.That(emulator.MediaDescription, Is.EqualTo("Cartridge: test device"));

        emulator.Invoke(_ => device.Input = 41);
        emulator.RunFrame();
        Assert.That(device.IoRam[0], Is.EqualTo(42));

        // On its own thread: the host changes the device between frames through Invoke.
        emulator.Start();
        emulator.Invoke(_ => device.Input = 99);
        WaitFor(() => emulator.Invoke(_ => device.IoRam[0]) == 100, "the program to see the new input");
        emulator.Stop();

        emulator.AttachExpansion(null);
        Assert.That(emulator.Expansion, Is.Null);
        emulator.Invoke(_ => device.Input = 7);
        emulator.RunFrame();
        Assert.That(device.IoRam[0], Is.EqualTo(100), "unplugged: the program no longer reaches it");
    }

    /// <summary>With the real ROMs: BASIC boots, keys type, and the picture is the familiar one (skipped without ROMs).</summary>
    [Test]
    public void Boots_Basic_And_Types_With_The_Real_Roms()
    {
        var roms = RomSet.TryLoadDefault();
        if (roms is null) Assert.Ignore("ROM images not available");
        using var emulator = new EmbeddedEmulator(roms!, new EmbeddedEmulatorOptions { AttachDrive = false });
        Assert.That(emulator.Invoke(s => s.Machine.WaitForBasicReady(400)), Is.True);
        foreach (var key in new[] { "H", "I" })
        {
            emulator.KeyDown(AvaloniaKeyCodes.ToWebCode(key));
            for (int i = 0; i < 4; i++) emulator.RunFrame();
            emulator.KeyUp(AvaloniaKeyCodes.ToWebCode(key));
            for (int i = 0; i < 4; i++) emulator.RunFrame();
        }
        Assert.That(emulator.Invoke(s => s.Machine.GetScreenText()), Does.Contain("READY.\nHI"));

        Assert.That(emulator.Video.TryAcquire(out var frame), Is.True);
        var indexes = Pixels(frame, FramePixelFormat.Indexed8);
        Assert.That(indexes[0], Is.EqualTo(14), "light blue border");
        Assert.That(indexes[(35 + 199) * frame.Width + 32 + 319], Is.EqualTo(6), "blue background");

        var bgra = Pixels(frame, FramePixelFormat.Bgra8888);
        var dir = Path.Combine(AppContext.BaseDirectory, "TestResults");
        Directory.CreateDirectory(dir);
        PngWriter.Save(Path.Combine(dir, "embedding-basic.png"), frame.Width, frame.Height, MemoryMarshal.Cast<byte, uint>(bgra));
    }

    /// <summary>A C128 on its 80 column screen hands out 768 pixel wide VDC frames (skipped without the C128 ROMs).</summary>
    [Test]
    public void C128_On_The_Vdc_Delivers_Wide_Frames()
    {
        var roms = C128RomSet.TryLoadDefault();
        if (roms is null) Assert.Ignore("C128 ROM images not available");
        using var emulator = new EmbeddedEmulator(roms!, new EmbeddedEmulatorOptions { Columns80 = true, AttachDrive = false });
        Assert.That(emulator.Model, Is.EqualTo(MachineModel.C128));
        for (int i = 0; i < 100; i++) emulator.RunFrame();
        Assert.That(emulator.Video.TryAcquire(out var frame), Is.True);
        Assert.That((frame.Width, frame.Height, frame.Source), Is.EqualTo((768, 272, VideoSource.Vdc)));
        Assert.That(frame.Palette.ToArray(), Is.EqualTo(Vdc8563.Palette));

        emulator.Display = DisplayOutput.VicII;
        emulator.RunFrame();
        emulator.Video.TryAcquire(out frame);
        Assert.That((frame.Width, frame.Source), Is.EqualTo((384, VideoSource.VicII)));
    }

    private static void WaitFor(Func<bool> condition, string what)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition())
        {
            if (deadline.Elapsed.TotalSeconds > 10)
                Assert.Fail($"Timed out waiting for {what}");
            Thread.Sleep(5);
        }
    }
}
