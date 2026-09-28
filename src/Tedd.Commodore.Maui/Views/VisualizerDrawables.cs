namespace Tedd.Commodore.Maui.Views;

/// <summary>Oscilloscope: draws a block of 16-bit samples as a polyline.</summary>
internal sealed class ScopeDrawable : IDrawable
{
    private static readonly Color Background = Color.FromRgb(0x10, 0x10, 0x10);
    private static readonly Color GridColor = Color.FromRgb(0x30, 0x30, 0x30);
    private static readonly Color TraceColor = Color.FromRgb(0x40, 0xE0, 0x40);

    private short[] _samples = Array.Empty<short>();

    /// <summary>Shows the samples (the array is read while drawing, so the caller must not resize it).</summary>
    public void SetSamples(short[] samples) => _samples = samples;

    public void Draw(ICanvas canvas, RectF dirtyRect)
    {
        float w = dirtyRect.Width, h = dirtyRect.Height;
        if (w <= 0 || h <= 0) return;
        canvas.FillColor = Background;
        canvas.FillRectangle(0, 0, w, h);

        canvas.StrokeSize = 1;
        canvas.StrokeColor = GridColor;
        float mid = h / 2;
        canvas.DrawLine(0, mid, w, mid);
        canvas.DrawLine(0, h / 4, w, h / 4);
        canvas.DrawLine(0, 3 * h / 4, w, 3 * h / 4);

        var samples = _samples;
        if (samples.Length < 2) return;
        float xs = w / (samples.Length - 1);
        float ys = (h / 2 - 1) / 32768f;
        var path = new PathF();
        path.MoveTo(0, mid - samples[0] * ys);
        for (int i = 1; i < samples.Length; i++)
            path.LineTo(i * xs, mid - samples[i] * ys);
        canvas.StrokeColor = TraceColor;
        canvas.StrokeSize = 1.2f;
        canvas.DrawPath(path);
    }
}

/// <summary>Spectrum analyser bars (0..1 each) with peak hold and optional frequency labels under some bars.</summary>
internal sealed class SpectrumDrawable : IDrawable
{
    private const float LabelHeight = 14;
    private const float PeakDecay = 0.985f;

    private static readonly Color Background = Color.FromRgb(0x10, 0x10, 0x10);
    private static readonly Color BarColor = Color.FromRgb(0x30, 0x90, 0xE0);
    private static readonly Color LoudBarColor = Color.FromRgb(0xE0, 0x80, 0x30);
    private static readonly Color PeakColor = Color.FromRgb(0xF0, 0xF0, 0xF0);
    private static readonly Color LabelColor = Color.FromRgb(0x90, 0x90, 0x90);
    private static readonly Color GridColor = Color.FromRgb(0x30, 0x30, 0x30);

    private float[] _bars = Array.Empty<float>();
    private float[] _peaks = Array.Empty<float>();
    private string?[] _labels = Array.Empty<string?>();

    /// <summary>Labels drawn under bars (null entries draw nothing); the array length must match the bar count.</summary>
    public void SetLabels(string?[] labels) => _labels = labels;

    /// <summary>Shows the bar heights (0..1). The array is read while drawing, so it must not be resized.</summary>
    public void SetBars(float[] bars)
    {
        _bars = bars;
        if (_peaks.Length != bars.Length) _peaks = new float[bars.Length];
        for (int i = 0; i < bars.Length; i++)
        {
            float p = _peaks[i] * PeakDecay;
            _peaks[i] = bars[i] > p ? bars[i] : p;
        }
    }

    public void Draw(ICanvas canvas, RectF dirtyRect)
    {
        float w = dirtyRect.Width, h = dirtyRect.Height;
        if (w <= 0 || h <= 0) return;
        canvas.FillColor = Background;
        canvas.FillRectangle(0, 0, w, h);

        var bars = _bars;
        if (bars.Length == 0) return;
        float plotHeight = Math.Max(1, h - LabelHeight);

        // -20 / -40 dB grid lines (bars are (dB + 60) / 60).
        canvas.StrokeSize = 1;
        canvas.StrokeColor = GridColor;
        for (int i = 1; i < 3; i++)
            canvas.DrawLine(0, plotHeight * i / 3, w, plotHeight * i / 3);

        float slot = w / bars.Length;
        float barWidth = Math.Max(1, slot - 2);
        canvas.FontSize = 10;
        for (int i = 0; i < bars.Length; i++)
        {
            float x = i * slot + 1;
            float v = Math.Clamp(bars[i], 0f, 1f);
            float barHeight = v * plotHeight;
            if (barHeight >= 1)
            {
                canvas.FillColor = v > 0.9f ? LoudBarColor : BarColor;
                canvas.FillRectangle(x, plotHeight - barHeight, barWidth, barHeight);
            }
            float peak = Math.Clamp(_peaks[i], 0f, 1f) * plotHeight;
            if (peak >= 1)
            {
                canvas.FillColor = PeakColor;
                canvas.FillRectangle(x, plotHeight - peak, barWidth, 1);
            }
            if (i < _labels.Length && _labels[i] is { } label)
            {
                canvas.FontColor = LabelColor;
                canvas.DrawString(label, x, plotHeight + 1, slot * 4, LabelHeight, HorizontalAlignment.Left, VerticalAlignment.Top);
            }
        }
    }
}
