using System.Text;
using Tedd.Commodore.Emulator.Tools;
using Tedd.Commodore.Emulator.Video;
using Tedd.Commodore.Hosting;
using Tedd.Commodore.Maui.Services;
using Tedd.Commodore.Maui.Views;

namespace Tedd.Commodore.Maui.Pages;

/// <summary>
/// Shows the eight VIC-II sprites: a thumbnail and a zoomed pixel view of the data each sprite pointer points
/// at, where the sprites sit relative to the display window, and the registers and internal DMA state behind
/// them (<see cref="SpriteSnapshot"/>). The machine is only read, through the runner, so the window works while
/// it runs (the snapshot is then taken between frames) and while it is frozen.
/// </summary>
public partial class SpriteViewerPage : ContentPage
{
    private static readonly int[] ZoomLevels = { 2, 4, 8, 12, 16, 24 };

    private readonly EmulatorRunner _runner;
    private readonly Action<bool> _setPaused;
    private readonly IDispatcherTimer _timer;
    private readonly SpriteSnapshot _snapshot = new();
    private readonly SpriteListItem[] _items = new SpriteListItem[8];
    private readonly byte[][] _pixels = new byte[8][];
    private readonly SpriteLayoutDrawable _layout = new();
    private readonly StringBuilder _sb = new();
    private int _selected;
    private long _frames;
    private int _rasterLine, _rasterCycle;
    private bool _ready;

    /// <param name="runner">The runner that owns the machine.</param>
    /// <param name="setPaused">Freezes/resumes the machine and keeps the main window's menu in sync.</param>
    public SpriteViewerPage(EmulatorRunner runner, Action<bool> setPaused)
    {
        InitializeComponent();
        _runner = runner;
        _setPaused = setPaused;
        LayoutView.Drawable = _layout;

        for (int n = 0; n < _items.Length; n++)
        {
            _pixels[n] = new byte[SpriteSnapshot.Width * SpriteSnapshot.Height];
            _items[n] = new SpriteListItem(n, Select);
            SpriteList.Children.Add(_items[n].View);
        }
        // Only from here on can the view option handlers run: they read the buffers built above.
        _ready = true;
        ZoomBox.SelectedIndex = 2;   // 8x
        Select(0);
        Refresh();

        _timer = Dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(100);
        _timer.Tick += (_, _) => { if (AutoRefresh.IsChecked) Refresh(); };
        _timer.Start();
        OnFreezeChanged();
    }

    /// <summary>Called by the main window when the pause state changes elsewhere.</summary>
    public void OnFreezeChanged()
    {
        FreezeButton.Text = _runner.Paused ? "Resume" : "Freeze";
        Refresh();
    }

    private void Page_Unloaded(object? sender, EventArgs e)
    {
        _timer.Stop();
        // Frees the native pixel buffers behind the preview; the window is not reused.
        Preview.Dispose();
    }

    private void Freeze_Clicked(object? sender, EventArgs e)
    {
        _setPaused(!_runner.Paused);
        OnFreezeChanged();
    }

    private void Refresh_Clicked(object? sender, EventArgs e) => Refresh();

    private void ViewOption_Changed(object? sender, EventArgs e)
    {
        if (_ready) UpdateDetail();
    }

    private void Select(int index)
    {
        _selected = index;
        for (int n = 0; n < _items.Length; n++)
            _items[n].SetSelected(n == index);
        UpdateDetail();
        _layout.SetSprites(_snapshot.Sprites, _selected);
        LayoutView.Invalidate();
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
        _layout.SetSprites(_snapshot.Sprites, _selected);
        LayoutView.Invalidate();
    }

    /// <summary>Zoom / grid / expansion / backdrop, applied to the large preview.</summary>
    private void ApplyViewOptions()
    {
        var sprite = _snapshot[_selected];
        bool expand = ExpandBox.IsChecked;
        Preview.Zoom = ZoomLevels[Math.Clamp(ZoomBox.SelectedIndex, 0, ZoomLevels.Length - 1)];
        Preview.ShowGrid = GridBox.IsChecked;
        Preview.BackgroundColor = BackgroundBox.IsChecked ? _snapshot.BackgroundColor : -1;
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
          .Append(" (").Append(C64Palette.Names[s.Color & 15]).Append(")\n");
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

    private async void SavePng_Clicked(object? sender, EventArgs e)
    {
        var s = _snapshot[_selected];
        bool expandX = ExpandBox.IsChecked && s.ExpandX;
        bool expandY = ExpandBox.IsChecked && s.ExpandY;
        int scaleX = expandX ? 2 : 1, scaleY = expandY ? 2 : 1;
        int width = SpriteSnapshot.Width * scaleX, height = SpriteSnapshot.Height * scaleY;
        var path = await FileDialogs.SaveAsync("PNG image", ".png", $"sprite{_selected}-{s.CpuDataAddress:X4}.png");
        if (path is null) return;
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
            PngWriter.Save(path, width, height, pixels);
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Cannot save the sprite", ex.Message, "OK");
        }
    }

    /// <summary>One entry of the sprite list: a 24 x 21 thumbnail plus a two line summary.</summary>
    private sealed class SpriteListItem
    {
        private static readonly Color SelectionEdgeLight = Color.FromRgb(0x30, 0x70, 0xC0);
        private static readonly Color SelectionEdgeDark = Color.FromRgb(0x5A, 0x9B, 0xE0);
        private static readonly Color SelectionFillLight = Color.FromRgb(0xDD, 0xE8, 0xF8);
        private static readonly Color SelectionFillDark = Color.FromRgb(0x26, 0x36, 0x4C);

        private readonly SpriteThumbnailDrawable _drawable = new();
        private readonly GraphicsView _thumbnail;
        private readonly Label _summary;
        private readonly Border _border;

        public SpriteListItem(int index, Action<int> select)
        {
            _thumbnail = new GraphicsView { Drawable = _drawable, WidthRequest = 48, HeightRequest = 42 };
            _summary = new Label { FontFamily = "Consolas", FontSize = 11 };
            _summary.SetAppThemeColor(Label.TextColorProperty, Color.FromRgb(0x50, 0x50, 0x50), Color.FromRgb(0xA8, 0xA8, 0xA8));
            var title = new Label { Text = "Sprite " + index, FontFamily = "Consolas", FontSize = 12, FontAttributes = FontAttributes.Bold };

            var grid = new Grid
            {
                ColumnDefinitions = { new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star) },
                ColumnSpacing = 6,
            };
            var frame = new Border { StrokeThickness = 1, Padding = 0, VerticalOptions = LayoutOptions.Start, Content = _thumbnail };
            frame.SetAppTheme<Brush>(Border.StrokeProperty,
                new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80)), new SolidColorBrush(Color.FromRgb(0x60, 0x60, 0x60)));
            grid.Add(frame, 0, 0);
            grid.Add(new VerticalStackLayout { Children = { title, _summary } }, 1, 0);

            _border = new Border
            {
                Stroke = Brush.Transparent,
                StrokeThickness = 2,
                Padding = 3,
                BackgroundColor = Colors.Transparent,
                Content = grid,
            };
            var tap = new TapGestureRecognizer();
            tap.Tapped += (_, _) => select(index);
            _border.GestureRecognizers.Add(tap);
        }

        public View View => _border;

        public void SetSelected(bool selected)
        {
            if (selected)
            {
                _border.SetAppTheme<Brush>(Border.StrokeProperty,
                    new SolidColorBrush(SelectionEdgeLight), new SolidColorBrush(SelectionEdgeDark));
                _border.SetAppThemeColor(VisualElement.BackgroundColorProperty, SelectionFillLight, SelectionFillDark);
            }
            else
            {
                _border.Stroke = Brush.Transparent;
                _border.BackgroundColor = Colors.Transparent;
            }
        }

        public void Update(SpriteInfo sprite, byte[] pixels)
        {
            _drawable.SetSprite(pixels);
            _thumbnail.Invalidate();

            var flags = new List<string>(6) { sprite.Enabled ? "on" : "off" };
            if (sprite.Multicolor) flags.Add("MC");
            if (sprite.ExpandX) flags.Add("2X");
            if (sprite.ExpandY) flags.Add("2Y");
            if (sprite.BehindForeground) flags.Add("behind");
            if (sprite.DmaActive) flags.Add("DMA");
            if (sprite.SpriteCollision || sprite.DataCollision) flags.Add("hit");
            var text = $"X {sprite.X,3} Y {sprite.Y,3}  col {sprite.Color}\n" +
                       $"ptr ${sprite.Pointer:X2} -> ${sprite.CpuDataAddress:X4}\n" +
                       string.Join(" ", flags);
            if (_summary.Text != text) _summary.Text = text;
        }
    }
}
