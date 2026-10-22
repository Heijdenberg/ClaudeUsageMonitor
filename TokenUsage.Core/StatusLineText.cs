using System.Globalization;

namespace TokenUsage.Core;

/// <summary>The text Claude Code shows in the status line.</summary>
public static class StatusLineText
{
    public static string Format(StatusInput input, DateTimeOffset now) =>
        $"{FormatWindow("5h", input.FiveHour, now)} | {FormatWindow("7d", input.SevenDay, now)}";

    public static string FormatWindow(string label, LimitWindow w, DateTimeOffset now)
    {
        if (w.UsedPercentage is not double used) return $"{label}: inväntar data";
        var text = $"{label}: {Math.Round(used).ToString(CultureInfo.InvariantCulture)}%";
        if (w.ResetsAt is not DateTimeOffset reset) return text + " (återställning: inväntar data)";

        var span = reset - now;
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        var left = span.TotalDays >= 1 ? $"{(int)Math.Floor(span.TotalDays)}d {span.Hours}h"
            : span.TotalHours >= 1 ? $"{(int)Math.Floor(span.TotalHours)}h {span.Minutes}m"
            : $"{(int)Math.Ceiling(span.TotalMinutes)}m";
        return text + $" (återställs om {left})";
    }
}
