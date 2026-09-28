using System;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Tedd.Commodore.Emulator.Tools;
using Tedd.Commodore.Emulator.Video;
// Tedd.WriteableBitmap puts a WriteableBitmap in the enclosing Tedd namespace, which wins over any
// same-named alias here. These views want WPF's; only the emulator screen trades it for the faster one.
using WpfBitmap = System.Windows.Media.Imaging.WriteableBitmap;

namespace Tedd.Commodore.GUI;

/// <summary>
/// Draws a whole character set as a grid of 8 x 8 glyphs in two C64 colors, with a hex gutter showing the first
/// character code of each row. The glyphs are decoded once into a <see cref="WpfBitmap"/> at one bitmap
/// pixel per character pixel and scaled up with nearest neighbour sampling, so zooming stays crisp and cheap
/// even for the 512 characters of a full ROM image. Hovering and clicking report character indices.
/// </summary>
public sealed class CharsetView : FrameworkElement
{
    private const int Size = CharacterSet.CharacterWidth;   // glyphs are square

    private static readonly Pen GridPen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF)), 1));
    private static readonly Pen SelectionPen = Freeze(new Pen(Brushes.White, 2));
    private static readonly Pen SelectionShadowPen = Freeze(new Pen(Brushes.Black, 4));
    private static readonly Pen HoverPen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0xA0, 0xFF, 0xFF, 0xFF)), 1));
    private static readonly Pen SetDividerPen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF)), 2));
    private static readonly Brush GutterBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)));
    private static readonly Typeface GutterTypeface = new("Consolas");

    private CharacterSet? _source;
    private WpfBitmap? _bitmap;
    private int[] _pixels = Array.Empty<int>();
    private int _columns = 32;
    private int _zoom = 4;
    private int _ink = 14;      // light blue on blue: the C64's own text colors
    private int _paper = 6;
    private bool _showGrid = true;
    private int _selectedIndex;
    private int _hoverIndex = -1;
    private double _labelSize = 11;
    private double _gutter = 34;

    public CharsetView()
    {
        Focusable = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
    }

    /// <summary>Raised when <see cref="SelectedIndex"/> changes, whatever changed it.</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>Raised when the character under the mouse changes; the argument is -1 when the mouse leaves.</summary>
    public event EventHandler<int>? HoverChanged;

    /// <summary>The characters to draw. Setting it keeps the selection if it still exists.</summary>
    public CharacterSet? Source
    {
        get => _source;
        set
        {
            _source = value;
            if (_selectedIndex >= (value?.Count ?? 0)) SetSelected(0);
            Rebuild();
        }
    }

    /// <summary>Characters per row; 32 lays 256 characters out as $x0 columns and 8 rows.</summary>
    public int Columns
    {
        get => _columns;
        set { _columns = Math.Max(1, value); Rebuild(); }
    }

    /// <summary>Screen pixels per character pixel.</summary>
    public int Zoom
    {
        get => _zoom;
        set { _zoom = Math.Clamp(value, 1, 24); InvalidateMeasure(); InvalidateVisual(); }
    }

    /// <summary>C64 color index (0..15) for set bits.</summary>
    public int Ink
    {
        get => _ink;
        set { _ink = value & 0x0F; Rebuild(); }
    }

    /// <summary>C64 color index (0..15) for clear bits.</summary>
    public int Paper
    {
        get => _paper;
        set { _paper = value & 0x0F; Rebuild(); }
    }

    /// <summary>Font size of the hex labels in the gutter (this element draws its own text).</summary>
    public double LabelSize
    {
        get => _labelSize;
        set { _labelSize = Math.Max(6, value); InvalidateMeasure(); InvalidateVisual(); }
    }

    /// <summary>Draws separator lines between the characters.</summary>
    public bool ShowGrid
    {
        get => _showGrid;
        set { _showGrid = value; InvalidateVisual(); }
    }

    /// <summary>The selected character, or -1 when the set is empty.</summary>
    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            if (_source is null || value < 0 || value >= _source.Count || value == _selectedIndex) return;
            SetSelected(value);
        }
    }

    /// <summary>Number of rows the current set needs.</summary>
    public int Rows => _source is null ? 0 : (_source.Count + _columns - 1) / _columns;

    /// <summary>Size of one character on screen, including nothing else.</summary>
    private double Cell => Size * _zoom;

    private void SetSelected(int index)
    {
        _selectedIndex = index;
        InvalidateVisual();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Redraws the bitmap from the source; call after the data behind a live set changed.</summary>
    public void Rebuild()
    {
        var source = _source;
        if (source is null || source.Count == 0)
        {
            _bitmap = null;
            InvalidateMeasure();
            InvalidateVisual();
            return;
        }

        int width = _columns * Size;
        int height = Rows * Size;
        if (_bitmap is null || _bitmap.PixelWidth != width || _bitmap.PixelHeight != height)
        {
            _bitmap = new WpfBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
            _pixels = new int[width * height];
        }

        int ink = unchecked((int)VicII.Palette[_ink]);
        int paper = unchecked((int)VicII.Palette[_paper]);
        Array.Fill(_pixels, paper);
        for (int index = 0; index < source.Count; index++)
        {
            var glyph = source.Glyph(index);
            int left = index % _columns * Size;
            int top = index / _columns * Size;
            for (int row = 0; row < Size; row++)
            {
                int bits = glyph[row];
                int at = (top + row) * width + left;
                for (int x = 0; x < Size; x++)
                    _pixels[at + x] = ((bits >> (7 - x)) & 1) != 0 ? ink : paper;
            }
        }
        _bitmap.WritePixels(new Int32Rect(0, 0, width, height), _pixels, width * 4, 0);
        InvalidateMeasure();
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _gutter = Math.Ceiling(MeasureGutter());
        return _source is null ? new Size(_gutter, 0) : new Size(_gutter + _columns * Cell, Rows * Cell);
    }

    private double MeasureGutter() => _labelSize * 2.6 + 8;

    protected override void OnRender(DrawingContext dc)
    {
        double cell = Cell;
        double width = _columns * cell, height = Rows * cell;
        // A hit-testable background, otherwise the mouse only sees the drawn glyphs.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, _gutter + width, Math.Max(height, 1)));
        if (_bitmap is null || _source is null) return;

        dc.DrawImage(_bitmap, new Rect(_gutter, 0, width, height));

        if (_showGrid && _zoom >= 3)
        {
            for (int c = 0; c <= _columns; c++)
            {
                double x = Math.Round(_gutter + c * cell) + 0.5;
                dc.DrawLine(GridPen, new Point(x, 0), new Point(x, height));
            }
            for (int r = 0; r <= Rows; r++)
            {
                double y = Math.Round(r * cell) + 0.5;
                dc.DrawLine(GridPen, new Point(_gutter, y), new Point(_gutter + width, y));
            }
        }

        // A brighter rule where one 256 character set ends and the next begins.
        for (int c = CharacterSet.CharactersPerSet; c < _source.Count; c += CharacterSet.CharactersPerSet)
        {
            double y = Math.Round(c / _columns * cell);
            dc.DrawLine(SetDividerPen, new Point(_gutter, y), new Point(_gutter + width, y));
        }

        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        for (int r = 0; r < Rows; r++)
        {
            // Labelled by screen code, so the second set of a ROM image starts over at $00.
            var text = new FormattedText($"${r * _columns & 0xFF:X2}", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                GutterTypeface, _labelSize, GutterBrush, dpi);
            dc.DrawText(text, new Point(_gutter - text.Width - 6, r * cell + (cell - text.Height) / 2));
        }

        if (_hoverIndex >= 0 && _hoverIndex < _source.Count && _hoverIndex != _selectedIndex)
            dc.DrawRectangle(null, HoverPen, CellRect(_hoverIndex, 0.5));
        if (_selectedIndex < _source.Count)
        {
            var rect = CellRect(_selectedIndex, 1);
            dc.DrawRectangle(null, SelectionShadowPen, rect);
            dc.DrawRectangle(null, SelectionPen, rect);
        }
    }

    private Rect CellRect(int index, double inset)
    {
        double cell = Cell;
        return new Rect(_gutter + index % _columns * cell + inset, index / _columns * cell + inset,
            cell - 2 * inset, cell - 2 * inset);
    }

    /// <summary>The character at a point in this element's coordinates, or -1 when there is none.</summary>
    public int IndexAt(Point point)
    {
        if (_source is null) return -1;
        double x = point.X - _gutter, y = point.Y;
        if (x < 0 || y < 0) return -1;
        int column = (int)(x / Cell), row = (int)(y / Cell);
        if (column >= _columns || row >= Rows) return -1;
        int index = row * _columns + column;
        return index < _source.Count ? index : -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        SetHover(IndexAt(e.GetPosition(this)));
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        SetHover(-1);
    }

    private void SetHover(int index)
    {
        if (index == _hoverIndex) return;
        _hoverIndex = index;
        InvalidateVisual();
        HoverChanged?.Invoke(this, index);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        int index = IndexAt(e.GetPosition(this));
        if (index >= 0) SelectedIndex = index;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_source is null) return;
        int last = _source.Count - 1;
        int index = e.Key switch
        {
            Key.Left => _selectedIndex - 1,
            Key.Right => _selectedIndex + 1,
            Key.Up => _selectedIndex - _columns,
            Key.Down => _selectedIndex + _columns,
            Key.PageUp => _selectedIndex - _columns * 8,
            Key.PageDown => _selectedIndex + _columns * 8,
            Key.Home => 0,
            Key.End => last,
            _ => _selectedIndex,
        };
        if (index == _selectedIndex) return;
        SelectedIndex = Math.Clamp(index, 0, last);
        e.Handled = true;
    }

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}

/// <summary>
/// One character blown up to fill the element, with a pixel grid: the bit pattern of the 8 bytes behind a
/// character, which is what you actually edit when designing a character set.
/// </summary>
public sealed class GlyphView : FrameworkElement
{
    private static readonly Pen GridPen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF)), 1));
    private static readonly Pen BorderPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80)), 1));

    private byte[] _rows = new byte[CharacterSet.CharacterHeight];
    private int _ink = 14;
    private int _paper = 6;

    public GlyphView()
    {
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
    }

    /// <summary>Shows the 8 rows of a character in the given C64 colors.</summary>
    public void SetGlyph(ReadOnlySpan<byte> glyph, int ink, int paper)
    {
        glyph.Slice(0, Math.Min(glyph.Length, _rows.Length)).CopyTo(_rows);
        _ink = ink & 0x0F;
        _paper = paper & 0x0F;
        InvalidateVisual();
    }

    /// <summary>Clears the view (no character selected).</summary>
    public void Clear()
    {
        Array.Clear(_rows, 0, _rows.Length);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double side = Math.Floor(Math.Min(ActualWidth, ActualHeight) / CharacterSet.CharacterWidth) * CharacterSet.CharacterWidth;
        if (side < CharacterSet.CharacterWidth) return;
        double pixel = side / CharacterSet.CharacterWidth;
        var ink = new SolidColorBrush(ToColor(VicII.Palette[_ink]));
        var paper = new SolidColorBrush(ToColor(VicII.Palette[_paper]));
        ink.Freeze();
        paper.Freeze();

        dc.DrawRectangle(paper, null, new Rect(0, 0, side, side));
        for (int row = 0; row < CharacterSet.CharacterHeight; row++)
            for (int x = 0; x < CharacterSet.CharacterWidth; x++)
                if (((_rows[row] >> (7 - x)) & 1) != 0)
                    dc.DrawRectangle(ink, null, new Rect(x * pixel, row * pixel, pixel, pixel));

        for (int i = 1; i < CharacterSet.CharacterWidth; i++)
        {
            double at = Math.Round(i * pixel) + 0.5;
            dc.DrawLine(GridPen, new Point(at, 0), new Point(at, side));
            dc.DrawLine(GridPen, new Point(0, at), new Point(side, at));
        }
        dc.DrawRectangle(null, BorderPen, new Rect(0.5, 0.5, side - 1, side - 1));
    }

    private static Color ToColor(uint argb) =>
        Color.FromRgb((byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
