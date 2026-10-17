using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace KatStreamToolkit.Views;

// Minimal dependency-free line chart for the relay metrics history: two
// polylines (in/out Mbps) over a dark grid, autoscaled y, age labels on x.
// Drawn in OnRender - no chart library, nothing animated, redraws only when
// the samples change or the control resizes.
public class MetricsChart : FrameworkElement
{
    public static readonly DependencyProperty SamplesProperty = DependencyProperty.Register(
        nameof(Samples), typeof(System.Collections.Generic.IReadOnlyList<KatStreamToolkit.Services.MetricSample>),
        typeof(MetricsChart), new PropertyMetadata(null, OnSamplesChanged));

    private static void OnSamplesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((MetricsChart)d).InvalidateVisual();

    public System.Collections.Generic.IReadOnlyList<KatStreamToolkit.Services.MetricSample>? Samples
    {
        get => (System.Collections.Generic.IReadOnlyList<KatStreamToolkit.Services.MetricSample>?)GetValue(SamplesProperty);
        set => SetValue(SamplesProperty, value);
    }

    private static readonly Pen GridPen = FreezePen(new SolidColorBrush(Color.FromRgb(0x2A, 0x2F, 0x37)));
    private static readonly Pen InPen = FreezePen(new SolidColorBrush(Color.FromRgb(0x8F, 0xD6, 0x94)));
    private static readonly Pen OutPen = FreezePen(new SolidColorBrush(Color.FromRgb(0x6A, 0xB0, 0xF3)));
    private static readonly Brush TextBrush = FreezeBrush(new SolidColorBrush(Color.FromRgb(0x9A, 0xA0, 0xA8)));

    public MetricsChart()
    {
        GridPen.DashStyle = DashStyles.Dash;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 10 || h < 10) return;
        var samples = Samples;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));

        // Frame + horizontal grid (4 divisions) with Mbps labels.
        double max = 1;
        if (samples is { Count: > 0 })
            max = Math.Max(1, samples.Max(s => Math.Max(s.InMbps, s.OutMbps)) * 1.15);
        var typeface = new Typeface("Segoe UI");
        for (int i = 0; i <= 4; i++)
        {
            double y = h * i / 4;
            dc.DrawLine(GridPen, new Point(0, y), new Point(w, y));
            string label = (max * (4 - i) / 4).ToString("0", CultureInfo.CurrentCulture);
            DrawText(dc, typeface, label, 10, new Point(4, y + 2), TextBrush);
        }

        if (samples is not { Count: > 1 })
        {
            DrawText(dc, typeface, "collecting samples (one per minute)...", 11,
                new Point(w / 2 - 90, h / 2), TextBrush);
            return;
        }

        Point Pt(int i, double value)
        {
            double x = w * i / (samples.Count - 1);
            double y = h - h * (value / max);
            return new Point(x, Math.Clamp(y, 0, h));
        }

        var geoIn = new StreamGeometry();
        var geoOut = new StreamGeometry();
        using (var gIn = geoIn.Open())
        using (var gOut = geoOut.Open())
        {
            for (int i = 0; i < samples.Count; i++)
            {
                var pIn = Pt(i, samples[i].InMbps);
                var pOut = Pt(i, samples[i].OutMbps);
                if (i == 0) { gIn.BeginFigure(pIn, false, false); gOut.BeginFigure(pOut, false, false); }
                else { gIn.LineTo(pIn, true, false); gOut.LineTo(pOut, true, false); }
            }
        }
        dc.DrawGeometry(null, InPen, geoIn);
        dc.DrawGeometry(null, OutPen, geoOut);

        // Age labels along the bottom (start / middle / now).
        var start = samples[0].UtcTime.ToLocalTime();
        var end = samples[^1].UtcTime.ToLocalTime();
        DrawText(dc, typeface, start.ToString("HH:mm"), 10, new Point(4, h - 14), TextBrush);
        DrawText(dc, typeface, end.ToString("HH:mm"), 10, new Point(w - 40, h - 14), TextBrush);

        // Legend, top right.
        DrawText(dc, typeface, $"in {samples[^1].InMbps:0.0}", 10, new Point(w - 150, 4), InPen.Brush);
        DrawText(dc, typeface, $"out {samples[^1].OutMbps:0.0} Mbps", 10, new Point(w - 92, 4), OutPen.Brush);
    }

    private static void DrawText(DrawingContext dc, Typeface typeface, string text, double size, Point at, Brush brush)
    {
        dc.DrawText(new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            typeface, size, brush, 1.25), at);
    }

    private static Pen FreezePen(SolidColorBrush b)
    {
        b.Freeze();
        var p = new Pen(b, 1.2);
        p.Freeze();
        return p;
    }

    private static Brush FreezeBrush(SolidColorBrush b)
    {
        b.Freeze();
        return b;
    }
}
