using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Tedd.Commodore.GUI;

/// <summary>Oscilloscope: draws a block of 16-bit samples as a polyline. Call <see cref="SetSamples"/> then it redraws.</summary>
public sealed class ScopeView : FrameworkElement
{
    private static readonly Brush BackgroundBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x10, 0x10, 0x10)));
    private static readonly Pen GridPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x30, 0x30, 0x30)), 1));
    private static readonly Pen TracePen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x40, 0xE0, 0x40)), 1.2));

    private short[] _samples = Array.Empty<short>();

    /// <summary>Shows the samples (the array is read during rendering, so the caller must not resize it).</summary>
    public void SetSamples(short[] samples)
    {
        _samples = samples;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        dc.DrawRectangle(BackgroundBrush, null, new Rect(0, 0, w, h));
        double mid = Math.Floor(h / 2) + 0.5;
        dc.DrawLine(GridPen, new Point(0, mid), new Point(w, mid));
        dc.DrawLine(GridPen, new Point(0, Math.Floor(h / 4) + 0.5), new Point(w, Math.Floor(h / 4) + 0.5));
        dc.DrawLine(GridPen, new Point(0, Math.Floor(3 * h / 4) + 0.5), new Point(w, Math.Floor(3 * h / 4) + 0.5));

        var samples = _samples;
        if (samples.Length < 2) return;
        double xs = w / (samples.Length - 1);
        double ys = (h / 2 - 1) / 32768.0;
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(0, mid - samples[0] * ys), false, false);
            for (int i = 1; i < samples.Length; i++)
                ctx.LineTo(new Point(i * xs, mid - samples[i] * ys), true, false);
        }
        geometry.Freeze();
        dc.DrawGeometry(null, TracePen, geometry);
    }

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}

/// <summary>Spectrum analyser bars (0..1 each) with peak hold and optional frequency labels under some bars.</summary>
public sealed class SpectrumView : FrameworkElement
{
    private static readonly Brush BackgroundBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x10, 0x10, 0x10)));
    private static readonly Brush BarBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x30, 0x90, 0xE0)));
    private static readonly Brush LoudBarBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xE0, 0x80, 0x30)));
    private static readonly Brush PeakBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0)));
    private static readonly Brush LabelBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x90, 0x90, 0x90)));
    private static readonly Pen GridPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x30, 0x30, 0x30)), 1));
    private static readonly Typeface LabelTypeface = new("Consolas");
    private const double LabelHeight = 14;
    private const float PeakDecay = 0.985f;

    private float[] _bars = Array.Empty<float>();
    private float[] _peaks = Array.Empty<float>();
    private string?[] _labels = Array.Empty<string?>();
    private FormattedText?[] _labelTexts = Array.Empty<FormattedText?>();

    /// <summary>Labels drawn under bars (null entries draw nothing); the array length must match the bar count.</summary>
    public void SetLabels(string?[] labels)
    {
        _labels = labels;
        _labelTexts = new FormattedText?[labels.Length];
    }

    /// <summary>Shows the bar heights (0..1). The array is read during rendering, so the caller must not resize it.</summary>
    public void SetBars(float[] bars)
    {
        _bars = bars;
        if (_peaks.Length != bars.Length) _peaks = new float[bars.Length];
        for (int i = 0; i < bars.Length; i++)
        {
            float p = _peaks[i] * PeakDecay;
            _peaks[i] = bars[i] > p ? bars[i] : p;
        }
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        dc.DrawRectangle(BackgroundBrush, null, new Rect(0, 0, w, h));

        var bars = _bars;
        if (bars.Length == 0) return;
        double plotHeight = Math.Max(1, h - LabelHeight);
        // -20 / -40 dB grid lines (bars are (dB + 60) / 60).
        for (int i = 1; i < 3; i++)
        {
            double y = Math.Floor(plotHeight * i / 3) + 0.5;
            dc.DrawLine(GridPen, new Point(0, y), new Point(w, y));
        }

        double slot = w / bars.Length;
        double barWidth = Math.Max(1, slot - 2);
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        for (int i = 0; i < bars.Length; i++)
        {
            double x = i * slot + 1;
            double v = Math.Clamp(bars[i], 0f, 1f);
            double barHeight = v * plotHeight;
            if (barHeight >= 1)
                dc.DrawRectangle(v > 0.9 ? LoudBarBrush : BarBrush, null, new Rect(x, plotHeight - barHeight, barWidth, barHeight));
            double peak = Math.Clamp(_peaks[i], 0f, 1f) * plotHeight;
            if (peak >= 1)
                dc.DrawRectangle(PeakBrush, null, new Rect(x, plotHeight - peak, barWidth, 1));

            if (i < _labels.Length && _labels[i] is { } label)
            {
                var text = _labelTexts[i] ??= new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    LabelTypeface, 10, LabelBrush, pixelsPerDip);
                dc.DrawText(text, new Point(x, plotHeight + 1));
            }
        }
    }

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
