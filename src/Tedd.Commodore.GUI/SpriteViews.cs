using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Tedd.Commodore.Emulator.Tools;
using Tedd.Commodore.Emulator.Video;

namespace Tedd.Commodore.GUI;

/// <summary>Shared brushes/pens and palette conversion for the sprite viewer.</summary>
internal static class SpriteBrushes
{
    /// <summary>The 16 C64 colors as WPF brushes, in <see cref="VicII.Palette"/> order.</summary>
    public static readonly Brush[] Palette = CreatePalette(0xFF);

    /// <summary>The same colors at half opacity, for the sprite boxes of the layout view.</summary>
    public static readonly Brush[] TranslucentPalette = CreatePalette(0x80);

    /// <summary>One pen per C64 color.</summary>
    public static readonly Pen[] PalettePens = CreatePens();

    public static readonly Brush CheckerDark = Freeze(new SolidColorBrush(Color.FromRgb(0x24, 0x24, 0x24)));
    public static readonly Brush CheckerLight = Freeze(new SolidColorBrush(Color.FromRgb(0x30, 0x30, 0x30)));
    public static readonly Pen PixelGridPen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)), 1));
    public static readonly Pen ByteGridPen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0x90, 0xFF, 0xFF, 0xFF)), 1));

    public static Color ToColor(uint argb) =>
        Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    public static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    private static Brush[] CreatePalette(byte alpha)
    {
        var brushes = new Brush[VicII.Palette.Length];
        for (int i = 0; i < brushes.Length; i++)
        {
            var c = ToColor(VicII.Palette[i]);
            brushes[i] = Freeze(new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B)));
        }
        return brushes;
    }

    private static Pen[] CreatePens()
    {
        var pens = new Pen[VicII.Palette.Length];
        for (int i = 0; i < pens.Length; i++)
            pens[i] = Freeze(new Pen(Palette[i], 1));
        return pens;
    }

    /// <summary>Fills <paramref name="rect"/> with a checkerboard marking the transparent areas.</summary>
    public static void DrawChecker(DrawingContext dc, Rect rect, double cell = 8)
    {
        dc.DrawRectangle(CheckerDark, null, rect);
        dc.PushClip(new RectangleGeometry(rect));
        for (double y = rect.Y, row = 0; y < rect.Bottom; y += cell, row++)
            for (double x = rect.X + (row % 2 == 0 ? 0 : cell); x < rect.Right; x += 2 * cell)
                dc.DrawRectangle(CheckerLight, null, new Rect(x, y, Math.Min(cell, rect.Right - x), Math.Min(cell, rect.Bottom - y)));
        dc.Pop();
    }
}

/// <summary>
/// Draws one decoded sprite (24 x 21 color indices, <see cref="SpriteSnapshot.Transparent"/> where nothing is
/// drawn) as a zoomable pixel grid. X/Y expansion is applied by doubling the cell size, so the shape has the
/// same proportions as on screen; the grid marks every pixel and, more strongly, the three byte columns of
/// each row.
/// </summary>
public sealed class SpriteView : FrameworkElement
{
    private byte[] _pixels = Array.Empty<byte>();
    private bool _expandX, _expandY;
    private double _zoom = 8;
    private bool _showGrid = true;
    private int _background = -1;

    /// <summary>Size of one sprite pixel in device independent units.</summary>
    public double Zoom
    {
        get => _zoom;
        set { _zoom = Math.Max(1, value); InvalidateMeasure(); InvalidateVisual(); }
    }

    /// <summary>Draws the pixel/byte grid (only visible from about 4 units per pixel).</summary>
    public bool ShowGrid
    {
        get => _showGrid;
        set { _showGrid = value; InvalidateVisual(); }
    }

    /// <summary>Color index drawn behind the sprite, or -1 for a checkerboard marking transparency.</summary>
    public int BackgroundColor
    {
        get => _background;
        set { _background = value; InvalidateVisual(); }
    }

    /// <summary>Shows <paramref name="pixels"/> (the array is read while rendering, so it must stay 24 x 21).</summary>
    public void SetSprite(byte[] pixels, bool expandX, bool expandY)
    {
        _pixels = pixels;
        _expandX = expandX;
        _expandY = expandY;
        InvalidateMeasure();
        InvalidateVisual();
    }

    private double CellWidth => _zoom * (_expandX ? 2 : 1);
    private double CellHeight => _zoom * (_expandY ? 2 : 1);

    protected override Size MeasureOverride(Size availableSize) =>
        new(SpriteSnapshot.Width * CellWidth, SpriteSnapshot.Height * CellHeight);

    protected override void OnRender(DrawingContext dc)
    {
        double cw = CellWidth, ch = CellHeight;
        var area = new Rect(0, 0, SpriteSnapshot.Width * cw, SpriteSnapshot.Height * ch);
        if (_background >= 0)
            dc.DrawRectangle(SpriteBrushes.Palette[_background & 15], null, area);
        else
            SpriteBrushes.DrawChecker(dc, area);
        if (_pixels.Length < SpriteSnapshot.Width * SpriteSnapshot.Height)
            return;

        // One filled geometry per color instead of one rectangle per pixel (at most 4 draw calls per sprite),
        // with horizontal runs of the same color merged into a single rectangle.
        var geometries = new Dictionary<byte, (StreamGeometry Geometry, StreamGeometryContext Context)>(4);
        for (int y = 0; y < SpriteSnapshot.Height; y++)
        {
            for (int x = 0; x < SpriteSnapshot.Width; x++)
            {
                byte c = _pixels[y * SpriteSnapshot.Width + x];
                if (c == SpriteSnapshot.Transparent)
                    continue;
                if (!geometries.TryGetValue(c, out var g))
                {
                    var geometry = new StreamGeometry();
                    g = (geometry, geometry.Open());
                    geometries[c] = g;
                }
                int runEnd = x;
                while (runEnd + 1 < SpriteSnapshot.Width && _pixels[y * SpriteSnapshot.Width + runEnd + 1] == c)
                    runEnd++;
                AddRectangle(g.Context, new Rect(x * cw, y * ch, (runEnd - x + 1) * cw, ch));
                x = runEnd;
            }
        }
        foreach (var (color, g) in geometries)
        {
            g.Context.Close();
            g.Geometry.Freeze();
            dc.DrawGeometry(SpriteBrushes.Palette[color & 15], null, g.Geometry);
        }

        if (!_showGrid || cw < 4)
            return;
        for (int x = 0; x <= SpriteSnapshot.Width; x++)
            dc.DrawLine(x % 8 == 0 ? SpriteBrushes.ByteGridPen : SpriteBrushes.PixelGridPen,
                new Point(x * cw, 0), new Point(x * cw, area.Height));
        for (int y = 0; y <= SpriteSnapshot.Height; y++)
            dc.DrawLine(SpriteBrushes.PixelGridPen, new Point(0, y * ch), new Point(area.Width, y * ch));
    }

    private static void AddRectangle(StreamGeometryContext ctx, Rect r)
    {
        ctx.BeginFigure(r.TopLeft, true, true);
        ctx.LineTo(r.TopRight, false, false);
        ctx.LineTo(r.BottomRight, false, false);
        ctx.LineTo(r.BottomLeft, false, false);
    }
}

/// <summary>
/// Where the sprites sit relative to the display window: the 320 x 200 display area inside the visible border
/// area, with one labelled box per enabled sprite at its MxX/MxY position and expanded size. Sprites that are
/// (partly) outside the visible area are visibly clipped, which is usually the reason one cannot be seen.
/// </summary>
public sealed class SpriteLayoutView : FrameworkElement
{
    // The visible PAL picture in sprite coordinates: X $1E..$15E, Y 16..300 (3.9 plus the blanking margins).
    private const int VisibleLeft = 0x1E - 24, VisibleTop = 16 - 50, VisibleWidth = 0x158 - 0x18 + 40, VisibleHeight = 284;

    private static readonly Brush BackBrush = SpriteBrushes.Freeze(new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x14)));
    private static readonly Pen WindowPen = SpriteBrushes.Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x60, 0x60, 0x60)), 1));
    private static readonly Pen SelectedPen = SpriteBrushes.Freeze(new Pen(Brushes.White, 2));
    private static readonly Typeface LabelFace = new("Consolas");

    private IReadOnlyList<SpriteInfo> _sprites = Array.Empty<SpriteInfo>();
    private int _selected = -1;

    /// <summary>Shows these sprites; call again (or <see cref="UIElement.InvalidateVisual"/>) after an update.</summary>
    public void SetSprites(IReadOnlyList<SpriteInfo> sprites, int selected)
    {
        _sprites = sprites;
        _selected = selected;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        dc.DrawRectangle(BackBrush, null, new Rect(0, 0, w, h));
        double scale = Math.Min(w / VisibleWidth, h / VisibleHeight);
        double ox = (w - VisibleWidth * scale) / 2, oy = (h - VisibleHeight * scale) / 2;
        Point Map(double x, double y) => new(ox + (x - VisibleLeft) * scale, oy + (y - VisibleTop) * scale);

        // The 40 x 25 character display window (sprite coordinates 0,0 .. 320,200).
        var topLeft = Map(0, 0);
        var bottomRight = Map(320, 200);
        dc.DrawRectangle(SpriteBrushes.Palette[11], WindowPen, new Rect(topLeft, bottomRight));
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, w, h)));
        foreach (var s in _sprites)
        {
            if (!s.Enabled) continue;
            var p0 = Map(s.DisplayX, s.DisplayY);
            var rect = new Rect(p0.X, p0.Y, s.PixelWidth * scale, s.PixelHeight * scale);
            var pen = s.Index == _selected ? SelectedPen : SpriteBrushes.PalettePens[s.Color & 15];
            dc.DrawRectangle(SpriteBrushes.TranslucentPalette[s.Color & 15], pen, rect);
            var text = new FormattedText(s.Index.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, LabelFace, 11, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(text, new Point(rect.X + 2, rect.Y + 1));
        }
        dc.Pop();
    }
}
