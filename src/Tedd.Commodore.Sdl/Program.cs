using System;
using System.IO;
using Tedd.Commodore.Emulator.C128;
using Tedd.Commodore.Emulator.C64;
using Tedd.Commodore.Emulator.Machines;
using Tedd.Commodore.Hosting;
using static SDL2.SDL;

namespace Tedd.Commodore.Sdl;

internal static class Program
{
    private const string RomHelp = """
        The C64 ROM images (BASIC, KERNAL, character generator and optionally the 1541 DOS) are not distributed with
        the emulator. Put them in a directory and either pass --roms <dir>, set the C64_ROMS environment variable, or
        place them next to the executable or in a "roms" sub-directory. Recognised file names (VICE naming):
          basic.901226-01.bin / basic.bin, kernal.901227-03.bin / kernal.bin, characters.901225-01.bin / chargen.bin,
          1541-II.251968-03.bin / dos1541 (or 1541-c000.325302-01.bin + 1541-e000.901229-05.bin).
        A C128 (--machine c128) additionally needs basic-4000.318018-04.bin + basic-8000.318019-04.bin (or the
        combined basic.318022-02.bin), kernal.318020-05.bin (or complete.318023-02.bin), characters.390059-01.bin,
        and the C64 BASIC/KERNAL above; the C128_ROMS variable is checked before C64_ROMS.
        """;

    /// <summary>Per-user key bindings file, shared with the other front-ends.</summary>
    private static string KeyBindingsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Tedd.Commodore", "keybindings.json");

    private static int Main(string[] args)
    {
        Options options;
        try
        {
            options = Options.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine();
            Console.Error.WriteLine(Options.Usage);
            return 1;
        }
        if (options.ShowHelp)
        {
            Console.WriteLine(Options.Usage);
            return 0;
        }

        bool c128 = options.Machine == "c128";
        RomSet? roms = null;
        C128RomSet? roms128 = null;
        try
        {
            if (c128)
            {
                roms128 = options.RomDirectory is not null
                    ? C128RomSet.Load(options.RomDirectory)
                    : C128RomSet.TryLoadDefault() ?? throw new FileNotFoundException("No C128 ROM directory found");
                Console.WriteLine($"ROMs: {roms128.Description} ({roms128.Directory})");
            }
            else
            {
                roms = options.RomDirectory is not null
                    ? RomSet.Load(options.RomDirectory)
                    : RomSet.TryLoadDefault() ?? throw new FileNotFoundException("No ROM directory found");
                Console.WriteLine($"ROMs: {roms.Description} ({roms.Directory})");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Cannot load the {(c128 ? "C128" : "C64")} ROMs: " + ex.Message);
            Console.Error.WriteLine();
            Console.Error.WriteLine(RomHelp);
            return 2;
        }
        if ((roms?.Drive1541 ?? roms128?.Drive1541) is null && !options.NoDrive)
            Console.WriteLine("No 1541 ROM found in the ROM directory: the disk drive is unavailable.");

        if (SDL_Init(SDL_INIT_VIDEO | SDL_INIT_AUDIO | SDL_INIT_GAMECONTROLLER | SDL_INIT_EVENTS) != 0)
        {
            Console.Error.WriteLine("SDL_Init failed: " + SDL_GetError());
            return 3;
        }

        EmulatorRunner? runner = null;
        SdlHost? host = null;
        SdlAudioSink? audio = null;
        try
        {
            try
            {
                audio = new SdlAudioSink(options.SampleRate);
                Console.WriteLine($"Audio: {audio.SampleRate} Hz, device buffer {audio.DeviceBufferSamples} samples");
            }
            catch (SdlException ex)
            {
                Console.WriteLine("Audio unavailable, running silent: " + ex.Message);
            }

            string bindingsPath = KeyBindingsPath;
            var bindings = File.Exists(bindingsPath) ? KeyBindings.LoadOrDefault(bindingsPath) : KeyBindings.CreateDefault(c128 ? MachineModel.C128 : MachineModel.C64);
            Console.WriteLine($"Key bindings: {bindingsPath}{(File.Exists(bindingsPath) ? "" : " (not found, using defaults)")}");

            // The session's sample rate must be the one the audio device actually runs at.
            int sampleRate = audio?.SampleRate ?? options.SampleRate;
            var session = roms128 is not null
                ? new EmulatorSession(roms128, sampleRate, attachDrive: !options.NoDrive, bindings, columns80: options.Columns80)
                : new EmulatorSession(roms!, sampleRate, attachDrive: !options.NoDrive, bindings);
            var video = new SdlVideoSink();
            session.Video = video;
            if (audio is not null)
                session.Audio = audio;

            try
            {
                AttachMedia(session, options);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Cannot attach media: " + ex.Message);
                return 4;
            }
            session.Warp = options.Warp;

            runner = new EmulatorRunner(session);
            host = new SdlHost(session, runner, video, audio, options.Scale, options.JoyPort);
            foreach (var file in options.Files)
                host.AttachFile(Path.GetFullPath(file), options.Autostart);

            runner.Start();
            host.Run();
            return 0;
        }
        catch (SdlException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 3;
        }
        finally
        {
            runner?.Dispose();   // stop the emulation thread before the sinks and SDL go away
            host?.Dispose();
            audio?.Dispose();
            SDL_Quit();
        }
    }

    private static void AttachMedia(EmulatorSession session, Options options)
    {
        if (options.Cartridge is not null)
        {
            session.AttachCartridgeFile(options.Cartridge);
            Console.WriteLine(session.MediaDescription);
        }
        if (options.Disk is not null)
        {
            session.AttachDiskFile(options.Disk, options.Autostart);
            Console.WriteLine(session.MediaDescription + (options.Autostart ? " (autostart)" : ""));
        }
        if (options.Tape is not null)
        {
            session.AttachProgramFile(options.Tape, 0, run: options.Autostart);
            Console.WriteLine(session.MediaDescription + (options.Autostart ? " (autostart)" : " (loaded, not started)"));
        }
    }
}
