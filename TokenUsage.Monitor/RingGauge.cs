using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace TokenUsage.Monitor;

/// <summary>A ring that fills clockwise from the top, with the percentage in the middle.</summary>
public class RingGauge : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double?), typeof(RingGauge),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty RingBrushProperty = DependencyProperty.Register(
        nameof(RingBrush), typeof(Brush), typeof(RingGauge),
        new FrameworkPropertyMetadata(Brushes.LimeGreen, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(RingGauge),
        new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackEdgeBrushProperty = DependencyProperty.Register(
        nameof(TrackEdgeBrush), typeof(Brush), typeof(RingGauge),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(
        nameof(Thickness), typeof(double), typeof(RingGauge),
        new FrameworkPropertyMetadata(12.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(
        nameof(Foreground), typeof(Brush), typeof(RingGauge),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CaptionProperty = DependencyProperty.Register(
        nameof(Caption), typeof(string), typeof(RingGauge),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Percentage 0–100, or null when there is no data yet.</summary>
    public double? Value { get => (double?)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public Brush RingBrush { get => (Brush)GetValue(RingBrushProperty); set => SetValue(RingBrushProperty, value); }
    public Brush TrackBrush { get => (Brush)GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    /// <summary>Optional 1 px line along the inner and outer edge of the ring, drawn over the fill.</summary>
    public Brush? TrackEdgeBrush { get => (Brush?)GetValue(TrackEdgeBrushProperty); set => SetValue(TrackEdgeBrushProperty, value); }
    public double Thickness { get => (double)GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    /// <summary>Small text under the percentage, such as "använt".</summary>
    public string? Caption { get => (string?)GetValue(CaptionProperty); set => SetValue(CaptionProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= Thickness) return;

        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var radius = (size - Thickness) / 2;

        dc.DrawEllipse(null, new Pen(TrackBrush, Thickness), center, radius, radius);

        if (Value is double v && v > 0)
        {
            var pen = new Pen(RingBrush, Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            var fraction = Math.Clamp(v, 0, 100) / 100;
            if (fraction >= 0.9999)
                dc.DrawEllipse(null, pen, center, radius, radius);
            else
                dc.DrawGeometry(null, pen, Arc(center, radius, fraction));
        }

        if (TrackEdgeBrush is { } edge)
        {
            // Half a pixel in from each edge, so the lines stay inside the ring's bounds.
            var edgePen = new Pen(edge, 1);
            dc.DrawEllipse(null, edgePen, center, radius + Thickness / 2 - 0.5, radius + Thickness / 2 - 0.5);
            dc.DrawEllipse(null, edgePen, center, radius - Thickness / 2 + 0.5, radius - Thickness / 2 + 0.5);
        }

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        // The window's font: the figure in its bold weight, the caption in the inherited one.
        var family = TextElement.GetFontFamily(this);
        var typeface = new Typeface(family, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        var text = Value is double p
            ? new FormattedText($"{p:0.#} %", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, size * 0.2, Foreground, dpi)
            : new FormattedText("inväntar\ndata", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, size * 0.1, Foreground, dpi);
        text.MaxTextWidth = size;
        text.TextAlignment = TextAlignment.Center;

        if (Value is null || string.IsNullOrEmpty(Caption))
        {
            dc.DrawText(text, new Point(center.X - size / 2, center.Y - text.Height / 2));
            return;
        }
        var caption = new FormattedText(Caption, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(family, FontStyles.Normal, TextElement.GetFontWeight(this), FontStretches.Normal), size * 0.09, Foreground, dpi) { MaxTextWidth = size, TextAlignment = TextAlignment.Center };
        var top = center.Y - (text.Height + caption.Height) / 2;
        dc.DrawText(text, new Point(center.X - size / 2, top));
        dc.PushOpacity(0.6);
        dc.DrawText(caption, new Point(center.X - size / 2, top + text.Height));
        dc.Pop();
    }

    // Arc clockwise from 12 o'clock covering the given fraction of the circle.
    private static Geometry Arc(Point c, double r, double fraction)
    {
        var angle = fraction * 2 * Math.PI;
        var start = new Point(c.X, c.Y - r);
        var end = new Point(c.X + r * Math.Sin(angle), c.Y - r * Math.Cos(angle));
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(start, false, false);
            ctx.ArcTo(end, new Size(r, r), 0, fraction > 0.5, SweepDirection.Clockwise, true, false);
        }
        geometry.Freeze();
        return geometry;
    }
}
