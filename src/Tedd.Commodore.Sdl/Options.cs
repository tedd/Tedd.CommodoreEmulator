using System;
using System.Collections.Generic;

namespace Tedd.Commodore.Sdl;

/// <summary>Command line options.</summary>
internal sealed class Options
{
    public string? RomDirectory;
    /// <summary>"c64" (default) or "c128".</summary>
    public string Machine = "c64";
    /// <summary>C128: start with the 40/80 DISPLAY key down (80 columns on the VDC).</summary>
    public bool Columns80;
    public string? Disk;
    public string? Tape;
    public string? Cartridge;
    public bool Autostart;
    public bool Warp;
    public bool NoDrive;
    public int Scale = 3;
    public int JoyPort = 2;
    public int SampleRate = 44100;
    public bool ShowHelp;
    /// <summary>Bare file arguments, attached by extension/content like a drag-and-drop.</summary>
    public readonly List<string> Files = new();

    public const string Usage = """
        Usage: Tedd.Commodore.Sdl [options] [file ...]

          --machine <c64|c128> the computer to emulate (default c64)
          --80                C128: press the 40/80 DISPLAY key, i.e. boot on the 80 column VDC screen
          --roms <dir>        directory with the ROM images (default: C64_ROMS / C128_ROMS env var, the executable's
                              directory, the current directory or a "roms" sub-directory of their ancestors)
          --disk <file.d64>   insert a disk image into drive 8
          --tape <file>       load a program from a .t64 tape image or a .prg file (injected into memory)
          --cart <file>       plug in a cartridge (.crt, or a raw 8K/16K .bin image)
          --autostart         run the attached disk (LOAD"*",8,1 + RUN) / tape program automatically
          --warp              start in warp mode (as fast as possible, no sound)
          --no-drive          do not attach a 1541 drive
          --scale <N>         initial window scale (default 3 = 1152x816)
          --joy-port <1|2>    C64 joystick port for game controllers (default 2)
          --sample-rate <N>   requested audio sample rate (default 44100; the device may pick another)
          --help              this text

        Bare file arguments (.d64/.t64/.prg/.crt/.bin) are attached like a drag-and-drop; --autostart applies to them too.

        Window: left Alt+Enter or left Alt+F toggles fullscreen, closing the window quits (Escape is RUN/STOP).
        Keys follow the key bindings file (defaults: F11 reset, F12 screenshot, Pause freeze, Page Up RESTORE,
        numeric keypad = joystick 2). Drop a .d64/.t64/.prg/.crt onto the window to attach it.
        """;

    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string? inlineValue = null;
            if (a.StartsWith("--", StringComparison.Ordinal) && a.IndexOf('=') is > 0 and var eq)
            {
                inlineValue = a[(eq + 1)..];
                a = a[..eq];
            }

            string Next()
            {
                if (inlineValue is not null) return inlineValue;
                if (i + 1 < args.Length) return args[++i];
                throw new ArgumentException($"{a} needs a value");
            }

            switch (a.ToLowerInvariant())
            {
                case "--roms": o.RomDirectory = Next(); break;
                case "--machine":
                    o.Machine = Next().ToLowerInvariant();
                    if (o.Machine is not ("c64" or "c128")) throw new ArgumentException($"--machine expects c64 or c128, got '{o.Machine}'");
                    break;
                case "--80": o.Columns80 = true; break;
                case "--disk": o.Disk = Next(); break;
                case "--tape": o.Tape = Next(); break;
                case "--cart": o.Cartridge = Next(); break;
                case "--autostart": o.Autostart = true; break;
                case "--warp": o.Warp = true; break;
                case "--no-drive": o.NoDrive = true; break;
                case "--scale": o.Scale = ParseInt(a, Next(), 1, 16); break;
                case "--joy-port": o.JoyPort = ParseInt(a, Next(), 1, 2); break;
                case "--sample-rate": o.SampleRate = ParseInt(a, Next(), 8000, 192000); break;
                case "--help":
                case "-h":
                case "-?":
                case "/?":
                    o.ShowHelp = true; break;
                default:
                    if (a.StartsWith('-'))
                        throw new ArgumentException($"Unknown option {a}");
                    o.Files.Add(a);
                    break;
            }
        }
        return o;
    }

    private static int ParseInt(string option, string value, int min, int max)
    {
        if (!int.TryParse(value, out int n) || n < min || n > max)
            throw new ArgumentException($"{option} expects a number between {min} and {max}, got '{value}'");
        return n;
    }
}
