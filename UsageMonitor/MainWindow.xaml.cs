using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;
using UsageCore;

namespace UsageMonitor;

public partial class MainWindow : Window
{
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RunsRefreshInterval = TimeSpan.FromSeconds(30);
    private static readonly CultureInfo Swedish = CultureInfo.GetCultureInfo("sv-SE");

    private static readonly Brush Green = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
    private static readonly Brush Yellow = new SolidColorBrush(Color.FromRgb(0xE0, 0xB0, 0x30));
    private static readonly Brush Red = new SolidColorBrush(Color.FromRgb(0xE0, 0x50, 0x50));

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(5) };
    private DateTime _runsLoadedAt = DateTime.MinValue;

    public MainWindow()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Refresh();
    }

    private bool RunsVisible => Tabs.SelectedIndex == 1;

    private void Refresh()
    {
        try
        {
            using var db = UsageDatabase.OpenReadOnly(UsagePaths.DatabasePath);
            RefreshLimits(db);
            if (RunsVisible && DateTime.Now - _runsLoadedAt > RunsRefreshInterval) LoadRuns(db);
        }
        catch (FileNotFoundException)
        {
            ShowNoData($"Hittar inte {UsagePaths.DatabasePath}. Har UsageStatusLine körts från Claude Codes statusrad?");
        }
        catch (SqliteException ex)
        {
            // Keep the last values and try again on the next tick.
            Status.Text = $"Kunde inte läsa databasen just nu ({ex.Message}). Försöker igen om 5 s.";
        }
    }

    // ---- Limits tab ----

    private void RefreshLimits(UsageDatabase db)
    {
        var five = db.ReadLimit(UsageDatabase.FiveHour);
        var week = db.ReadLimit(UsageDatabase.SevenDay);
        ShowWindow(db, five, TimeSpan.FromHours(5), FiveRing, FiveReset, FiveSpend);
        ShowWindow(db, week, TimeSpan.FromDays(7), WeekRing, WeekReset, WeekSpend);

        var checkedAt = new[] { five?.CheckedAt, week?.CheckedAt }.Max();
        if (checkedAt is null)
        {
            ShowNoData("Databasen har inga gränsvärden än.");
            return;
        }

        var age = DateTimeOffset.Now - checkedAt.Value;
        var text = $"Senast rapporterat {checkedAt.Value.LocalDateTime:HH:mm:ss} ({FormatSpan(age)} sedan).";
        var changedAt = new[] { five?.ChangedAt, week?.ChangedAt }.Max();
        if (changedAt is not null)
            text += $" Procenten ändrades senast {changedAt.Value.LocalDateTime:HH:mm} ({FormatSpan(DateTimeOffset.Now - changedAt.Value)} sedan).";
        if (age > StaleAfter)
        {
            Status.Text = text + "\nIngen session rapporterar – körs Claude Code?";
            Status.Foreground = Yellow;
        }
        else
        {
            Status.Text = text;
            Status.ClearValue(TextBlock.ForegroundProperty);
        }
    }

    private static void ShowWindow(UsageDatabase db, StoredLimit? limit, TimeSpan length, RingGauge ring, TextBlock reset, TextBlock spend)
    {
        var used = limit?.Window.UsedPercentage;
        ring.Value = used;
        if (used is not null)
            ring.RingBrush = used.Value >= 90 ? Red : used.Value >= 75 ? Yellow : Green;

        if (limit?.Window.ResetsAt is not DateTimeOffset resetsAt)
        {
            reset.Text = "Återställs: inväntar data";
            spend.Text = "";
            return;
        }

        var left = resetsAt - DateTimeOffset.Now;
        if (left < TimeSpan.Zero) left = TimeSpan.Zero;
        reset.Text = $"Återställs {resetsAt.LocalDateTime.ToString("ddd d MMM HH:mm", Swedish)}\n(om {FormatSpan(left)})";

        // Claude Code usage logged since the window started. Usage elsewhere (claude.ai, the app) isn't in the logs.
        var (tokens, cost) = RunReport.Totals(db, resetsAt - length);
        spend.Text = string.Format(Swedish, "I fönstret: {0:N0} svar, {1:N0} utskrivna tokens\n≈ {2:N2} $ i API-pris",
            tokens.Responses, tokens.Output, cost ?? 0);
    }

    private void ShowNoData(string message)
    {
        FiveRing.Value = WeekRing.Value = null;
        FiveReset.Text = WeekReset.Text = "Återställs: inväntar data";
        FiveSpend.Text = WeekSpend.Text = "";
        Status.Text = message;
        Status.Foreground = Yellow;
    }

    // ---- Runs tab ----

    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source == Tabs && RunsVisible) ReloadRuns();
    }

    private void Period_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded) ReloadRuns();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => ReloadRuns();

    private void ReloadRuns()
    {
        try
        {
            using var db = UsageDatabase.OpenReadOnly(UsagePaths.DatabasePath);
            LoadRuns(db);
        }
        catch (Exception ex) when (ex is FileNotFoundException or SqliteException)
        {
            RunsSummary.Text = $"Kunde inte läsa databasen: {ex.Message}";
        }
    }

    private void LoadRuns(UsageDatabase db)
    {
        var days = int.Parse((string)((ComboBoxItem)Period.SelectedItem).Tag);
        var since = days == 0 ? DateTimeOffset.UnixEpoch : DateTimeOffset.Now.AddDays(-days);
        var runs = RunReport.Load(db, since).Select(r => new RunRow(r)).ToList();
        _runsLoadedAt = DateTime.Now;

        // Keep the selection and sort order across refreshes.
        var selected = (Runs.SelectedItem as RunRow)?.Key;
        var sort = Runs.Items.SortDescriptions.ToList();
        Runs.ItemsSource = runs;
        foreach (var s in sort) Runs.Items.SortDescriptions.Add(s);
        Runs.SelectedItem = runs.FirstOrDefault(r => r.Key == selected);

        var cost = runs.Sum(r => r.Cost ?? 0);
        RunsSummary.Text = string.Format(Swedish, "{0:N0} körningar, {1:N0} svar, ≈ {2:N2} $ i API-pris. Uppdaterad {3:HH:mm:ss}.",
            runs.Count, runs.Sum(r => r.Responses), cost, DateTime.Now);
    }

    private void Runs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Runs.SelectedItem is not RunRow run)
        {
            Agents.ItemsSource = null;
            DetailsTitle.Text = "Välj en körning för att se vad varje agent och modell kostade.";
            return;
        }
        Agents.ItemsSource = run.Agents;
        DetailsTitle.Text = $"{run.Label}  ·  session {run.SessionId}";
    }

    internal static string FormatSpan(TimeSpan s)
    {
        if (s.TotalDays >= 1) return $"{(int)s.TotalDays}d {s.Hours}h";
        if (s.TotalHours >= 1) return $"{(int)s.TotalHours}h {s.Minutes}m";
        if (s.TotalMinutes >= 1) return $"{(int)s.TotalMinutes}m {s.Seconds}s";
        return $"{Math.Max(0, (int)s.TotalSeconds)}s";
    }
}

/// <summary>A run as shown in the grid.</summary>
public sealed class RunRow(RunSummary run)
{
    public (string, string?) Key { get; } = (run.SessionId, run.PromptId);
    public string SessionId => run.SessionId;
    public DateTime Start => run.Start.LocalDateTime;
    public string Session => run.SessionTitle ?? run.SessionId[..8];
    public string Label => run.Label;
    public double DurationSeconds => (run.End - run.Start).TotalSeconds;
    public string Duration => MainWindow.FormatSpan(run.End - run.Start);
    public long Responses => run.Tokens.Responses;
    public long Output => run.Tokens.Output;
    public long CacheRead => run.Tokens.CacheRead;
    public long CacheWrite => run.Tokens.CacheWrite;
    public double? Cost => run.CostUsd;
    public IReadOnlyList<AgentRow> Agents { get; } = run.Agents.Select(a => new AgentRow(a)).ToList();
}

public sealed class AgentRow(AgentUsage agent)
{
    public string Name => agent.Name;
    public string Model => agent.Model;
    public long Responses => agent.Tokens.Responses;
    public long Input => agent.Tokens.Input;
    public long Output => agent.Tokens.Output;
    public long Thinking => agent.Tokens.Thinking;
    public long CacheRead => agent.Tokens.CacheRead;
    public long CacheWrite => agent.Tokens.CacheWrite;
    public double? Cost => agent.CostUsd;
}
