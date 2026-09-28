using System.Text;
using Tedd.Commodore.Emulator.Tools;
using Tedd.Commodore.Hosting;
using Tedd.Commodore.Maui.Services;
using Tedd.Commodore.Maui.Views;

namespace Tedd.Commodore.Maui.Pages;

/// <summary>
/// Shows a character generator as a grid of glyphs, with the technical detail behind each one: screen code,
/// the PETSCII codes that print it, where its 8 bytes live in the ROM image, in the CPU address space and in
/// the VIC banks. Three sources can be shown: the machine's character ROM, a character set loaded from a file,
/// and - live - the 2 KiB the VIC-II is actually reading, which is how a program's own character set appears
/// while it is being built. A loaded set can be written over the running machine's character ROM.
/// </summary>
public partial class CharsetViewerPage : ContentPage
{
    private static readonly int[] ZoomLevels = { 2, 3, 4, 6, 8, 10 };

    private const string Notes =
        "The character generator ROM is 4096 bytes: set 1 (uppercase + graphics) at $0000 and set 2 " +
        "(lowercase + uppercase) at $0800. Each set holds 256 characters of 8 bytes, one byte per pixel row, " +
        "MSB leftmost; $80-$FF are the same glyphs inverted, which is what reverse video draws.\n\n" +
        "CPU: the ROM appears at $D000-$DFFF while CHAREN ($01 bit 2) is 0 and LORAM or HIRAM is 1 - the " +
        "I/O registers and the character ROM share those addresses.\n\n" +
        "VIC: the PLA shadows the ROM into $1000-$1FFF of VIC banks 0 and 2, so the VIC sees it at $1000 " +
        "(bank 0) and $9000 (bank 2). Banks 1 and 3 see RAM there, which is why a program with its own " +
        "character set usually keeps it in bank 0 at $2000, $2800, $3000 or $3800.\n\n" +
        "$D018 bits 3-1 pick the 2 KiB character base inside the bank (bits 7-4 pick the video matrix). With " +
        "the screen at $0400, POKE 53272,21 selects ROM set 1 and 23 selects set 2. From BASIC, " +
        "PRINT CHR$(142) switches to set 1 and CHR$(14) to set 2; CHR$(8) and CHR$(9) lock and unlock the " +
        "SHIFT + C= shortcut.\n\n" +
        "\"Apply to machine\" copies a loaded image over the running machine's character ROM. It takes effect " +
        "from the next character fetch and lasts until the emulator is closed; nothing is written to disk.";

    private enum ViewSource { MachineRom, File, Vic }

    private enum ViewSet { Set1, Set2, Both }

    private readonly EmulatorRunner _runner;
    private readonly IDispatcherTimer _timer;
    private readonly byte[] _originalRom;
    private readonly string _charRomName;
    private KeyboardHook? _keyboard;
    private byte[]? _file;
    private string _fileName = "";
    private CharacterSet? _set;
    private ViewSource _source = ViewSource.MachineRom;
    private ViewSet _setChoice = ViewSet.Set1;
    private int _setOffset;      // where the shown block starts inside the 4096 byte character generator image
    private int _vicBank;
    private byte _d018;
    private string? _appliedName;   // what was written over the character ROM, so the name shown stays honest
    private bool _ready;

    /// <param name="runner">The runner that owns the machine; everything it reads or writes goes through it.</param>
    public CharsetViewerPage(EmulatorRunner runner)
    {
        InitializeComponent();
        _runner = runner;
        var roms = runner.Session.Roms;
        _originalRom = (byte[])roms.Char.Clone();
        _charRomName = CharRomName(roms.Description);
        NotesText.Text = Notes;
        BuildOptionBoxes();
        Charset.SelectionChanged += (_, _) => UpdateCharacter();
        Charset.HoverChanged += (_, index) => HoverText.Text = DescribeShort(index);

        _timer = Dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(250);
        _timer.Tick += (_, _) => { if (AutoRefresh.IsChecked && _source != ViewSource.File && !_runner.Paused) Reload(); };
    }

    private void Page_Loaded(object? sender, EventArgs e)
    {
        if (_ready) return;
        _ready = true;
        ApplyDisplayOptions();
        Reload();
        if (Window is { } window)
        {
            _keyboard = KeyboardHook.Attach(window);
            if (_keyboard is not null) _keyboard.KeyDown += OnKeyDown;
        }
        _timer.Start();
    }

    private void Page_Unloaded(object? sender, EventArgs e)
    {
        _timer.Stop();
        if (_keyboard is not null)
        {
            _keyboard.KeyDown -= OnKeyDown;
            _keyboard.Dispose();
            _keyboard = null;
        }
        // Frees the native pixel buffers behind the two pictures; the window is not reused.
        Charset.Dispose();
        Glyph.Dispose();
    }

    /// <summary>Arrow keys walk the grid, the way they did in the WPF viewer.</summary>
    private void OnKeyDown(PhysicalKeyEvent e)
    {
        if (Charset.MoveSelection(e.Code)) e.Handled = true;
    }

    /// <summary>Picks the character ROM out of a <see cref="Tedd.Commodore.Emulator.C64.RomSet.Description"/>.</summary>
    private static string CharRomName(string description)
    {
        foreach (var part in description.Split(','))
        {
            string token = part.Trim();
            if (token.StartsWith("CHAR=", StringComparison.Ordinal))
                return token.Substring("CHAR=".Length);
        }
        return "character ROM";
    }

    private static string SetName(bool firstSet) => firstSet ? "set 1 (uppercase/graphics)" : "set 2 (lowercase/uppercase)";

    #region display options

    private void BuildOptionBoxes()
    {
        SourceBox.Items.Add("Machine character ROM");
        SourceBox.Items.Add("Loaded file (none yet)");
        SourceBox.Items.Add("VIC-II live (what $D018 points at)");
        SourceBox.SelectedIndex = 0;
        SetBox.SelectedIndex = 0;

        foreach (int zoom in ZoomLevels)
            ZoomBox.Items.Add(zoom + "x");
        ZoomBox.SelectedIndex = Array.IndexOf(ZoomLevels, 3);   // 32 columns at 3x fit the default width
        FillColorBox(InkBox, 14);
        FillColorBox(PaperBox, 6);
    }

    private static void FillColorBox(Picker box, int selected)
    {
        for (int i = 0; i < C64Palette.ShortNames.Length; i++)
            box.Items.Add($"{i,2} {C64Palette.ShortNames[i]}");
        box.SelectedIndex = selected;
    }

    private void Display_Changed(object? sender, EventArgs e)
    {
        if (!_ready) return;
        ApplyDisplayOptions();
        UpdateCharacter();
    }

    private void ApplyDisplayOptions()
    {
        Charset.Zoom = ZoomLevels[Math.Clamp(ZoomBox.SelectedIndex, 0, ZoomLevels.Length - 1)];
        Charset.Ink = Math.Max(0, InkBox.SelectedIndex);
        Charset.Paper = Math.Max(0, PaperBox.SelectedIndex);
        Charset.ShowGrid = GridBox.IsChecked;
    }

    #endregion

    #region reading the character set

    private void Source_Changed(object? sender, EventArgs e)
    {
        if (!_ready) return;
        // The "loaded file" entry is only usable once something has been loaded.
        if (SourceBox.SelectedIndex == (int)ViewSource.File && _file is null)
        {
            SourceBox.SelectedIndex = (int)_source;
            return;
        }
        _source = (ViewSource)Math.Max(0, SourceBox.SelectedIndex);
        _setChoice = (ViewSet)Math.Max(0, SetBox.SelectedIndex);
        Reload();
    }

    private void Refresh_Clicked(object? sender, EventArgs e) => Reload();

    /// <summary>Size of the image the current source works on: a file, or the 4096 byte character ROM.</summary>
    private int ImageSize() => _source == ViewSource.File ? _file?.Length ?? 0 : CharacterSet.RomSize;

    /// <summary>Which part of that image the Set box asks for; a file smaller than the ROM is shown whole.</summary>
    private (int Offset, int Length) Slice(int imageSize) => imageSize < CharacterSet.RomSize
        ? (0, imageSize)
        : _setChoice switch
        {
            ViewSet.Set2 => (CharacterSet.LowercaseSetOffset, CharacterSet.SetSize),
            ViewSet.Both => (0, CharacterSet.RomSize),
            _ => (0, CharacterSet.SetSize),
        };

    private void Reload()
    {
        CharacterSet set;
        if (_source == ViewSource.Vic)
        {
            CharacterSet? captured = null;
            byte d018 = 0;
            int bank = 0;
            _runner.Invoke(() =>
            {
                var machine = _runner.Session.Machine;
                bank = machine.VicBank;
                d018 = machine.Vic.Peek(0x18);
                captured = CharacterSet.FromVic(machine.Vic, bank);
            });
            set = captured!;
            _vicBank = bank;
            _d018 = d018;
            // The ROM shadow starts at $1000 or $1800, so this is the offset of the set inside the ROM image.
            _setOffset = set.IsRomShadow ? set.VicAddress - CharacterSet.VicShadowBase : 0;
        }
        else
        {
            byte[] image = _source == ViewSource.File
                ? _file ?? Array.Empty<byte>()
                : _runner.Invoke(() => (byte[])_runner.Session.Roms.Char.Clone());
            if (image.Length == 0) return;
            var (offset, length) = Slice(image.Length);
            _setOffset = offset;
            var data = new byte[length];
            Array.Copy(image, offset, data, 0, length);
            set = new CharacterSet(data, _source == ViewSource.File ? _fileName : _appliedName ?? _charRomName);
        }

        SetBox.IsEnabled = _source != ViewSource.Vic;
        // Only redraw the (potentially 512 glyph) grid when the bytes really changed; the timer polls often.
        bool changed = _set is null || _set.Crc32 != set.Crc32 || _set.Count != set.Count || _set.Name != set.Name;
        _set = set;
        if (changed) Charset.Source = set;
        UpdateSetInfo();
        UpdateCharacter();
    }

    #endregion

    #region information

    private static void Line(StringBuilder sb, string label, string value) =>
        sb.Append(label.PadRight(9)).Append(' ').AppendLine(value);

    private void UpdateSetInfo()
    {
        var set = _set;
        if (set is null) { SetText.Text = ""; return; }
        int imageSize = ImageSize();
        var sb = new StringBuilder();

        Line(sb, "Source", _source switch
        {
            ViewSource.File => "a file loaded into this window",
            ViewSource.Vic => "the running machine, as the VIC-II reads it",
            _ => "the running machine's character ROM",
        });
        Line(sb, "Name", set.Name);
        Line(sb, "Showing", _source == ViewSource.Vic
            ? "the 2 KiB at the current character base"
            : imageSize < CharacterSet.RomSize
                ? "the whole file (one character set)"
                : _setChoice == ViewSet.Both ? "both sets" : SetName(_setChoice == ViewSet.Set1));
        Line(sb, "Size", $"{set.Size} bytes = {set.Count} characters of {CharacterSet.CharacterWidth} x {CharacterSet.CharacterHeight}");
        Line(sb, "", $"{CharacterSet.BytesPerCharacter} bytes each, 1 bit per pixel, MSB leftmost");
        Line(sb, "CRC-32", $"${set.Crc32:X8}");

        if (_source == ViewSource.Vic)
        {
            int bankBase = _vicBank << 14;
            Line(sb, "VIC bank", $"{_vicBank} (${bankBase:X4}-${bankBase + 0x3FFF:X4})");
            Line(sb, "$D018", $"${_d018:X2} -> character base ${set.VicAddress:X4} in the bank");
            Line(sb, "Address", $"${bankBase + set.VicAddress:X4}-${bankBase + set.VicAddress + set.Size - 1:X4} to the CPU");
            if (set.IsRomShadow)
            {
                Line(sb, "Reading", "the character ROM shadow");
                Line(sb, "", SetName(set.VicAddress == CharacterSet.VicShadowBase));
            }
            else
            {
                Line(sb, "Reading", "RAM - a character set installed by software");
            }
        }
        else
        {
            int end = _setOffset + set.Size - 1;
            Line(sb, "Image", $"${_setOffset:X4}-${end:X4} of {imageSize} bytes");
            if (end < CharacterSet.RomSize)
            {
                Line(sb, "CPU", $"${CharacterSet.CpuBase + _setOffset:X4}-${CharacterSet.CpuBase + end:X4} while CHAREN = 0");
                Line(sb, "VIC", $"${CharacterSet.VicShadowBase + _setOffset:X4}-${CharacterSet.VicShadowBase + end:X4} in banks 0 and 2");
            }
            if (set.Size == CharacterSet.SetSize && imageSize == CharacterSet.RomSize)
                Line(sb, "Select", _setOffset == 0 ? "PRINT CHR$(142)  /  POKE 53272,21" : "PRINT CHR$(14)   /  POKE 53272,23");
            if (_source == ViewSource.File && imageSize == CharacterSet.SetSize)
                Line(sb, "Target", SetName(_setChoice != ViewSet.Set2) + " - from the Set box");
        }

        if (set.Size >= CharacterSet.SetSize)
        {
            int mismatches = set.InvertedMismatchCount(0);
            Line(sb, "Reverse", mismatches == 0
                ? "$80-$FF are the inverted copies of $00-$7F"
                : $"$80-$FF are inverted copies of $00-$7F, bar {mismatches}");
        }
        Line(sb, "Blank", $"{BlankCount(set)} of {set.Count} characters are empty");
        SetText.Text = sb.ToString().TrimEnd();
    }

    private static int BlankCount(CharacterSet set)
    {
        int blank = 0;
        for (int i = 0; i < set.Count; i++)
            if (set.IsBlank(i)) blank++;
        return blank;
    }

    private void UpdateCharacter()
    {
        var set = _set;
        if (set is null || set.Count == 0)
        {
            Glyph.Clear();
            BytesText.Text = "";
            CharText.Text = "";
            return;
        }

        int index = Math.Clamp(Charset.SelectedIndex, 0, set.Count - 1);
        var glyph = set.Glyph(index);
        Glyph.SetGlyph(glyph, Charset.Ink, Charset.Paper);

        var bytes = new StringBuilder("row byte  pixels\n");
        for (int row = 0; row < glyph.Length; row++)
        {
            bytes.Append(' ').Append(row).Append("  $").Append(glyph[row].ToString("X2")).Append("  ");
            for (int bit = 7; bit >= 0; bit--)
                bytes.Append(((glyph[row] >> bit) & 1) != 0 ? '#' : '.');
            bytes.AppendLine();
        }
        BytesText.Text = bytes.ToString().TrimEnd();

        int code = index & 0xFF;
        int romOffset = _setOffset + index * CharacterSet.BytesPerCharacter;
        bool lowercase = romOffset >= CharacterSet.LowercaseSetOffset;
        bool fromRom = _source != ViewSource.Vic || set.IsRomShadow;

        var sb = new StringBuilder();
        Line(sb, "Character", $"${code:X2} ({code})   grid row {index / Charset.Columns}, column {index % Charset.Columns}");
        Line(sb, "Meaning", CharacterSet.Describe(code, lowercase) + (fromRom
            ? "   in set " + (lowercase ? "2" : "1")
            : "   in ROM set 1; this set draws its own glyph"));
        Line(sb, "Screen", $"POKE 1024,{code} : POKE 55296,{Charset.Ink}");

        var petscii = CharacterSet.PetsciiCodesFor(code);
        if (petscii.Length == 0)
        {
            Line(sb, "PETSCII", "none - the reverse video half; CHR$(18) turns");
            Line(sb, "", "reverse on, CHR$(146) off");
        }
        else
        {
            var codes = new StringBuilder();
            for (int i = 0; i < petscii.Length; i++)
                codes.Append(i > 0 ? ", " : "").Append('$').Append(petscii[i].ToString("X2")).Append(" (").Append(petscii[i]).Append(')');
            Line(sb, "PETSCII", codes.ToString());
            Line(sb, "", $"PRINT CHR$({petscii[0]})");
        }

        if (_source == ViewSource.Vic)
        {
            int address = (_vicBank << 14) + set.VicAddress + index * CharacterSet.BytesPerCharacter;
            Line(sb, "Data", $"${address:X4}-${address + CharacterSet.BytesPerCharacter - 1:X4} (VIC ${set.VicAddress + index * CharacterSet.BytesPerCharacter:X4})");
        }
        else
        {
            Line(sb, "Data", $"${romOffset:X4}-${romOffset + CharacterSet.BytesPerCharacter - 1:X4} in the image (byte {romOffset})");
            if (romOffset + CharacterSet.BytesPerCharacter <= CharacterSet.RomSize)
            {
                Line(sb, "CPU", $"${CharacterSet.CpuBase + romOffset:X4}-${CharacterSet.CpuBase + romOffset + CharacterSet.BytesPerCharacter - 1:X4}");
                Line(sb, "VIC", $"${CharacterSet.VicShadowBase + romOffset:X4} in bank 0, ${0x9000 + romOffset:X4} in bank 2");
            }
        }
        Line(sb, "Pixels", $"{set.SolidPixelCount(index)} of {CharacterSet.CharacterWidth * CharacterSet.CharacterHeight} set");
        CharText.Text = sb.ToString().TrimEnd();
    }

    private string DescribeShort(int index)
    {
        var set = _set;
        if (set is null || index < 0 || index >= set.Count) return "";
        int code = index & 0xFF;
        bool lowercase = _setOffset + index * CharacterSet.BytesPerCharacter >= CharacterSet.LowercaseSetOffset;
        return $"${code:X2} / {code}   {CharacterSet.Describe(code, lowercase)}";
    }

    #endregion

    #region loading and applying a character set

    private async void Load_Clicked(object? sender, EventArgs e)
    {
        var path = await FileDialogs.OpenAsync("Load a character set", ".bin", ".rom", ".chr", ".64c", ".prg");
        if (path is null) return;
        try
        {
            var data = File.ReadAllBytes(path);
            // .64c and character sets saved as a PRG start with a two byte load address.
            if (data.Length % CharacterSet.BytesPerCharacter == 2)
                data = data[2..];
            if (data.Length == 0 || data.Length > CharacterSet.RomSize || data.Length % CharacterSet.BytesPerCharacter != 0)
                throw new InvalidDataException(
                    $"A character set is a multiple of {CharacterSet.BytesPerCharacter} bytes and at most " +
                    $"{CharacterSet.RomSize} ({CharacterSet.RomSize} = both sets, {CharacterSet.SetSize} = one set). " +
                    $"{Path.GetFileName(path)} has {data.Length} usable bytes.");

            _file = data;
            _fileName = Path.GetFileName(path);
            SourceBox.Items[(int)ViewSource.File] = "Loaded file: " + _fileName;
            ApplyButton.IsEnabled = data.Length == CharacterSet.SetSize || data.Length == CharacterSet.RomSize;
            _source = ViewSource.File;
            SourceBox.SelectedIndex = (int)ViewSource.File;
            Reload();
            StatusText.Text = ApplyButton.IsEnabled
                ? $"Loaded {_fileName}, {data.Length} bytes. \"Apply to machine\" writes it over the running character ROM."
                : $"Loaded {_fileName}, {data.Length} bytes - viewing only; applying needs exactly {CharacterSet.SetSize} or {CharacterSet.RomSize} bytes.";
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Cannot load character set", ex.Message, "OK");
        }
    }

    private async void Apply_Clicked(object? sender, EventArgs e)
    {
        if (_file is not { } image) return;
        int offset = image.Length == CharacterSet.RomSize || _setChoice != ViewSet.Set2 ? 0 : CharacterSet.LowercaseSetOffset;
        try
        {
            _runner.Invoke(() => _runner.Session.Roms.ReplaceChar(image, offset));
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Cannot apply character set", ex.Message, "OK");
            return;
        }
        RestoreButton.IsEnabled = true;
        _appliedName = image.Length == CharacterSet.RomSize
            ? _fileName
            : $"{_charRomName} + {_fileName} in set {(offset == 0 ? 1 : 2)}";
        StatusText.Text = image.Length == CharacterSet.RomSize
            ? $"Applied {_fileName} over the whole character ROM ($0000-$0FFF) of the running machine."
            : $"Applied {_fileName} to {SetName(offset == 0)}, ${offset:X4}-${offset + CharacterSet.SetSize - 1:X4} of the character ROM.";
        if (_source != ViewSource.File) Reload();
    }

    private void Restore_Clicked(object? sender, EventArgs e)
    {
        _runner.Invoke(() => _runner.Session.Roms.ReplaceChar(_originalRom));
        _appliedName = null;
        RestoreButton.IsEnabled = false;
        StatusText.Text = "Character ROM restored to what it was when this window was opened.";
        if (_source != ViewSource.File) Reload();
    }

    #endregion
}
