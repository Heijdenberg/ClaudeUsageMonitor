using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TokenUsage.Core;

namespace TokenUsage.Monitor;

/// <summary>One rate-limit window: how much is left, when it resets, and what Claude Code used in it.</summary>
public partial class LimitCard : UserControl
{
    internal static readonly Brush Green = Frozen(0x4C, 0xAF, 0x50);
    internal static readonly Brush Yellow = Frozen(0xE0, 0xB0, 0x30);
    internal static readonly Brush Red = Frozen(0xE0, 0x50, 0x50);

    public LimitCard() => InitializeComponent();

    public string Title { get => TitleText.Text; set => TitleText.Text = value; }

    /// <summary>
    /// Shows the window as stored, with the usage logged in it and how its percentage grew (both null when
    /// there is no reset time to bound the window).
    /// </summary>
    public void Show(StoredLimit? limit, TimeSpan length, (TokenCounts Tokens, double? CostUsd)? usage,
        IReadOnlyList<LimitPoint>? history, DateTimeOffset now)
    {
        var used = limit?.Window.UsedPercentage;
        var resetsAt = limit?.Window.ResetsAt;
        var expired = resetsAt is not null && limit!.Window.IsExpired(now);

        ChartTitle.Text = expired ? "Förbrukning i förra fönstret" : "Förbrukning i fönstret";
        if (resetsAt is DateTimeOffset end && history is not null)
            Chart.Show(history, end - length, end, now, history.Count > 0 ? BrushFor(history[^1].UsedPercentage) : Green);
        else
            Chart.Clear();

        if (expired)
        {
            // The old percentage no longer applies; the next window gets values when a session reports again.
            ShowUsed(null);
            SetLeft("Återställd", Muted);
            ResetInText.Text = "inväntar nya värden";
            ResetAtText.Text = "Återställdes " + FormatTime(resetsAt!.Value);
            ShowUsage("Förra fönstret", usage);
            return;
        }

        ShowUsed(used);
        if (used is double u)
            SetLeft(string.Format(MainWindow.Swedish, "{0:0} % kvar", Math.Max(0, 100 - u)), BrushFor(u));
        else
            SetLeft("Inväntar data", Muted);

        ResetInText.Text = resetsAt is DateTimeOffset r ? "Återställs om " + FormatLeft(r - now) : used is null ? "" : "Återställning: inväntar data";
        ResetAtText.Text = resetsAt is DateTimeOffset at ? FormatTime(at) : "";
        ShowUsage("I fönstret", usage);
    }

    public void ShowNoData() => Show(null, TimeSpan.Zero, null, null, DateTimeOffset.Now);

    private void ShowUsed(double? used)
    {
        Ring.Value = used;
        if (used is double u) Ring.RingBrush = BrushFor(u);
    }

    private void SetLeft(string text, Brush brush)
    {
        LeftText.Text = text;
        LeftText.Foreground = brush;
    }

    private void ShowUsage(string title, (TokenCounts Tokens, double? CostUsd)? usage)
    {
        var sv = MainWindow.Swedish;
        FiguresTitle.Text = usage is null ? "" : title + ", enligt Claude Codes loggar";
        ResponsesText.Text = usage is { } x ? x.Tokens.Responses.ToString("N0", sv) : "–";
        OutputText.Text = usage is { } y ? y.Tokens.Output.ToString("N0", sv) : "–";
        CostText.Text = usage is { } z ? string.Format(sv, "{0:N2} $", z.CostUsd ?? 0) : "–";
    }

    private Brush Muted => (Brush)FindResource("Muted");

    internal static Brush BrushFor(double used) => used >= 90 ? Red : used >= 75 ? Yellow : Green;

    private static string FormatTime(DateTimeOffset at) => at.LocalDateTime.ToString("dddd d MMMM HH:mm", MainWindow.Swedish);

    /// <summary>"7 min", "2 h 14 min", "2 d 7 h".</summary>
    internal static string FormatLeft(TimeSpan s)
    {
        if (s < TimeSpan.Zero) s = TimeSpan.Zero;
        if (s.TotalDays >= 1) return $"{(int)s.TotalDays} d {s.Hours} h";
        if (s.TotalHours >= 1) return $"{(int)s.TotalHours} h {s.Minutes} min";
        return $"{Math.Max(1, (int)Math.Ceiling(s.TotalMinutes))} min";
    }

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
