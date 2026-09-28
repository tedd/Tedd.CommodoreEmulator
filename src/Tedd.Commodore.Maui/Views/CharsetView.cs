using Tedd.Commodore.Emulator.Tools;

namespace Tedd.Commodore.Maui.Views;

/// <summary>
/// Draws a whole character set as a grid of 8 x 8 glyphs in two C64 colors, with a hex gutter showing the first
/// character code of each row.
///
/// It is two stacked layers: the glyphs themselves, decoded into a <see cref="PixelSurface"/> at one bitmap pixel
/// per character pixel and magnified by the GPU the same way the emulator picture is; and a transparent overlay
/// canvas with the gutter, grid, hover box and selection box, which is redrawn whenever the pointer moves. So a
/// 512 character ROM image costs 32768 pixel writes when the characters or colors change and nothing at all when
/// only the zoom does - the CPU never draws a magnified glyph, whatever the zoom.
/// </summary>
public sealed class CharsetView : ContentView, IDisposable
{
    private const int Size = CharacterSet.CharacterWidth;   // glyphs are square

    private readonly PixelSurface _glyphs = new();
    private readonly OverlayLayer _overlay;
    private readonly View _glyphView;
    private readonly GraphicsView _overlayView;

    private CharacterSet? _source;
    private int _columns = 32;
    private int _zoom = 4;
    private int _ink = 14;      // light blue on blue: the C64's own text colors
    private int _paper = 6;
    private bool _showGrid = true;
    private int _selectedIndex;
    private int _hoverIndex = -1;
    private double _labelSize = 11;

    public CharsetView()
    {
        _overlay = new OverlayLayer(this);
        _glyphView = _glyphs.View;
        _glyphView.HorizontalOptions = LayoutOptions.Start;
        _glyphView.VerticalOptions = LayoutOptions.Start;
        _overlayView = new GraphicsView { Drawable = _overlay, BackgroundColor = Microsoft.Maui.Graphics.Colors.Transparent };

        var pointer = new PointerGestureRecognizer();
        pointer.PointerMoved += (_, e) => SetHover(IndexAt(e.GetPosition(_overlayView)));
        pointer.PointerExited += (_, _) => SetHover(-1);
        _overlayView.GestureRecognizers.Add(pointer);

        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, e) =>
        {
            int index = IndexAt(e.GetPosition(_overlayView));
            if (index >= 0) SelectedIndex = index;
        };
        _overlayView.GestureRecognizers.Add(tap);

        Content = new Grid { Children = { _glyphView, _overlayView } };
    }

    /// <summary>Raised when <see cref="SelectedIndex"/> changes, whatever changed it.</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>Raised when the character under the pointer changes; the argument is -1 when it leaves.</summary>
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

    /// <summary>Screen pixels per character pixel. The GPU does the magnifying, so this only resizes.</summary>
    public int Zoom
    {
        get => _zoom;
        set { _zoom = Math.Clamp(value, 1, 24); Resize(); }
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

    /// <summary>Draws separator lines between the characters.</summary>
    public bool ShowGrid
    {
        get => _showGrid;
        set { _showGrid = value; _overlayView.Invalidate(); }
    }

    /// <summary>The selected character, or 0 when the set is empty.</summary>
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

    /// <summary>Size of one character on screen.</summary>
    private double Cell => CharacterSet.CharacterWidth * _zoom;

    /// <summary>Width of the hex label column on the left.</summary>
    private double Gutter => Math.Ceiling(_labelSize * 2.6 + 8);

    /// <summary>Moves the selection the way the arrow keys do; returns true when it moved.</summary>
    public bool MoveSelection(string code)
    {
        if (_source is null) return false;
        int last = _source.Count - 1;
        int index = code switch
        {
            "ArrowLeft" => _selectedIndex - 1,
            "ArrowRight" => _selectedIndex + 1,
            "ArrowUp" => _selectedIndex - _columns,
            "ArrowDown" => _selectedIndex + _columns,
            "PageUp" => _selectedIndex - _columns * 8,
            "PageDown" => _selectedIndex + _columns * 8,
            "Home" => 0,
            "End" => last,
            _ => _selectedIndex,
        };
        if (index == _selectedIndex) return false;
        SelectedIndex = Math.Clamp(index, 0, last);
        return true;
    }

    /// <summary>Redraws everything from the source; call after the data behind a live set changed.</summary>
    public void Rebuild()
    {
        Resize();
        PaintGlyphs();
    }

    /// <summary>Lays the two layers out for the current zoom, without touching a single glyph pixel.</summary>
    private void Resize()
    {
        double cell = Cell;
        double width = _columns * cell, height = Math.Max(1, Rows * cell);
        WidthRequest = Gutter + width;
        HeightRequest = height;
        // The bitmap covers the glyphs only; the overlay spans the gutter as well and draws the labels in it.
        _glyphView.Margin = new Thickness(Gutter, 0, 0, 0);
        _glyphView.WidthRequest = width;
        _glyphView.HeightRequest = height;
        _overlayView.Invalidate();
    }

    /// <summary>Decodes the character set into the surface, one bitmap pixel per character pixel.</summary>
    private void PaintGlyphs()
    {
        var source = _source;
        if (source is null || source.Count == 0)
        {
            _glyphView.IsVisible = false;
            return;
        }

        int columns = _columns;
        uint ink = C64Palette.NativeOf(_ink);
        uint paper = C64Palette.NativeOf(_paper);
        _glyphs.Paint(columns * Size, Rows * Size, (pixels, stride) =>
        {
            pixels.Fill(paper);
            for (int index = 0; index < source.Count; index++)
            {
                var glyph = source.Glyph(index);
                int left = index % columns * Size;
                int top = index / columns * Size;
                for (int row = 0; row < Size; row++)
                {
                    int bits = glyph[row];
                    if (bits == 0) continue;    // the paper fill above already covers an empty row
                    var line = pixels.Slice((top + row) * stride + left, Size);
                    for (int x = 0; x < Size; x++)
                        if (((bits >> (7 - x)) & 1) != 0) line[x] = ink;
                }
            }
        });
    }

    /// <summary>Releases the native buffers behind the glyph picture.</summary>
    public void Dispose() => _glyphs.Dispose();

    /// <summary>The character at a point in this view's coordinates, or -1 when there is none.</summary>
    public int IndexAt(Point? point)
    {
        if (_source is null || point is not { } p) return -1;
        double x = p.X - Gutter, y = p.Y;
        if (x < 0 || y < 0) return -1;
        int column = (int)(x / Cell), row = (int)(y / Cell);
        if (column >= _columns || row >= Rows) return -1;
        int index = row * _columns + column;
        return index < _source.Count ? index : -1;
    }

    private void SetSelected(int index)
    {
        _selectedIndex = index;
        _overlayView.Invalidate();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetHover(int index)
    {
        if (index == _hoverIndex) return;
        _hoverIndex = index;
        _overlayView.Invalidate();
        HoverChanged?.Invoke(this, index);
    }

    /// <summary>Gutter labels, grid, set divider, hover and selection: cheap, and redrawn on every pointer move.</summary>
    private sealed class OverlayLayer : IDrawable
    {
        private static readonly Color GridColor = Color.FromRgba(0xFF, 0xFF, 0xFF, 0x50);
        private static readonly Color HoverColor = Color.FromRgba(0xFF, 0xFF, 0xFF, 0xA0);
        private static readonly Color DividerColor = Color.FromRgba(0xFF, 0xFF, 0xFF, 0xC0);
        private static readonly Color GutterColor = Color.FromRgb(0x88, 0x88, 0x88);

        private readonly CharsetView _owner;

        public OverlayLayer(CharsetView owner) => _owner = owner;

        public void Draw(ICanvas canvas, RectF dirtyRect)
        {
            var source = _owner._source;
            if (source is null || source.Count == 0) return;
            float cell = (float)_owner.Cell;
            float gutter = (float)_owner.Gutter;
            int columns = _owner._columns;
            int rows = _owner.Rows;
            float width = columns * cell, height = rows * cell;

            canvas.StrokeSize = 1;
            if (_owner._showGrid && _owner._zoom >= 3)
            {
                canvas.StrokeColor = GridColor;
                for (int c = 0; c <= columns; c++)
                    canvas.DrawLine(gutter + c * cell, 0, gutter + c * cell, height);
                for (int r = 0; r <= rows; r++)
                    canvas.DrawLine(gutter, r * cell, gutter + width, r * cell);
            }

            // A brighter rule where one 256 character set ends and the next begins.
            canvas.StrokeColor = DividerColor;
            canvas.StrokeSize = 2;
            for (int c = CharacterSet.CharactersPerSet; c < source.Count; c += CharacterSet.CharactersPerSet)
            {
                float y = c / columns * cell;
                canvas.DrawLine(gutter, y, gutter + width, y);
            }

            // Labelled by screen code, so the second set of a ROM image starts over at $00.
            canvas.FontColor = GutterColor;
            canvas.FontSize = (float)_owner._labelSize;
            for (int r = 0; r < rows; r++)
                canvas.DrawString($"${r * columns & 0xFF:X2}", 0, r * cell, gutter - 6, cell,
                    HorizontalAlignment.Right, VerticalAlignment.Center);

            int hover = _owner._hoverIndex, selected = _owner._selectedIndex;
            canvas.StrokeSize = 1;
            if (hover >= 0 && hover < source.Count && hover != selected)
            {
                canvas.StrokeColor = HoverColor;
                canvas.DrawRectangle(CellRect(hover, 0.5f, cell, gutter, columns));
            }
            if (selected < source.Count)
            {
                var rect = CellRect(selected, 1f, cell, gutter, columns);
                canvas.StrokeColor = Microsoft.Maui.Graphics.Colors.Black;
                canvas.StrokeSize = 4;
                canvas.DrawRectangle(rect);
                canvas.StrokeColor = Microsoft.Maui.Graphics.Colors.White;
                canvas.StrokeSize = 2;
                canvas.DrawRectangle(rect);
            }
        }

        private static RectF CellRect(int index, float inset, float cell, float gutter, int columns) =>
            new(gutter + index % columns * cell + inset, index / columns * cell + inset, cell - 2 * inset, cell - 2 * inset);
    }
}

/// <summary>
/// One character blown up to fill the view, with a pixel grid: the bit pattern of the 8 bytes behind a
/// character, which is what you actually edit when designing a character set. The 8 x 8 pixels are a
/// <see cref="PixelSurface"/> like every other C64 picture; the grid and the border are drawn over it.
/// </summary>
public sealed class GlyphView : ContentView, IDisposable
{
    private const int Size = CharacterSet.CharacterWidth;   // glyphs are square

    private readonly PixelSurface _surface = new();
    private readonly GraphicsView _overlayView;

    private readonly byte[] _rows = new byte[CharacterSet.CharacterHeight];
    private int _ink = 14;
    private int _paper = 6;

    public GlyphView()
    {
        _overlayView = new GraphicsView
        {
            Drawable = new GridLayer(),
            BackgroundColor = Microsoft.Maui.Graphics.Colors.Transparent,
            InputTransparent = true,
        };
        Content = new Grid { Children = { _surface.View, _overlayView } };
    }

    /// <summary>Shows the 8 rows of a character in the given C64 colors.</summary>
    public void SetGlyph(ReadOnlySpan<byte> glyph, int ink, int paper)
    {
        Array.Clear(_rows);
        glyph.Slice(0, Math.Min(glyph.Length, _rows.Length)).CopyTo(_rows);
        _ink = ink & 0x0F;
        _paper = paper & 0x0F;
        Paint();
    }

    /// <summary>Clears the view (no character selected).</summary>
    public void Clear()
    {
        Array.Clear(_rows);
        Paint();
    }

    public void Dispose() => _surface.Dispose();

    private void Paint()
    {
        uint ink = C64Palette.NativeOf(_ink);
        uint paper = C64Palette.NativeOf(_paper);
        var rows = _rows;
        _surface.Paint(Size, CharacterSet.CharacterHeight, (pixels, stride) =>
        {
            for (int row = 0; row < CharacterSet.CharacterHeight; row++)
            {
                var line = pixels.Slice(row * stride, Size);
                for (int x = 0; x < Size; x++)
                    line[x] = ((rows[row] >> (7 - x)) & 1) != 0 ? ink : paper;
            }
        });
    }

    /// <summary>The pixel grid and the border: screen pixels, so they stay on a canvas over the picture.</summary>
    private sealed class GridLayer : IDrawable
    {
        private static readonly Color GridColor = Color.FromRgba(0xFF, 0xFF, 0xFF, 0x60);
        private static readonly Color BorderColor = Color.FromRgb(0x80, 0x80, 0x80);

        public void Draw(ICanvas canvas, RectF dirtyRect)
        {
            float side = Math.Min(dirtyRect.Width, dirtyRect.Height);
            if (side < Size) return;
            float pixel = side / Size;

            canvas.StrokeSize = 1;
            canvas.StrokeColor = GridColor;
            for (int i = 1; i < Size; i++)
            {
                canvas.DrawLine(i * pixel, 0, i * pixel, side);
                canvas.DrawLine(0, i * pixel, side, i * pixel);
            }
            canvas.StrokeColor = BorderColor;
            canvas.DrawRectangle(0.5f, 0.5f, side - 1, side - 1);
        }
    }
}
