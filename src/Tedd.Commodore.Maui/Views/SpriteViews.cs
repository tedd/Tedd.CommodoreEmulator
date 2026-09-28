using Tedd.Maui;
using Tedd.Commodore.Emulator.Tools;

namespace Tedd.Commodore.Maui.Views;

/// <summary>
/// The pixels of one decoded sprite (24 x 21 color indices, <see cref="SpriteSnapshot.Transparent"/> where
/// nothing is drawn) written into a <see cref="PixelSurface"/>: one bitmap pixel per sprite pixel, whatever the
/// zoom, because the GPU does the magnifying.
/// </summary>
internal static class SpritePixels
{
    /// <summary>The two greys of the checkerboard that marks the transparent pixels.</summary>
    private static readonly uint CheckerDark = WriteableBitmap.FromRgba(0x24, 0x24, 0x24, 0xFF);
    private static readonly uint CheckerLight = WriteableBitmap.FromRgba(0x30, 0x30, 0x30, 0xFF);

    /// <summary>True when <paramref name="pixels"/> holds a whole decoded sprite.</summary>
    public static bool IsComplete(byte[] pixels) => pixels.Length >= SpriteSnapshot.Width * SpriteSnapshot.Height;

    /// <summary>
    /// Writes the sprite, with <paramref name="background"/> (a C64 color index, or -1 for a checkerboard
    /// <paramref name="checker"/> sprite pixels wide) behind the transparent pixels.
    /// </summary>
    public static void Paint(Span<uint> pixels, int stride, byte[] sprite, int background, int checker)
    {
        uint back = background >= 0 ? C64Palette.NativeOf(background) : 0;
        for (int y = 0; y < SpriteSnapshot.Height; y++)
        {
            var row = pixels.Slice(y * stride, SpriteSnapshot.Width);
            for (int x = 0; x < SpriteSnapshot.Width; x++)
            {
                byte c = sprite[y * SpriteSnapshot.Width + x];
                row[x] = c != SpriteSnapshot.Transparent ? C64Palette.NativeOf(c)
                    : background >= 0 ? back
                    : ((x / checker + y / checker) & 1) == 0 ? CheckerDark : CheckerLight;
            }
        }
    }
}

/// <summary>
/// Shows one decoded sprite as a zoomable pixel grid: its pixels on a <see cref="PixelSurface"/> the GPU
/// magnifies, with the pixel and byte column grid on a canvas over them. X/Y expansion is applied by doubling
/// the cell size, so the shape has the same proportions as on screen; the grid marks every pixel and, more
/// strongly, the three byte columns of each row.
/// </summary>
public sealed class SpriteView : ContentView, IDisposable
{
    private readonly PixelSurface _surface = new();
    private readonly GridLayer _grid;
    private readonly GraphicsView _gridView;

    private byte[] _pixels = Array.Empty<byte>();
    private double _zoom = 8;
    private bool _showGrid = true;
    private int _background = -1;
    private bool _expandX, _expandY;

    public SpriteView()
    {
        _grid = new GridLayer(this);
        _gridView = new GraphicsView
        {
            Drawable = _grid,
            BackgroundColor = Microsoft.Maui.Graphics.Colors.Transparent,
            InputTransparent = true,
        };
        Content = new Grid { Children = { _surface.View, _gridView } };
    }

    /// <summary>Size of one sprite pixel, before expansion. The GPU scales, so this only resizes the view.</summary>
    public double Zoom
    {
        get => _zoom;
        set { _zoom = value; Resize(); }
    }

    /// <summary>Draws the pixel/byte grid (only visible from about 4 units per pixel).</summary>
    public bool ShowGrid
    {
        get => _showGrid;
        set { _showGrid = value; _gridView.Invalidate(); }
    }

    /// <summary>Color index drawn behind the sprite, or -1 for a checkerboard marking transparency.</summary>
    public int BackgroundColor
    {
        get => _background;
        set { _background = value; Paint(); }
    }

    public bool ExpandX => _expandX;

    public bool ExpandY => _expandY;

    public double CellWidth => _zoom * (_expandX ? 2 : 1);

    public double CellHeight => _zoom * (_expandY ? 2 : 1);

    /// <summary>Shows <paramref name="pixels"/> (the array is read while painting, so it must stay 24 x 21).</summary>
    public void SetSprite(byte[] pixels, bool expandX, bool expandY)
    {
        _pixels = pixels;
        _expandX = expandX;
        _expandY = expandY;
        Resize();
        Paint();
    }

    public void Dispose() => _surface.Dispose();

    /// <summary>Sizes both layers to the sprite at the current zoom and expansion.</summary>
    private void Resize()
    {
        WidthRequest = SpriteSnapshot.Width * CellWidth;
        HeightRequest = SpriteSnapshot.Height * CellHeight;
        _gridView.Invalidate();
    }

    private void Paint()
    {
        var sprite = _pixels;
        if (!SpritePixels.IsComplete(sprite)) return;
        int background = _background;
        _surface.Paint(SpriteSnapshot.Width, SpriteSnapshot.Height,
            (pixels, stride) => SpritePixels.Paint(pixels, stride, sprite, background, 1));
    }

    /// <summary>The pixel and byte column grid: screen pixels, so it stays on a canvas over the picture.</summary>
    private sealed class GridLayer : IDrawable
    {
        private static readonly Color PixelGrid = Color.FromRgba(0xFF, 0xFF, 0xFF, 0x40);
        private static readonly Color ByteGrid = Color.FromRgba(0xFF, 0xFF, 0xFF, 0x90);

        private readonly SpriteView _owner;

        public GridLayer(SpriteView owner) => _owner = owner;

        public void Draw(ICanvas canvas, RectF dirtyRect)
        {
            float cw = (float)_owner.CellWidth, ch = (float)_owner.CellHeight;
            if (!_owner._showGrid || cw < 4) return;
            float width = SpriteSnapshot.Width * cw, height = SpriteSnapshot.Height * ch;

            canvas.StrokeSize = 1;
            for (int x = 0; x <= SpriteSnapshot.Width; x++)
            {
                canvas.StrokeColor = x % 8 == 0 ? ByteGrid : PixelGrid;
                canvas.DrawLine(x * cw, 0, x * cw, height);
            }
            canvas.StrokeColor = PixelGrid;
            for (int y = 0; y <= SpriteSnapshot.Height; y++)
                canvas.DrawLine(0, y * ch, width, y * ch);
        }
    }
}

/// <summary>
/// A 24 x 21 sprite at two screen pixels per sprite pixel, for the list on the left. This one stays on the
/// vector canvas rather than becoming a <see cref="PixelSurface"/> like the big preview above: there are eight
/// of them and they are refreshed ten times a second, and a GPU surface asks the UI thread for a great deal
/// more work per redraw than a canvas does - eighty of those a second is enough on its own to leave the whole
/// window sluggish. A thumbnail is also never magnified, which is where the pixel path earns its keep.
/// </summary>
internal sealed class SpriteThumbnailDrawable : IDrawable
{
    private static readonly Color CheckerDark = Color.FromRgb(0x24, 0x24, 0x24);
    private static readonly Color CheckerLight = Color.FromRgb(0x30, 0x30, 0x30);

    private byte[] _pixels = Array.Empty<byte>();

    public void SetSprite(byte[] pixels) => _pixels = pixels;

    public void Draw(ICanvas canvas, RectF dirtyRect)
    {
        float cw = dirtyRect.Width / SpriteSnapshot.Width;
        float ch = dirtyRect.Height / SpriteSnapshot.Height;
        // Transparent pixels get a checkerboard so an empty sprite is not just a blank box.
        DrawChecker(canvas, dirtyRect.Width, dirtyRect.Height, cw * 3);
        if (!SpritePixels.IsComplete(_pixels)) return;
        for (int y = 0; y < SpriteSnapshot.Height; y++)
        {
            for (int x = 0; x < SpriteSnapshot.Width; x++)
            {
                byte c = _pixels[y * SpriteSnapshot.Width + x];
                if (c == SpriteSnapshot.Transparent) continue;
                int runEnd = x;
                while (runEnd + 1 < SpriteSnapshot.Width && _pixels[y * SpriteSnapshot.Width + runEnd + 1] == c)
                    runEnd++;
                canvas.FillColor = C64Palette.Of(c);
                canvas.FillRectangle(x * cw, y * ch, (runEnd - x + 1) * cw, ch);
                x = runEnd;
            }
        }
    }

    private static void DrawChecker(ICanvas canvas, float width, float height, float cell)
    {
        canvas.FillColor = CheckerDark;
        canvas.FillRectangle(0, 0, width, height);
        canvas.FillColor = CheckerLight;
        for (float y = 0, row = 0; y < height; y += cell, row++)
            for (float x = row % 2 == 0 ? 0 : cell; x < width; x += 2 * cell)
                canvas.FillRectangle(x, y, Math.Min(cell, width - x), Math.Min(cell, height - y));
    }
}

/// <summary>
/// Where the sprites sit relative to the display window: the 320 x 200 display area inside the visible border
/// area, with one labelled box per enabled sprite at its MxX/MxY position and expanded size. Sprites that are
/// (partly) outside the visible area are visibly clipped, which is usually the reason one cannot be seen.
/// </summary>
internal sealed class SpriteLayoutDrawable : IDrawable
{
    // The visible PAL picture in sprite coordinates: X $1E..$15E, Y 16..300 (3.9 plus the blanking margins).
    private const int VisibleLeft = 0x1E - 24, VisibleTop = 16 - 50, VisibleWidth = 0x158 - 0x18 + 40, VisibleHeight = 284;

    private static readonly Color Back = Color.FromRgb(0x14, 0x14, 0x14);
    private static readonly Color WindowEdge = Color.FromRgb(0x60, 0x60, 0x60);

    private IReadOnlyList<SpriteInfo> _sprites = Array.Empty<SpriteInfo>();
    private int _selected = -1;

    /// <summary>Shows these sprites; invalidate the hosting view afterwards.</summary>
    public void SetSprites(IReadOnlyList<SpriteInfo> sprites, int selected)
    {
        _sprites = sprites;
        _selected = selected;
    }

    public void Draw(ICanvas canvas, RectF dirtyRect)
    {
        float w = dirtyRect.Width, h = dirtyRect.Height;
        if (w <= 0 || h <= 0) return;
        canvas.FillColor = Back;
        canvas.FillRectangle(0, 0, w, h);

        float scale = Math.Min(w / VisibleWidth, h / VisibleHeight);
        float ox = (w - VisibleWidth * scale) / 2, oy = (h - VisibleHeight * scale) / 2;
        PointF Map(float x, float y) => new(ox + (x - VisibleLeft) * scale, oy + (y - VisibleTop) * scale);

        // The 40 x 25 character display window (sprite coordinates 0,0 .. 320,200).
        var topLeft = Map(0, 0);
        var bottomRight = Map(320, 200);
        canvas.FillColor = C64Palette.Of(11);
        canvas.FillRectangle(topLeft.X, topLeft.Y, bottomRight.X - topLeft.X, bottomRight.Y - topLeft.Y);
        canvas.StrokeSize = 1;
        canvas.StrokeColor = WindowEdge;
        canvas.DrawRectangle(topLeft.X, topLeft.Y, bottomRight.X - topLeft.X, bottomRight.Y - topLeft.Y);

        canvas.SaveState();
        canvas.ClipRectangle(0, 0, w, h);
        canvas.FontSize = 11;
        foreach (var s in _sprites)
        {
            if (!s.Enabled) continue;
            var p0 = Map(s.DisplayX, s.DisplayY);
            var rect = new RectF(p0.X, p0.Y, s.PixelWidth * scale, s.PixelHeight * scale);
            canvas.FillColor = C64Palette.Translucent[s.Color & 15];
            canvas.FillRectangle(rect);
            canvas.StrokeColor = s.Index == _selected ? Microsoft.Maui.Graphics.Colors.White : C64Palette.Of(s.Color);
            canvas.StrokeSize = s.Index == _selected ? 2 : 1;
            canvas.DrawRectangle(rect);
            canvas.FontColor = Microsoft.Maui.Graphics.Colors.White;
            canvas.DrawString(s.Index.ToString(), rect.X + 2, rect.Y + 12, HorizontalAlignment.Left);
        }
        canvas.RestoreState();
    }
}
