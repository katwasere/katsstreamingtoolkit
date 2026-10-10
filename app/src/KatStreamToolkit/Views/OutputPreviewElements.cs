using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace KatStreamToolkit.Views;

public enum FrameStretch
{
    Fill,
    Uniform,
}

// Draws the "OBS frame" used by the Output Studio rendition previews: either a
// colour-bar test card or a user-loaded screenshot, stretched to the element size.
public class FramePreview : FrameworkElement
{
    private static readonly Brush[] Bars =
    {
        Freeze(0xB4, 0xB4, 0xB4), Freeze(0xB4, 0xB4, 0x00), Freeze(0x00, 0xB4, 0xB4), Freeze(0x00, 0xB4, 0x00),
        Freeze(0xB4, 0x00, 0xB4), Freeze(0xB4, 0x00, 0x00), Freeze(0x00, 0x00, 0xB4),
    };

    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source), typeof(ImageSource), typeof(FramePreview), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StretchProperty = DependencyProperty.Register(
        nameof(Stretch), typeof(FrameStretch), typeof(FramePreview), new FrameworkPropertyMetadata(FrameStretch.Fill, FrameworkPropertyMetadataOptions.AffectsRender));

    public ImageSource? Source
    {
        get => (ImageSource?)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public FrameStretch Stretch
    {
        get => (FrameStretch)GetValue(StretchProperty);
        set => SetValue(StretchProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        if (Source != null)
        {
            Rect rect = Stretch == FrameStretch.Fill
                ? new Rect(0, 0, w, h)
                : FitRect(Source.Width / Source.Height, w, h);
            dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, w, h));
            dc.DrawImage(Source, rect);
            return;
        }

        for (int i = 0; i < Bars.Length; i++)
        {
            var bar = new Rect(w * i / Bars.Length, 0, w / Bars.Length + 0.5, h * 0.75);
            dc.DrawRectangle(Bars[i], null, bar);
        }
        dc.DrawRectangle(Freeze(0x10, 0x12, 0x16), null, new Rect(0, h * 0.75, w, h * 0.25));
        dc.DrawEllipse(Freeze(0xE6, 0xE8, 0xEB), null, new Point(w / 2, h * 0.875), h * 0.075, h * 0.075);

        var text = new FormattedText(
            "your OBS output", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), Math.Max(8, h * 0.07), Freeze(0xE6, 0xE8, 0xEB), 1.25);
        dc.DrawText(text, new Point((w - text.Width) / 2, h * 0.76));
    }

    private static Rect FitRect(double aspect, double w, double h)
    {
        double boxAspect = w / h;
        double rw = aspect >= boxAspect ? w : h * aspect;
        double rh = aspect >= boxAspect ? w / aspect : h;
        return new Rect((w - rw) / 2, (h - rh) / 2, rw, rh);
    }

    private static SolidColorBrush Freeze(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}

// Darkens everything outside the crop window over a source frame and outlines
// the kept region, so you can see exactly what a portrait crop keeps.
public class CropOverlay : FrameworkElement
{
    public static readonly DependencyProperty SourceWProperty = DependencyProperty.Register(
        nameof(SourceW), typeof(double), typeof(CropOverlay), new FrameworkPropertyMetadata(1920.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SourceHProperty = DependencyProperty.Register(
        nameof(SourceH), typeof(double), typeof(CropOverlay), new FrameworkPropertyMetadata(1080.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CropWProperty = DependencyProperty.Register(
        nameof(CropW), typeof(double), typeof(CropOverlay), new FrameworkPropertyMetadata(607.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CropHProperty = DependencyProperty.Register(
        nameof(CropH), typeof(double), typeof(CropOverlay), new FrameworkPropertyMetadata(1080.0, FrameworkPropertyMetadataOptions.AffectsRender));

    // Crop origin in source pixels. NaN (= default) keeps the historic centered
    // behavior for CenterCrop; Custom layouts pass their real (draggable) origin
    // so the outline moves with the crop instead of always sitting centered.
    public static readonly DependencyProperty CropXProperty = DependencyProperty.Register(
        nameof(CropX), typeof(double), typeof(CropOverlay), new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CropYProperty = DependencyProperty.Register(
        nameof(CropY), typeof(double), typeof(CropOverlay), new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));

    public double SourceW { get => (double)GetValue(SourceWProperty); set => SetValue(SourceWProperty, value); }
    public double SourceH { get => (double)GetValue(SourceHProperty); set => SetValue(SourceHProperty, value); }
    public double CropW { get => (double)GetValue(CropWProperty); set => SetValue(CropWProperty, value); }
    public double CropH { get => (double)GetValue(CropHProperty); set => SetValue(CropHProperty, value); }
    public double CropX { get => (double)GetValue(CropXProperty); set => SetValue(CropXProperty, value); }
    public double CropY { get => (double)GetValue(CropYProperty); set => SetValue(CropYProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0 || SourceW <= 0 || SourceH <= 0) return;

        double sx = w / SourceW, sy = h / SourceH;
        double cx = (double.IsNaN(CropX) ? Math.Max(0, (SourceW - CropW) / 2) : Math.Max(0, CropX)) * sx;
        double cy = (double.IsNaN(CropY) ? Math.Max(0, (SourceH - CropH) / 2) : Math.Max(0, CropY)) * sy;
        double cw = CropW * sx, ch = CropH * sy;

        var dim = new SolidColorBrush(Color.FromArgb(160, 0, 0, 0));
        dim.Freeze();
        dc.DrawRectangle(dim, null, new Rect(0, 0, w, Math.Max(0, cy)));
        dc.DrawRectangle(dim, null, new Rect(0, cy + ch, w, Math.Max(0, h - cy - ch)));
        dc.DrawRectangle(dim, null, new Rect(0, cy, Math.Max(0, cx), Math.Max(0, ch)));
        dc.DrawRectangle(dim, null, new Rect(cx + cw, cy, Math.Max(0, w - cx - cw), Math.Max(0, ch)));

        var pen = new Pen(Freeze(0x8F, 0xD6, 0x94), 2);
        dc.DrawRectangle(null, pen, new Rect(cx, cy, cw, ch));
    }

    private static SolidColorBrush Freeze(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
