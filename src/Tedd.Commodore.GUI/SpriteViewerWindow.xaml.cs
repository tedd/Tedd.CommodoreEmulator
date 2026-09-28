using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Tedd.Commodore.Emulator.C64;
using Tedd.Commodore.Emulator.Tools;
using Tedd.Commodore.Emulator.Video;
using Tedd.Commodore.Hosting;
// Tedd.WriteableBitmap puts a WriteableBitmap in the enclosing Tedd namespace, which wins over any
// same-named alias here. These views want WPF's; only the emulator screen trades it for the faster one.
using WpfBitmap = System.Windows.Media.Imaging.WriteableBitmap;

namespace Tedd.Commodore.GUI;

/// <summary>
/// Shows the eight VIC-II sprites: a thumbnail and a zoomed pixel view of the data each sprite pointer points
/// at, where the sprites sit relative to the display window, and the registers and internal DMA state behind
/// them (<see cref="SpriteSnapshot"/>). The machine is only read, through the runner, so the window works while
/// it runs (the snapshot is then taken between frames) and while it is frozen.
/// </summary>
public partial class SpriteViewerWindow : Window
{
    /// <summary>Names of the 16 C64 colors, in palette order.</summary>
    private static readonly string[] ColorNames =
    {
        "black", "white", "red", "cyan", "purple", "green", "blue", "yellow",
        "orange", "brown", "light red", "dark grey", "grey", "light green", "light blue", "light grey",
    };

    private readonly EmulatorRunner _runner;
    private readonly Action<bool> _setPaused;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly SpriteSnapshot _snapshot = new();
    private readonly SpriteItem[] _items = new SpriteItem[8];
    private readonly byte[][] _pixels = new byte[8][];
    private readonly StringBuilder _sb = new();
    private int _selected;
    private long _frames;
    private int _rasterLine, _rasterCycle;

    /// <param name="runner">The runner that owns the machine.</param>
    /// <param name="setPaused">Freezes/resumes the machine and keeps the main window's menu in sync.</param>
    public SpriteViewerWindow(EmulatorRunner runner, Action<bool> setPaused)
    {
        InitializeComponent();
        _runner = runner;
        _setPaused = setPaused;
        for (int n = 0; n < _items.Length; n++)
        {
            _pixels[n] = new byte[SpriteSnapshot.Width * SpriteSnapshot.Height];
            _items[n] = new SpriteItem(n);
        }
        SpriteList.ItemsSource = _items;
        SpriteList.SelectedIndex = 0;
        Refresh();
        _timer.Tick += (_, _) => { if (AutoRefresh.IsChecked == true) Refresh(); };
        _timer.Start();
        OnFreezeChanged();
    }

    /// <summary>Called by the main window when the pause state changes elsewhere.</summary>
    public void OnFreezeChanged()
    {
        FreezeButton.IsChecked = _runner.Paused;
        FreezeButton.Content = _runner.Paused ? "Resume" : "Freeze";
        Refresh();
    }

    private void Freeze_Click(object sender, RoutedEventArgs e)
    {
        _setPaused(FreezeButton.IsChecked == true);
        OnFreezeChanged();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();

    private void Zoom_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        ApplyViewOptions();
    }

    private void ViewOption_Changed(object sender, RoutedEventArgs e) => ApplyViewOptions();

    private void SpriteList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SpriteList.SelectedIndex >= 0)
            _selected = SpriteList.SelectedIndex;
        UpdateDetail();
        Layout.SetSprites(_snapshot.Sprites, _selected);
    }

    private void Refresh()
    {
        var machine = _runner.Session.Machine;
        _runner.Invoke(() =>
        {
            _snapshot.Update(machine.Vic, machine.VicBank);
            _frames = machine.Frames;
            _rasterLine = machine.Vic.RasterLine;
            _rasterCycle = machine.Vic.RasterCycle;
        });

        for (int n = 0; n < _items.Length; n++)
        {
            var sprite = _snapshot[n];
            sprite.Render(_pixels[n]);
            _items[n].Update(sprite, _pixels[n]);
        }
        UpdateGlobal();
        UpdateDetail();
        Layout.SetSprites(_snapshot.Sprites, _selected);
    }

    /// <summary>Zoom / grid / expansion / backdrop, applied to the large preview.</summary>
    private void ApplyViewOptions()
    {
        var sprite = _snapshot[_selected];
        double zoom = (ZoomBox.SelectedIndex switch { 0 => 2, 1 => 4, 3 => 12, 4 => 16, 5 => 24, _ => 8 });
        bool expand = ExpandBox.IsChecked == true;
        Preview.Zoom = zoom;
        Preview.ShowGrid = GridBox.IsChecked == true;
        Preview.BackgroundColor = BackgroundBox.IsChecked == true ? _snapshot.BackgroundColor : -1;
        Preview.SetSprite(_pixels[_selected], expand && sprite.ExpandX, expand && sprite.ExpandY);
    }

    private void UpdateDetail()
    {
        var s = _snapshot[_selected];
        ApplyViewOptions();
        DetailHeader.Text = $"Sprite {s.Index}   {(s.Enabled ? "ENABLED" : "disabled")}" +
                            (s.DmaActive ? "  DMA" : "") + (s.Displayed ? "  DISPLAY" : "");
        PreviewCaption.Text = $"{SpriteSnapshot.Width} x {SpriteSnapshot.Height} pixels, displayed as " +
                              $"{s.PixelWidth} x {s.PixelHeight}   data $" + s.CpuDataAddress.ToString("X4");

        var sb = _sb;
        sb.Clear();
        sb.Append("Position  X $").Append(s.X.ToString("X3")).Append(" (").Append(s.X).Append(")   Y $")
          .Append(s.Y.ToString("X2")).Append(" (").Append(s.Y).Append(")\n");
        sb.Append("          top left pixel at (").Append(s.DisplayX).Append(", ").Append(s.DisplayY)
          .Append(") in the display window\n");
        sb.Append("Registers $").Append((0xD000 + s.Index * 2).ToString("X4")).Append("/$")
          .Append((0xD001 + s.Index * 2).ToString("X4")).Append("   MSB in $D010 bit ").Append(s.Index).Append('\n');
        sb.Append("Color     $").Append((0xD027 + s.Index).ToString("X4")).Append(" = ").Append(s.Color)
          .Append(" (").Append(ColorNames[s.Color & 15]).Append(")\n");
        sb.Append("Mode      ").Append(s.Multicolor ? "multicolor: $D025 = " + _snapshot.MulticolorColor0 +
                                                      ", $D026 = " + _snapshot.MulticolorColor1
                                                    : "single color (one bit per pixel)").Append('\n');
        sb.Append("Expansion ").Append(s.ExpandX ? "X" : "-").Append(s.ExpandY ? " Y" : " -")
          .Append("   ($D01D / $D017 bit ").Append(s.Index).Append(")\n");
        sb.Append("Priority  ").Append(s.BehindForeground ? "behind foreground graphics" : "in front of graphics")
          .Append(" ($D01B bit ").Append(s.Index).Append(" = ").Append(s.BehindForeground ? 1 : 0).Append(")\n");
        sb.Append("Pointer   $").Append(s.CpuPointerAddress.ToString("X4")).Append(" = $")
          .Append(s.Pointer.ToString("X2")).Append("   data $").Append(s.CpuDataAddress.ToString("X4"))
          .Append("-$").Append((s.CpuDataAddress + SpriteSnapshot.DataSize - 1).ToString("X4")).Append('\n');
        sb.Append("Sequencer MC ").Append(s.Mc).Append(" (line ").Append(s.CurrentLine < 0 ? "-" : s.CurrentLine.ToString())
          .Append(")  MCBASE ").Append(s.McBase).Append("  Y-exp FF ").Append(s.ExpansionFlipFlop ? "set" : "clear")
          .Append("\n          latched pointer $").Append(s.LatchedPointer.ToString("X2"))
          .Append("  shift register $").Append(s.ShiftRegister.ToString("X6")).Append('\n');
        sb.Append("Collision sprite-sprite ").Append(s.SpriteCollision ? "YES" : "no")
          .Append("   sprite-data ").Append(s.DataCollision ? "YES" : "no").Append('\n');
        sb.Append("Pixels    ").Append(s.SolidPixelCount()).Append(" of ")
          .Append(SpriteSnapshot.Width * SpriteSnapshot.Height).Append(" not transparent");
        DetailText.Text = sb.ToString();

        sb.Clear();
        for (int row = 0; row < SpriteSnapshot.Height; row++)
        {
            int at = row * 3;
            sb.Append('$').Append((s.CpuDataAddress + at).ToString("X4")).Append("  ")
              .Append(s.Data[at].ToString("X2")).Append(' ').Append(s.Data[at + 1].ToString("X2")).Append(' ')
              .Append(s.Data[at + 2].ToString("X2")).Append("  ");
            for (int x = 0; x < SpriteSnapshot.Width; x++)
            {
                byte c = _pixels[_selected][row * SpriteSnapshot.Width + x];
                sb.Append(c == SpriteSnapshot.Transparent ? '.' : c.ToString("X")[0]);
            }
            if (row < SpriteSnapshot.Height - 1) sb.Append('\n');
        }
        DataText.Text = sb.ToString();
    }

    private void UpdateGlobal()
    {
        var sb = _sb;
        sb.Clear();
        sb.Append("$D015 enable   ").Append(Bits(_snapshot.EnableRegister))
          .Append("    $D017 Y expand ").Append(Bits(_snapshot.ExpandYRegister))
          .Append("    $D01B priority ").Append(Bits(_snapshot.PriorityRegister)).Append('\n');
        sb.Append("$D01C multicol ").Append(Bits(_snapshot.MulticolorRegister))
          .Append("    $D01D X expand ").Append(Bits(_snapshot.ExpandXRegister))
          .Append("    $D021 bg 0     ").Append(_snapshot.BackgroundColor).Append('\n');
        sb.Append("$D01E spr-spr  ").Append(Bits(_snapshot.SpriteSpriteCollisions))
          .Append("    $D01F spr-data ").Append(Bits(_snapshot.SpriteDataCollisions))
          .Append("    $D025/$D026 = ").Append(_snapshot.MulticolorColor0).Append(" / ")
          .Append(_snapshot.MulticolorColor1).Append("   (collisions shown without clearing them)\n");
        sb.Append("Pointers at $").Append((_snapshot.BankBase + _snapshot.VideoMatrix + 0x3F8).ToString("X4"))
          .Append("-$").Append((_snapshot.BankBase + _snapshot.VideoMatrix + 0x3FF).ToString("X4"))
          .Append("   VIC bank ").Append(_snapshot.BankBase >> 14).Append(" ($")
          .Append(_snapshot.BankBase.ToString("X4")).Append("-$").Append((_snapshot.BankBase + 0x3FFF).ToString("X4"))
          .Append(")   video matrix $").Append((_snapshot.BankBase + _snapshot.VideoMatrix).ToString("X4")).Append('\n');
        sb.Append(_snapshot.ActiveDmaCount).Append(" of 8 sprites have DMA on (about ")
          .Append(_snapshot.ActiveDmaCount * 2).Append(" cycles per raster line taken from the CPU)")
          .Append("   raster line ").Append(_rasterLine).Append(", cycle ").Append(_rasterCycle)
          .Append(", frame ").Append(_frames);
        if (!_runner.Paused) sb.Append('\n').Append("Read between frames - freeze to see the sprite state on an exact raster line.");
        GlobalText.Text = sb.ToString();
    }

    private static string Bits(byte value)
    {
        var chars = new char[10];
        chars[0] = '%';
        for (int i = 0; i < 8; i++)
            chars[i + 1] = (value & (0x80 >> i)) != 0 ? '1' : '0';
        chars[9] = ' ';
        return new string(chars, 0, 9);
    }

    private void SavePng_Click(object sender, RoutedEventArgs e)
    {
        var s = _snapshot[_selected];
        bool expandX = ExpandBox.IsChecked == true && s.ExpandX;
        bool expandY = ExpandBox.IsChecked == true && s.ExpandY;
        int scaleX = expandX ? 2 : 1, scaleY = expandY ? 2 : 1;
        int width = SpriteSnapshot.Width * scaleX, height = SpriteSnapshot.Height * scaleY;
        var dlg = new SaveFileDialog
        {
            Filter = "PNG image (*.png)|*.png",
            FileName = $"sprite{_selected}-{s.CpuDataAddress:X4}.png",
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var pixels = new uint[width * height];
            var source = _pixels[_selected];
            uint backdrop = VicII.Palette[_snapshot.BackgroundColor & 15];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    byte c = source[y / scaleY * SpriteSnapshot.Width + x / scaleX];
                    pixels[y * width + x] = c == SpriteSnapshot.Transparent ? backdrop : VicII.Palette[c & 15];
                }
            PngWriter.Save(dlg.FileName, width, height, pixels);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Cannot save the sprite", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        base.OnClosed(e);
    }

    /// <summary>One entry of the sprite list: a 24 x 21 thumbnail plus a two line summary.</summary>
    public sealed class SpriteItem : INotifyPropertyChanged
    {
        private readonly int[] _buffer = new int[SpriteSnapshot.Width * SpriteSnapshot.Height];
        private string _summary = "";

        public SpriteItem(int index)
        {
            Title = "Sprite " + index;
            Thumbnail = new WpfBitmap(SpriteSnapshot.Width, SpriteSnapshot.Height, 96, 96, PixelFormats.Bgra32, null);
        }

        public string Title { get; }
        public WpfBitmap Thumbnail { get; }

        public string Summary
        {
            get => _summary;
            private set
            {
                if (_summary == value) return;
                _summary = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Summary)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public void Update(SpriteInfo sprite, byte[] pixels)
        {
            for (int y = 0; y < SpriteSnapshot.Height; y++)
                for (int x = 0; x < SpriteSnapshot.Width; x++)
                {
                    byte c = pixels[y * SpriteSnapshot.Width + x];
                    // Transparent pixels get a checkerboard so an empty sprite is not just a blank box.
                    _buffer[y * SpriteSnapshot.Width + x] = c == SpriteSnapshot.Transparent
                        ? (((x / 3) + (y / 3)) & 1) == 0 ? unchecked((int)0xFF2A2A2A) : unchecked((int)0xFF343434)
                        : unchecked((int)VicII.Palette[c & 15]);
                }
            Thumbnail.WritePixels(new Int32Rect(0, 0, SpriteSnapshot.Width, SpriteSnapshot.Height),
                _buffer, SpriteSnapshot.Width * 4, 0);

            var flags = new List<string>(6);
            flags.Add(sprite.Enabled ? "on" : "off");
            if (sprite.Multicolor) flags.Add("MC");
            if (sprite.ExpandX) flags.Add("2X");
            if (sprite.ExpandY) flags.Add("2Y");
            if (sprite.BehindForeground) flags.Add("behind");
            if (sprite.DmaActive) flags.Add("DMA");
            if (sprite.SpriteCollision || sprite.DataCollision) flags.Add("hit");
            Summary = $"X {sprite.X,3} Y {sprite.Y,3}  col {sprite.Color}\n" +
                      $"ptr ${sprite.Pointer:X2} -> ${sprite.CpuDataAddress:X4}\n" +
                      string.Join(" ", flags);
        }
    }
}
