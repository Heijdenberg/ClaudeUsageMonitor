using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;
using ClaudeUsage.Core;

namespace ClaudeUsage.Monitor;

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
    private List<RunRow> _allRuns = [];
    private bool _updatingFilter;
    private bool _systemFrame;   // Windows 11 rounds the corners and draws the border itself

    public MainWindow()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Refresh();
    }

    // ---- Title bar ----

    private void Minimize_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

    private void MaximizeRestore_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);

    /// <summary>Also covers maximizing by double-click on the title bar, Aero Snap and Win+Up.</summary>
    private void Window_StateChanged(object? sender, EventArgs e)
    {
        var maximized = WindowState == WindowState.Maximized;
        MaximizeButton.Content = maximized ? "\uE923" : "\uE922";   // Segoe Fluent Icons: restore / maximize
        MaximizeButton.ToolTip = maximized ? "Återställ" : "Maximera";
        UpdateFrame();
    }

    private void UpdateFrame() =>
        WindowFrame.BorderThickness = new Thickness(_systemFrame || WindowState == WindowState.Maximized ? 0 : 1);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(hwnd).AddHook(WndProc);

        // Windows 11 doesn't round borderless windows by default; ask for it, with a border in the app's line color.
        // Fails on Windows 10, which keeps the square 1 px frame from the XAML.
        const int DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_ROUND = 2, DWMWA_BORDER_COLOR = 34;
        int round = DWMWCP_ROUND, borderColor = 0x003A3A3A;   // COLORREF 0x00BBGGRR
        _systemFrame = Native.DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int)) == 0;
        if (_systemFrame) Native.DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref borderColor, sizeof(int));
        UpdateFrame();
    }

    /// <summary>
    /// Without the system frame, Windows would maximize the window over the whole monitor, covering the
    /// taskbar and pushing the edges off-screen. Limit the maximized size to the monitor's work area instead.
    /// </summary>
    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_GETMINMAXINFO = 0x0024;
        if (msg != WM_GETMINMAXINFO) return IntPtr.Zero;

        var monitor = Native.MonitorFromWindow(hwnd, Native.MONITOR_DEFAULTTONEAREST);
        var info = new Native.MonitorInfo { Size = Marshal.SizeOf<Native.MonitorInfo>() };
        if (monitor == IntPtr.Zero || !Native.GetMonitorInfo(monitor, ref info)) return IntPtr.Zero;

        // Positions are relative to the monitor; WPF still applies MinWidth/MinHeight afterwards.
        var mmi = Marshal.PtrToStructure<Native.MinMaxInfo>(lParam);
        mmi.MaxPosition = new Native.Point { X = info.Work.Left - info.Monitor.Left, Y = info.Work.Top - info.Monitor.Top };
        mmi.MaxSize = new Native.Point { X = info.Work.Right - info.Work.Left, Y = info.Work.Bottom - info.Work.Top };
        Marshal.StructureToPtr(mmi, lParam, false);
        return IntPtr.Zero;
    }

    private static class Native
    {
        public const int MONITOR_DEFAULTTONEAREST = 2;

        [StructLayout(LayoutKind.Sequential)] public struct Point { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        public struct MinMaxInfo { public Point Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }

        [StructLayout(LayoutKind.Sequential)]
        public struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }

        [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);
        [DllImport("user32.dll")] public static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
        [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
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
            ShowNoData($"Hittar inte {UsagePaths.DatabasePath}. Har ClaudeUsage.StatusLine körts från Claude Codes statusrad?");
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
        _allRuns = RunReport.Load(db, since).Select(r => new RunRow(r)).ToList();
        _runsLoadedAt = DateTime.Now;
        UpdateSessionFilter();
        ShowRuns();
    }

    /// <summary>The sessions in the loaded period, newest first, keeping the current choice.</summary>
    private void UpdateSessionFilter()
    {
        var selected = SelectedSessionId;
        var options = _allRuns
            .GroupBy(r => r.SessionId)
            .OrderByDescending(g => g.Max(r => r.Start))
            .Select(g => new SessionOption(g.Key, $"{g.First().Session}  ·  {g.First().ShortSessionId}"))
            .Prepend(new SessionOption(null, "Alla sessioner"))
            .ToList();
        // A chosen session with no runs in this period stays chosen, so changing the period doesn't drop the filter.
        if (selected is not null && options.All(o => o.SessionId != selected))
            options.Insert(1, new SessionOption(selected, $"{selected[..8]} (inga körningar i perioden)"));

        _updatingFilter = true;
        SessionFilter.ItemsSource = options;
        SessionFilter.SelectedItem = options.First(o => o.SessionId == selected);
        _updatingFilter = false;
    }

    private string? SelectedSessionId => (SessionFilter.SelectedItem as SessionOption)?.SessionId;

    private void SessionFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_updatingFilter) ShowRuns();
    }

    private void ShowRuns()
    {
        var session = SelectedSessionId;
        var runs = session is null ? _allRuns : _allRuns.Where(r => r.SessionId == session).ToList();

        // Keep the selection and sort order across refreshes.
        var selected = (Runs.SelectedItem as RunRow)?.Key;
        var sort = Runs.Items.SortDescriptions.ToList();
        Runs.ItemsSource = runs;
        foreach (var s in sort) Runs.Items.SortDescriptions.Add(s);
        Runs.SelectedItem = runs.FirstOrDefault(r => r.Key == selected);

        var cost = runs.Sum(r => r.Cost ?? 0);
        RunsSummary.Text = string.Format(Swedish, "{0:N0} körningar, {1:N0} svar, ≈ {2:N2} $ i API-pris. Uppdaterad {3:HH:mm:ss}.",
            runs.Count, runs.Sum(r => r.Responses), cost, _runsLoadedAt);
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

/// <summary>An entry in the session filter; a null id means all sessions.</summary>
public sealed record SessionOption(string? SessionId, string Text);

/// <summary>A run as shown in the grid.</summary>
public sealed class RunRow(RunSummary run)
{
    public (string, string?) Key { get; } = (run.SessionId, run.PromptId);
    public string SessionId => run.SessionId;
    public DateTime Start => run.Start.LocalDateTime;
    public string Session => run.SessionTitle ?? "–";
    // The id never changes, unlike the name; the first 8 characters are enough to tell sessions apart.
    public string ShortSessionId => run.SessionId[..8];
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
