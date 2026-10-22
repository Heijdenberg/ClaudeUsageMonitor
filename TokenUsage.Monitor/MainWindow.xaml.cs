using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;
using TokenUsage.Core;

namespace TokenUsage.Monitor;

public partial class MainWindow : Window
{
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RunsRefreshInterval = TimeSpan.FromSeconds(30);
    internal static readonly CultureInfo Swedish = CultureInfo.GetCultureInfo("sv-SE");


    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(5) };
    private DateTime _runsLoadedAt = DateTime.MinValue;
    private List<RunRow> _allRuns = [];
    private bool _updatingFilter;
    private string? _menuCellText; // the right-clicked cell's value, for "Kopiera cell"
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
        _systemFrame = UseSystemFrame(this);
        UpdateFrame();
    }

    /// <summary>
    /// Sets up a borderless window: maximizing stays within the work area, and on Windows 11 the corners are
    /// rounded with a border in the app's line color. False on Windows 10, where the window keeps its own
    /// square 1 px frame from the XAML.
    /// </summary>
    internal static bool UseSystemFrame(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        HwndSource.FromHwnd(hwnd).AddHook(WndProc);

        const int DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_ROUND = 2, DWMWA_BORDER_COLOR = 34;
        int round = DWMWCP_ROUND, borderColor = 0x00413A37;   // COLORREF 0x00BBGGRR: the theme's Line, #373A41
        if (Native.DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int)) != 0) return false;
        Native.DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref borderColor, sizeof(int));
        return true;
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
            ShowNoData($"Hittar inte {UsagePaths.DatabasePath}. Har TokenUsage.StatusLine körts från Claude Codes statusrad?");
        }
        catch (SqliteException ex)
        {
            // Keep the last values and try again on the next tick.
            SetStatus(LimitCard.Red, "Kunde inte läsa databasen", $"{ex.Message}\nFörsöker igen om 5 s.");
        }
    }

    // ---- Limits tab ----

    private void RefreshLimits(UsageDatabase db)
    {
        var five = db.ReadLimit(UsageDatabase.FiveHour);
        var week = db.ReadLimit(UsageDatabase.SevenDay);
        var now = DateTimeOffset.Now;
        ShowLimit(db, FiveCard, UsageDatabase.FiveHour, five, TimeSpan.FromHours(5), now);
        ShowLimit(db, WeekCard, UsageDatabase.SevenDay, week, TimeSpan.FromDays(7), now);

        var checkedAt = new[] { five?.CheckedAt, week?.CheckedAt }.Max();
        if (checkedAt is null)
        {
            ShowNoData("Databasen har inga gränsvärden än.");
            return;
        }

        // A short line with the details in its tooltip.
        var age = now - checkedAt.Value;
        var details = $"Senast rapporterat {checkedAt.Value.LocalDateTime:HH:mm:ss} ({FormatSpan(age)} sedan).";
        var changedAt = new[] { five?.ChangedAt, week?.ChangedAt }.Max();
        if (changedAt is not null)
            details += $"\nProcenten ändrades senast {changedAt.Value.LocalDateTime:HH:mm} ({FormatSpan(now - changedAt.Value)} sedan).";
        if (age > StaleAfter)
            SetStatus(LimitCard.Yellow, "Ingen session rapporterar – körs Claude Code?", details);
        else
            SetStatus(LimitCard.Green, age < TimeSpan.FromSeconds(10) ? "Uppdaterad nyss" : $"Uppdaterad för {FormatSpan(age)} sedan", details);
    }

    // Claude Code usage logged in the window, which ends at the reset time. Usage elsewhere (claude.ai, the app) isn't in the logs.
    private static void ShowLimit(UsageDatabase db, LimitCard card, string window, StoredLimit? limit, TimeSpan length, DateTimeOffset now)
    {
        if (limit?.Window.ResetsAt is not DateTimeOffset resetsAt)
        {
            card.Show(limit, length, null, null, now);
            return;
        }
        card.Show(limit, length, RunReport.Totals(db, resetsAt - length, resetsAt), db.ReadLimitHistory(window, resetsAt), now);
    }

    private void SetStatus(Brush dot, string text, string details)
    {
        StatusDot.Fill = dot;
        Status.Text = text;
        StatusLine.ToolTip = details;
    }

    private void ShowNoData(string message)
    {
        FiveCard.ShowNoData();
        WeekCard.ShowNoData();
        SetStatus(LimitCard.Yellow, "Ingen data än", message);
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
        MarkRepeatedSessions();
        var maxCost = runs.Max(r => r.Cost) ?? 0;
        foreach (var r in runs) r.CostShare = maxCost > 0 ? (r.Cost ?? 0) / maxCost : 0;

        RunCount.Text = runs.Count.ToString("N0", Swedish);
        ResponseCount.Text = runs.Sum(r => r.Responses).ToString("N0", Swedish);
        CostTotal.Text = string.Format(Swedish, "≈ {0:N2} $", runs.Sum(r => r.Cost ?? 0));
        RunsSummary.Text = $"Uppdaterad {_runsLoadedAt:HH:mm:ss}";
    }

    private void Runs_Sorting(object sender, DataGridSortingEventArgs e) =>
        // The grid sorts after this event; mark the rows once it has.
        Dispatcher.BeginInvoke(MarkRepeatedSessions, DispatcherPriority.Background);

    /// <summary>Fades the session name on rows in the same session as the row above, in the order shown.</summary>
    private void MarkRepeatedSessions()
    {
        string? previous = null;
        foreach (RunRow row in Runs.Items)
        {
            row.SameSessionAsAbove = row.SessionId == previous;
            previous = row.SessionId;
        }
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
        DetailsTitle.Text = run.Details;
    }

    private void Runs_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        // The menu acts on the row under the mouse, so select it first; outside the rows there is no menu.
        if (ItemsControl.ContainerFromElement(Runs, (DependencyObject)e.OriginalSource) is not DataGridRow { Item: RunRow run } row)
        {
            e.Handled = true;
            return;
        }
        row.IsSelected = true;

        var cell = FindParent<DataGridCell>((DependencyObject)e.OriginalSource);
        _menuCellText = cell is null ? null : CellText(cell, run);
        CopyCellItem.Visibility = _menuCellText is null ? Visibility.Collapsed : Visibility.Visible;
        CopyCellItem.Header = cell is null ? "" : $"Kopiera cell ({cell.Column.Header})";
        // Only on the prompt cell, where the text is shortened.
        ShowPromptItem.Visibility = IsPromptColumn(cell?.Column) ? Visibility.Visible : Visibility.Collapsed;

        SessionFilterItem.Header = SelectedSessionId is null
            ? $"Visa bara sessionen {run.Session}  ·  {run.ShortSessionId}"
            : "Visa alla sessioner";
    }

    // The prompt is copied in full, not as shortened in the grid; other cells as shown.
    private static string? CellText(DataGridCell cell, RunRow run) =>
        IsPromptColumn(cell.Column) ? run.PromptText() : FindChild<TextBlock>(cell)?.Text;

    private static bool IsPromptColumn(DataGridColumn? column) =>
        ((column as DataGridBoundColumn)?.Binding as System.Windows.Data.Binding)?.Path.Path == nameof(RunRow.Label);

    private void ShowPrompt_Click(object sender, RoutedEventArgs e)
    {
        if (Runs.SelectedItem is not RunRow run) return;
        // Read from Claude Code's log when opened; shown, never stored.
        new PromptWindow(run.PromptText(), run.Details) { Owner = this }.Show();
    }

    private static T? FindParent<T>(DependencyObject? element) where T : DependencyObject
    {
        while (element is not null and not T)
            element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        return element as T;
    }

    private static T? FindChild<T>(DependencyObject element) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
        {
            var child = VisualTreeHelper.GetChild(element, i);
            if ((child as T ?? FindChild<T>(child)) is T found) return found;
        }
        return null;
    }

    private void CopyCell_Click(object sender, RoutedEventArgs e)
    {
        if (_menuCellText is not null) SetClipboard(_menuCellText);
    }

    private void Runs_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Ctrl+C copies the row with the whole prompt, not just the cells as shown.
        if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
        {
            CopyRow();
            e.Handled = true;
        }
    }

    private void CopyRow_Click(object sender, RoutedEventArgs e) => CopyRow();

    private void CopyRow()
    {
        if (Runs.SelectedItem is RunRow run) SetClipboard(run.ToClipboardText());
    }

    private void SetClipboard(string text)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch (ExternalException ex)   // another program is holding the clipboard
        {
            RunsSummary.Text = $"Kunde inte kopiera: {ex.Message}";
        }
    }

    private void SessionFilterItem_Click(object sender, RoutedEventArgs e)
    {
        if (Runs.SelectedItem is not RunRow run) return;
        var session = SelectedSessionId is null ? run.SessionId : null;
        SessionFilter.SelectedItem = ((IEnumerable<SessionOption>)SessionFilter.ItemsSource).First(o => o.SessionId == session);
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
public sealed class RunRow(RunSummary run) : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    public (string, string?) Key { get; } = (run.SessionId, run.PromptId);
    public string SessionId => run.SessionId;
    public DateTime Start => run.Start.LocalDateTime;
    public string Session => run.SessionTitle ?? "–";
    // The id never changes, unlike the name; the first 8 characters are enough to tell sessions apart.
    public string ShortSessionId => run.SessionId[..8];
    public string SessionToolTip => $"{Session}\nSessions-id: {SessionId}";
    public string Details => string.Join("  ·  ", Start.ToString("ddd d MMM HH:mm", MainWindow.Swedish), Session,
        "session " + SessionId, Duration, string.Format(MainWindow.Swedish, "{0:N2} $", Cost ?? 0));

    /// <summary>Set from the order shown, so a run of rows from one session reads as a group.</summary>
    public bool SameSessionAsAbove
    {
        get => _sameSessionAsAbove;
        set
        {
            if (_sameSessionAsAbove == value) return;
            _sameSessionAsAbove = value;
            PropertyChanged?.Invoke(this, new(nameof(SameSessionAsAbove)));
        }
    }
    private bool _sameSessionAsAbove;

    /// <summary>The price as a share of the largest in the list shown, for the bar behind it.</summary>
    public double CostShare
    {
        get => _costShare;
        set
        {
            if (_costShare == value) return;
            _costShare = value;
            PropertyChanged?.Invoke(this, new(nameof(CostShare)));
        }
    }
    private double _costShare;
    public string Label => run.Label;

    /// <summary>The whole prompt, read from the log (the database holds no prompt text); the label once the log is gone.</summary>
    public string PromptText() => run.Prompt?.ReadText() ?? Label;

    /// <summary>
    /// The row as labelled lines, with the whole prompt and the full session id.
    /// </summary>
    public string ToClipboardText()
    {
        var sv = MainWindow.Swedish;
        return string.Join(Environment.NewLine,
            "Start: " + Start.ToString("ddd d MMM HH:mm", sv),
            $"Session: {Session} ({SessionId})",
            "Prompt: " + PromptText(),
            "Tid: " + Duration,
            string.Format(sv, "Svar: {0:N0}", Responses),
            string.Format(sv, "Utskrivet: {0:N0}", Output),
            string.Format(sv, "Cacheläsning: {0:N0}", CacheRead),
            string.Format(sv, "Cacheskrivning: {0:N0}", CacheWrite),
            "API-pris: " + (Cost is double c ? string.Format(sv, "{0:N2} $", c) : "–"));
    }
    public double DurationSeconds => (run.End - run.Start).TotalSeconds;
    public string Duration => MainWindow.FormatSpan(run.End - run.Start);
    public long Responses => run.Tokens.Responses;
    public long Output => run.Tokens.Output;
    public long CacheRead => run.Tokens.CacheRead;
    public long CacheWrite => run.Tokens.CacheWrite;
    public double? Cost => run.CostUsd;
    public IReadOnlyList<AgentRow> Agents { get; } = run.Agents
        .Select(a => new AgentRow(a, run.Agents.Max(x => x.CostUsd) ?? 0)).ToList();
}

public sealed class AgentRow(AgentUsage agent, double maxCost)
{
    /// <summary>The price as a share of the costliest agent in the run, for the bar behind it.</summary>
    public double CostShare => maxCost > 0 ? (agent.CostUsd ?? 0) / maxCost : 0;
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
