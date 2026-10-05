using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace UsageMonitor;

public partial class MainWindow : Window
{
    private const string UsagePath = @"C:\temp\usage.json";
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(30);
    private static readonly CultureInfo Swedish = CultureInfo.GetCultureInfo("sv-SE");

    private static readonly Brush Green = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
    private static readonly Brush Yellow = new SolidColorBrush(Color.FromRgb(0xE0, 0xB0, 0x30));
    private static readonly Brush Red = new SolidColorBrush(Color.FromRgb(0xE0, 0x50, 0x50));

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(5) };

    public MainWindow()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Refresh();
    }

    private void Refresh()
    {
        JsonDocument doc;
        try
        {
            // Statusradsskriptet byter ut filen atomiskt, så vi tillåter att den skrivs/ersätts medan vi läser.
            using var stream = new FileStream(UsagePath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            doc = JsonDocument.Parse(stream);
        }
        catch (FileNotFoundException)
        {
            ShowNoData($"Hittar inte {UsagePath}. Har statusraden körts?");
            return;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Behåll senaste visning och försök igen vid nästa tick.
            Status.Text = $"Kunde inte läsa filen just nu ({ex.GetType().Name}). Försöker igen om 5 s.";
            return;
        }

        using (doc)
        {
            var root = doc.RootElement;
            ShowWindow(root, "five_hour", FivePercent, FiveBar, FiveReset);
            ShowWindow(root, "seven_day", WeekPercent, WeekBar, WeekReset);

            var timestamp = ReadDate(root, "timestamp");
            if (timestamp is null)
            {
                Status.Text = "Filen saknar tidsstämpel.";
                Status.Foreground = Yellow;
                return;
            }

            var age = DateTimeOffset.Now - timestamp.Value;
            var text = $"Filen skrevs {timestamp.Value.LocalDateTime:HH:mm:ss} ({FormatSpan(age)} sedan). Uppdaterad {DateTime.Now:HH:mm:ss}.";
            if (age > StaleAfter)
            {
                Status.Text = text + "\nFilen uppdateras inte – körs Claude Code?";
                Status.Foreground = Yellow;
            }
            else
            {
                Status.Text = text;
                Status.ClearValue(TextBlock.ForegroundProperty);
            }
        }
    }

    private static void ShowWindow(JsonElement root, string name, TextBlock percent, ProgressBar bar, TextBlock reset)
    {
        double? used = null;
        DateTimeOffset? resetsAt = null;
        if (root.TryGetProperty(name, out var w) && w.ValueKind == JsonValueKind.Object)
        {
            if (w.TryGetProperty("used_percentage", out var p) && p.ValueKind == JsonValueKind.Number)
                used = p.GetDouble();
            resetsAt = ReadDate(w, "resets_at");
        }

        if (used is null)
        {
            percent.Text = "inväntar data";
            bar.Value = 0;
        }
        else
        {
            percent.Text = $"{used.Value:0.#} %";
            bar.Value = Math.Clamp(used.Value, 0, 100);
            bar.Foreground = used.Value >= 90 ? Red : used.Value >= 75 ? Yellow : Green;
        }

        if (resetsAt is null)
        {
            reset.Text = "Återställs: inväntar data";
        }
        else
        {
            var left = resetsAt.Value - DateTimeOffset.Now;
            if (left < TimeSpan.Zero) left = TimeSpan.Zero;
            var when = resetsAt.Value.LocalDateTime.ToString("ddd d MMM HH:mm", Swedish);
            reset.Text = $"Återställs {when} (om {FormatSpan(left)})";
        }
    }

    private void ShowNoData(string message)
    {
        FivePercent.Text = WeekPercent.Text = "inväntar data";
        FiveBar.Value = WeekBar.Value = 0;
        FiveReset.Text = WeekReset.Text = "Återställs: inväntar data";
        Status.Text = message;
        Status.Foreground = Yellow;
    }

    private static DateTimeOffset? ReadDate(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(v.GetString(), out var d) ? d : null;

    private static string FormatSpan(TimeSpan s)
    {
        if (s.TotalDays >= 1) return $"{(int)s.TotalDays}d {s.Hours}h";
        if (s.TotalHours >= 1) return $"{(int)s.TotalHours}h {s.Minutes}m";
        if (s.TotalMinutes >= 1) return $"{(int)s.TotalMinutes}m {s.Seconds}s";
        return $"{Math.Max(0, (int)s.TotalSeconds)}s";
    }
}
