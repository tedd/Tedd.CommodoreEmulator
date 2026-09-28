using System.Globalization;
using Tedd.Commodore.Emulator.Media;

namespace Tedd.Commodore.Maui.Views;

/// <summary>
/// Geometry of the hex grid, shared by the rows, the header and the hit testing so they agree to the pixel.
/// Everything derives from the advance width of one character of the monospaced font, measured on the first
/// canvas that draws; until then Consolas' nominal advance is used, which only matters for a tap before the
/// first paint.
/// </summary>
public static class HexMetrics
{
    public const string FontFamily = "Consolas";
    public const float FontSize = 11f;
    public const float RowHeight = 16f;
    public const float Inset = 6f;
    public const int Columns = 16;

    private static readonly IFont Font = new Microsoft.Maui.Graphics.Font(FontFamily);
    private static readonly Color[] ReadColors = BuildRamp(0x2E, 0xB8, 0x2E);
    private static readonly Color[] WriteColors = BuildRamp(0xFF, 0x8C, 0x00);
    private static float _charWidth = FontSize * 0.55f;
    private static bool _measured;

    public static float CharWidth => _charWidth;
    public static float AddressX => Inset;
    /// <summary>The digits start four characters and a two character gap after the address.</summary>
    public static float HexX => AddressX + 6 * _charWidth;
    /// <summary>Two digits and a space.</summary>
    public static float CellWidth => 3 * _charWidth;
    public static float TextX => HexX + Columns * CellWidth + _charWidth;
    public static float Width => TextX + Columns * _charWidth + Inset;

    /// <summary>Left edge of the digits of a column.</summary>
    public static float CellX(int column) => HexX + column * CellWidth;

    /// <summary>The highlight box of a column: its digits with half the gap on either side, less a pixel of air.</summary>
    public static RectF CellBox(int column) =>
        new(CellX(column) - _charWidth * 0.5f + 1, 1, CellWidth - 2, RowHeight - 2);

    /// <summary>The column under an x coordinate, or -1 outside the hex columns.</summary>
    public static int ColumnAt(double x)
    {
        double column = Math.Floor((x - HexX + _charWidth * 0.5) / CellWidth);
        return column >= 0 && column < Columns ? (int)column : -1;
    }

    /// <summary>Sets the grid font on a canvas, measuring it the first time.</summary>
    public static void Prepare(ICanvas canvas)
    {
        canvas.Font = Font;
        canvas.FontSize = FontSize;
        if (_measured) return;
        var size = canvas.GetStringSize(new string('0', 32), Font, FontSize);
        if (size.Width > 0)
        {
            _charWidth = size.Width / 32;
            _measured = true;
        }
    }

    public static bool IsDark => Application.Current?.RequestedTheme == AppTheme.Dark;

    public static Color TextColor => IsDark ? Colors.White : Colors.Black;

    public static Color AddressColor => IsDark ? Color.FromRgb(0xA8, 0xA8, 0xA8) : Color.FromRgb(0x50, 0x50, 0x50);

    /// <summary>The highlight for a heat value: orange for a write, green for a read, fading with the level.</summary>
    public static Color HeatColor(byte heat) =>
        (HexHeatMap.IsWrite(heat) ? WriteColors : ReadColors)[HexHeatMap.Level(heat)];

    // One colour per kind and level, so painting allocates nothing.
    private static Color[] BuildRamp(byte r, byte g, byte b)
    {
        var ramp = new Color[HexHeatMap.MaxHeat + 1];
        for (int i = 0; i < ramp.Length; i++)
            ramp[i] = Color.FromRgba(r, g, b, (byte)(230 * i / HexHeatMap.MaxHeat));
        return ramp;
    }
}

/// <summary>The dump behind a hex grid: its bytes, the read/write heat of every byte and the rows that show it.</summary>
public sealed class HexDocument
{
    public HexDocument(int size, int baseAddress)
    {
        Data = new byte[size];
        Heat = new HexHeatMap(size);
        BaseAddress = baseAddress;
        var rows = new HexRow[size / HexMetrics.Columns];
        for (int i = 0; i < rows.Length; i++)
            rows[i] = new HexRow(this, i * HexMetrics.Columns);
        Rows = rows;
    }

    public byte[] Data { get; }
    public HexHeatMap Heat { get; }
    /// <summary>Address shown for offset 0.</summary>
    public int BaseAddress { get; }
    public IReadOnlyList<HexRow> Rows { get; }

    /// <summary>Called with the offset and the new value when the user has edited a byte; the owner writes it to the machine.</summary>
    public Action<int, byte>? WriteRequested { get; set; }

    /// <summary>Repaints every row that has a control on screen (after a theme change, for instance).</summary>
    public void InvalidateVisible()
    {
        foreach (var row in Rows)
            row.View?.Invalidate();
    }
}

/// <summary>One line of 16 bytes of a <see cref="HexDocument"/>. The bytes live in the document; this is the address and the link to the control showing the row.</summary>
public sealed class HexRow
{
    internal HexRow(HexDocument document, int offset)
    {
        Document = document;
        Offset = offset;
        Address = (document.BaseAddress + offset).ToString("X4");
    }

    public HexDocument Document { get; }
    public int Offset { get; }
    public string Address { get; }

    /// <summary>The control currently showing this row, or null while it is outside the realised range of the list.</summary>
    public HexRowView? View { get; internal set; }

    public void Invalidate() => View?.Invalidate();
}

/// <summary>
/// One row of the memory viewer: the address, the 16 bytes as hex on their read/write highlights, and the bytes
/// as text, painted in one pass on a <see cref="GraphicsView"/> rather than laid out as 18 controls. A tap on a
/// byte drops a small <see cref="Entry"/> over it; Enter (or leaving the box) writes the value back.
/// <para>
/// The row knows nothing about time: it paints the document's current bytes and heat whenever it is asked to,
/// so a row that scrolls into view is right on its first paint and a row that is off screen costs nothing. The
/// list recycles these controls; <see cref="HexRow.View"/> follows the control around so the page can repaint
/// exactly the rows that are on screen.
/// </para>
/// </summary>
public sealed class HexRowView : Grid
{
    private readonly GraphicsView _canvas;
    private readonly CellEditor _editor;
    private HexRow? _row;
    private int _editColumn = -1;
    private string _editOriginal = "";

    static HexRowView()
    {
#if WINDOWS
        // The WinUI text box is built for 32 pixel rows and 64 pixel widths; this one has to fit in a hex cell.
        Microsoft.Maui.Handlers.EntryHandler.Mapper.AppendToMapping("HexCellEditor", (handler, view) =>
        {
            if (view is not CellEditor) return;
            var box = handler.PlatformView;
            box.MinHeight = 0;
            box.MinWidth = 0;
            box.Padding = new Microsoft.UI.Xaml.Thickness(2, 0, 2, 0);
            box.VerticalContentAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center;
        });
#endif
    }

    public HexRowView()
    {
        HeightRequest = HexMetrics.RowHeight;

        _canvas = new GraphicsView { Drawable = new RowDrawable(this), BackgroundColor = Colors.Transparent };
        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, e) =>
        {
            if (e.GetPosition(_canvas) is { } p)
                BeginEdit(HexMetrics.ColumnAt(p.X));
        };
        _canvas.GestureRecognizers.Add(tap);

        _editor = new CellEditor
        {
            IsVisible = false,
            FontFamily = HexMetrics.FontFamily,
            FontSize = HexMetrics.FontSize,
            MaxLength = 2,
            HorizontalOptions = LayoutOptions.Start,
            VerticalOptions = LayoutOptions.Center,
            HorizontalTextAlignment = TextAlignment.Center,
            VerticalTextAlignment = TextAlignment.Center,
            MinimumHeightRequest = 0,
            MinimumWidthRequest = 0,
            HeightRequest = HexMetrics.RowHeight,
        };
        _editor.Completed += (_, _) => EndEdit(commit: true);
        _editor.Unfocused += (_, _) => EndEdit(commit: true);

        Children.Add(_canvas);
        Children.Add(_editor);
    }

    public void Invalidate() => _canvas.Invalidate();

    protected override void OnBindingContextChanged()
    {
        base.OnBindingContextChanged();
        Attach(BindingContext as HexRow);
    }

    /// <summary>Keeps the row's back reference to its control current as the list recycles the control.</summary>
    private void Attach(HexRow? row)
    {
        if (_row == row) return;
        if (_editColumn >= 0) EndEdit(commit: false);
        if (_row is not null && _row.View == this) _row.View = null;
        _row = row;
        if (row is not null) row.View = this;
        _canvas.Invalidate();
    }

    private void BeginEdit(int column)
    {
        if (column < 0 || _row is null) return;
        if (_editColumn >= 0) EndEdit(commit: true);
        _editColumn = column;
        _editOriginal = _row.Document.Data[_row.Offset + column].ToString("X2");
        var box = HexMetrics.CellBox(column);
        _editor.Text = _editOriginal;
        _editor.Margin = new Thickness(box.X - 2, 0, 0, 0);
        _editor.WidthRequest = box.Width + 8;
        _editor.IsVisible = true;
        // Focus and select-all go through the dispatcher: done here they land before the platform box has been
        // laid out for its new visibility, and typing then appends to a box that is already two characters full.
        Dispatcher.Dispatch(() =>
        {
            if (_editColumn != column) return;
            _editor.Focus();
            _editor.CursorPosition = 0;
            _editor.SelectionLength = _editor.Text?.Length ?? 0;
        });
    }

    private void EndEdit(bool commit)
    {
        if (_editColumn < 0) return;
        int column = _editColumn;
        _editColumn = -1;
        _editor.IsVisible = false;
        if (!commit || _row is null) return;
        var text = (_editor.Text ?? "").Trim().TrimStart('$');
        if (text.Equals(_editOriginal, StringComparison.OrdinalIgnoreCase)) return;
        if (byte.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte value))
            _row.Document.WriteRequested?.Invoke(_row.Offset + column, value);
    }

    /// <summary>The in-place editor; a type of its own so the platform mapper can single it out.</summary>
    private sealed class CellEditor : Entry
    {
    }

    private sealed class RowDrawable : IDrawable
    {
        private const string Digits = "0123456789ABCDEF";
        private readonly HexRowView _owner;
        // "00 01 .. 0F" + two spaces + 16 characters of text, rebuilt on every paint.
        private readonly char[] _line = new char[HexMetrics.Columns * 3 + 1 + HexMetrics.Columns];

        public RowDrawable(HexRowView owner) => _owner = owner;

        public void Draw(ICanvas canvas, RectF dirtyRect)
        {
            if (_owner._row is not { } row) return;
            HexMetrics.Prepare(canvas);
            var data = row.Document.Data;
            var heat = row.Document.Heat;
            int offset = row.Offset;
            float height = HexMetrics.RowHeight;

            // Highlights go under the digits.
            for (int i = 0; i < HexMetrics.Columns; i++)
            {
                byte h = heat[offset + i];
                if (h == 0) continue;
                canvas.FillColor = HexMetrics.HeatColor(h);
                canvas.FillRoundedRectangle(HexMetrics.CellBox(i), 2);
            }

            var line = _line;
            int text = HexMetrics.Columns * 3 + 1;
            for (int i = 0; i < HexMetrics.Columns; i++)
            {
                byte b = data[offset + i];
                line[i * 3] = Digits[b >> 4];
                line[i * 3 + 1] = Digits[b & 0x0F];
                line[i * 3 + 2] = ' ';
                char c = Petscii.ScreenCodeToAscii((byte)(b & 0x7F));
                line[text + i] = c < ' ' || c > '~' ? '.' : c;
            }
            line[text - 1] = ' ';

            // One string for the address and one for the rest: with a monospaced face the columns fall where
            // the metrics say they do, and a row costs two text layouts rather than eighteen.
            canvas.FontColor = HexMetrics.AddressColor;
            canvas.DrawString(row.Address, HexMetrics.AddressX, 0, 4096, height,
                HorizontalAlignment.Left, VerticalAlignment.Center, TextFlow.OverflowBounds);
            canvas.FontColor = HexMetrics.TextColor;
            canvas.DrawString(new string(line), HexMetrics.HexX, 0, 4096, height,
                HorizontalAlignment.Left, VerticalAlignment.Center, TextFlow.OverflowBounds);
        }
    }
}

/// <summary>The "Addr  00 01 .. 0F  Text" line above the rows, drawn with the rows' metrics so it lines up with them.</summary>
public sealed class HexHeaderView : GraphicsView
{
    public HexHeaderView()
    {
        Drawable = new HeaderDrawable();
        HeightRequest = HexMetrics.RowHeight + 4;
        BackgroundColor = Colors.Transparent;
    }

    private sealed class HeaderDrawable : IDrawable
    {
        private static readonly string Line = BuildLine();

        private static string BuildLine()
        {
            var sb = new System.Text.StringBuilder("Addr  ");
            for (int i = 0; i < HexMetrics.Columns; i++)
                sb.Append(i.ToString("X2")).Append(' ');
            return sb.Append(" Text").ToString();
        }

        public void Draw(ICanvas canvas, RectF dirtyRect)
        {
            HexMetrics.Prepare(canvas);
            canvas.FontColor = HexMetrics.AddressColor;
            canvas.DrawString(Line, HexMetrics.AddressX, 0, 4096, dirtyRect.Height,
                HorizontalAlignment.Left, VerticalAlignment.Center, TextFlow.OverflowBounds);
        }
    }
}
