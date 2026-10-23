using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using TokenUsage.Core;

namespace TokenUsage.Monitor;

/// <summary>
/// A limit window's percentage over time, as steps (it holds until the next change), from the window's
/// start to its reset. The dashed line is an even pace that would reach 100 % exactly at the reset.
/// </summary>
public class UsageChart : FrameworkElement
{
    private const double AxisWidth = 34, AxisHeight = 18;

    private IReadOnlyList<LimitPoint> _points = [];
    private DateTimeOffset _from, _to, _now;

    public Brush LineBrush { get; set; } = Brushes.LimeGreen;
    public Brush GridBrush { get; set; } = Brushes.DimGray;
    public Brush LabelBrush { get; set; } = Brushes.Gray;

    public void Show(IReadOnlyList<LimitPoint> points, DateTimeOffset from, DateTimeOffset to, DateTimeOffset now, Brush line)
    {
        (_points, _from, _to, _now, LineBrush) = (points, from, to, now, line);
        InvalidateVisual();
    }

    public void Clear()
    {
        _points = [];
        _from = _to = default;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var plot = new Rect(AxisWidth, 6, Math.Max(0, ActualWidth - AxisWidth - 6), Math.Max(0, ActualHeight - AxisHeight - 6));
        if (plot.Width < 20 || plot.Height < 20) return;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));   // the whole area takes the tooltip

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var gridPen = new Pen(GridBrush, 1) { DashStyle = new DashStyle([2, 3], 0) };
        foreach (var pct in new[] { 0, 50, 100 })
        {
            var y = Math.Round(plot.Bottom - plot.Height * pct / 100) + 0.5;
            dc.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            var label = Text($"{pct} %", dpi);
            dc.DrawText(label, new Point(plot.Left - label.Width - 8, y - label.Height / 2));
        }

        if (_to <= _from)
        {
            var empty = Text("Ingen historik för fönstret än", dpi);
            dc.DrawText(empty, new Point(plot.Left + (plot.Width - empty.Width) / 2, plot.Top + (plot.Height - empty.Height) / 2));
            return;
        }

        var start = Text(_from.LocalDateTime.ToString(Format, MainWindow.Swedish), dpi);
        var end = Text(_to.LocalDateTime.ToString(Format, MainWindow.Swedish), dpi);
        dc.DrawText(start, new Point(plot.Left, plot.Bottom + 4));
        dc.DrawText(end, new Point(plot.Right - end.Width, plot.Bottom + 4));

        Point At(DateTimeOffset t, double pct) => new(
            plot.Left + plot.Width * Math.Clamp((t - _from) / (_to - _from), 0, 1),
            plot.Bottom - plot.Height * Math.Clamp(pct, 0, 100) / 100);

        var pacePen = new Pen(LabelBrush, 1) { DashStyle = new DashStyle([4, 4], 0) };
        dc.PushOpacity(0.5);
        dc.DrawLine(pacePen, At(_from, 0), At(_to, 100));
        dc.Pop();

        if (_points.Count == 0) return;

        // Steps: each value holds until the next change, the last one until now (or the reset, if earlier).
        var until = _now < _to ? _now : _to;
        var line = new StreamGeometry();
        var area = new StreamGeometry();
        using (var l = line.Open())
        using (var a = area.Open())
        {
            var first = At(_points[0].At, _points[0].UsedPercentage);
            l.BeginFigure(first, false, false);
            a.BeginFigure(new Point(first.X, plot.Bottom), true, true);
            a.LineTo(first, false, false);
            var previous = _points[0];
            foreach (var p in _points.Skip(1).Append(new LimitPoint(until, _points[^1].UsedPercentage)))
            {
                var flat = At(p.At, previous.UsedPercentage);
                var step = At(p.At, p.UsedPercentage);
                l.LineTo(flat, true, true);
                l.LineTo(step, true, true);
                a.LineTo(flat, false, false);
                a.LineTo(step, false, false);
                previous = p;
            }
            a.LineTo(new Point(At(until, 0).X, plot.Bottom), false, false);
        }

        var color = (LineBrush as SolidColorBrush)?.Color ?? Colors.LimeGreen;
        var fill = new LinearGradientBrush(Color.FromArgb(0x55, color.R, color.G, color.B), Color.FromArgb(0x05, color.R, color.G, color.B), 90);
        dc.DrawGeometry(fill, null, area);
        dc.DrawGeometry(null, new Pen(LineBrush, 2) { LineJoin = PenLineJoin.Round }, line);
        dc.DrawEllipse(LineBrush, null, At(until, _points[^1].UsedPercentage), 3.5, 3.5);
    }

    // Five-hour windows by clock time, longer ones with the day.
    private string Format => _to - _from > TimeSpan.FromDays(1) ? "ddd d MMM HH:mm" : "HH:mm";

    private FormattedText Text(string s, double dpi) =>
        new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(TextElement.GetFontFamily(this), FontStyles.Normal, TextElement.GetFontWeight(this), FontStretches.Normal), 12, LabelBrush, dpi);
}
