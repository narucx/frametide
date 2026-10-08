using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Frametide.Core.Bench;
using Frametide.Core.Boost;
using static Frametide.Localization.Loc;
using static Frametide.UiKit;

namespace Frametide.Views;

public partial class BenchmarkPage : UserControl
{
    private sealed record Metric(string Name, bool HigherIsBetter, bool Absolute, string Unit, string Key);

    private static readonly Metric[] Metrics =
    [
        new("Average FPS", true, false, "", "AvgFps"),
        new("1% low", true, false, "", "Low1"),
        new("0.1% low", true, false, "", "Low01"),
        new("Stutters per minute", false, false, "", "StuttersPerMin"),
        new("GPU power", false, false, " W", "GpuW"),
        new("GPU clock", true, false, " MHz", "GpuMHz"),
        new("GPU temperature", false, true, " °C", "GpuC"),
    ];

    private readonly MainWindow _main;
    private readonly DispatcherTimer _status = new() { Interval = TimeSpan.FromSeconds(3) };
    private bool _filling;

    public BenchmarkPage(MainWindow main)
    {
        _main = main;
        InitializeComponent();
        TranslateTree(this);

        RecordCheck.IsChecked = BenchRecorder.RecordEnabled;
        SkipCheck.IsChecked = BenchRecorder.SkipBoostOnce;
        BoostFilter.Items.Add(new ComboBoxItem { Content = T("All sessions"), Tag = "" });
        BoostFilter.Items.Add(new ComboBoxItem { Content = T("Only with boost"), Tag = "with" });
        BoostFilter.Items.Add(new ComboBoxItem { Content = T("Only without boost"), Tag = "without" });
        PeriodFilter.Items.Add(new ComboBoxItem { Content = T("All time"), Tag = 0 });
        PeriodFilter.Items.Add(new ComboBoxItem { Content = T("Last 7 days"), Tag = 7 });
        PeriodFilter.Items.Add(new ComboBoxItem { Content = T("Last 30 days"), Tag = 30 });
        BoostFilter.SelectedIndex = 0;
        PeriodFilter.SelectedIndex = 0;
        foreach (var c in new[] { GameFilter, BoostFilter, PeriodFilter })
            c.SelectionChanged += (_, _) => { if (!_filling) ShowSessions(); };

        RecordCheck.Click += (_, _) => { BenchRecorder.RecordEnabled = RecordCheck.IsChecked == true; UpdateStatus(); };
        SkipCheck.Click += (_, _) =>
        {
            var on = SkipCheck.IsChecked == true;
            if (on && !BoostConfig.Load().AutoBoost)
            {
                SkipCheck.IsChecked = false;
                Info(T("Auto Game Boost is off anyway. For a session without boost simply do not press START."));
                return;
            }
            BenchRecorder.SkipBoostOnce = on;
        };
        PmDownload.Click += async (_, _) =>
        {
            PmDownload.IsEnabled = false;
            var path = await _main.RunAsync("Downloading PresentMon", () => PresentMon.InstallLatestAsync().GetAwaiter().GetResult());
            PmDownload.IsEnabled = true;
            UpdateStatus();
            if (path is not null) Info(T("PresentMon installed. Recording starts with the next game from your list."));
        };

        _status.Tick += (_, _) => UpdateStatus();
        Loaded += (_, _) =>
        {
            BenchStore.Changed += OnChanged;
            _status.Start();
            UpdateStatus();
            ShowSessions();
        };
        Unloaded += (_, _) =>
        {
            BenchStore.Changed -= OnChanged;
            _status.Stop();
        };
    }

    private void OnChanged() => Dispatcher.BeginInvoke(() =>
    {
        SkipCheck.IsChecked = BenchRecorder.SkipBoostOnce;
        ShowSessions();
    });

    private void UpdateStatus()
    {
        var pm = PresentMon.ExePath;
        PmText.Text = pm is not null
            ? T("PresentMon found: {0} (by Intel, open source).", System.IO.Path.GetFileNameWithoutExtension(pm))
            : T("PresentMon is missing. Download it (about 1 MB, official Intel release on GitHub, the signature is checked).");
        PmDownload.Content = pm is not null ? T("Update PresentMon") : T("Download PresentMon");

        var rec = BenchRecorder.Current;
        StatusText.Text = rec is not null ? T("Recording {0} since {1} ({2} min).", rec.Game, rec.Start.ToString("t"), (int)(DateTime.Now - rec.Start).TotalMinutes)
            : pm is null ? T("Not recording: PresentMon is missing.")
            : !BenchRecorder.RecordEnabled ? T("Recording is off.")
            : T("Waiting for a game from the list.");
        StatusText.Foreground = Brush(rec is not null ? "Good" : "Muted");
    }

    /// <summary>Fills the game filter (keeps the selection) and returns the sessions matching all filters.</summary>
    private List<BenchSession> Filter(IReadOnlyList<BenchSession> all)
    {
        _filling = true;
        var game = (GameFilter.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        GameFilter.Items.Clear();
        GameFilter.Items.Add(new ComboBoxItem { Content = T("All games"), Tag = "" });
        foreach (var g in all.GroupBy(s => s.Game).OrderBy(g => g.Key))
            GameFilter.Items.Add(new ComboBoxItem { Content = $"{g.Key} ({g.Count()})", Tag = g.Key });
        GameFilter.SelectedItem = GameFilter.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == game) ?? GameFilter.Items[0];
        _filling = false;

        game = (string)((ComboBoxItem)GameFilter.SelectedItem).Tag;
        var boost = (string)((ComboBoxItem)BoostFilter.SelectedItem).Tag;
        var days = (int)((ComboBoxItem)PeriodFilter.SelectedItem).Tag;
        var since = days > 0 ? DateTime.Now.AddDays(-days) : DateTime.MinValue;
        return all.Where(s => (game == "" || s.Game == game) && (boost == "" || (boost == "with") == s.Boost) && s.Start >= since).ToList();
    }

    private void ShowSessions()
    {
        var all = BenchStore.Sessions();
        var sessions = Filter(all);
        Compare.Children.Clear();
        SessionList.Children.Clear();
        FilterInfo.Text = T("{0} of {1} sessions, {2} min", sessions.Count, all.Count, (int)Math.Round(sessions.Sum(s => s.DurationSec) / 60));
        if (all.Count == 0) { Compare.Children.Add(Text("No sessions yet. Start a game from your Game Boost list and play for at least a minute.", 13, "Muted")); return; }
        if (sessions.Count == 0) { Compare.Children.Add(Text("No sessions match the filter.", 13, "Muted")); return; }

        foreach (var c in BenchStore.Compare(sessions)) ShowComparison(c, sessions);
        foreach (var s in sessions) SessionList.Children.Add(SessionRow(s));
    }

    private void ShowComparison(BenchComparison c, List<BenchSession> sessions)
    {
        Compare.Children.Add(Text(c.Game, 14, bold: true, translate: false));
        string Count(BenchSide? side) => side is null ? "-" : T("{0}x, {1} min", side.Count, side.Minutes);
        var rows = new List<IReadOnlyList<object>> { new object[] { T("Sessions"), Count(c.Without), Count(c.With), "" } };
        foreach (var m in Metrics)
        {
            var a = c.Without?[m.Key];
            var b = c.With?[m.Key];
            string Num(double? v) => v is null ? "-" : m.Key == "StuttersPerMin" ? v.Value.ToString("N1") : v.Value.ToString("N0") + m.Unit;
            rows.Add(new object[] { T(m.Name), Num(a), Num(b), Diff(a, b, m) });
        }
        Compare.Children.Add(Table([T("Value"), T("Without boost"), T("With boost"), T("Difference")], rows));

        if (c.Without is null) Compare.Children.Add(Text("No session without Game Boost yet. Tick 'Next session WITHOUT Game Boost' above and play once.", 12, "Warn", margin: "0,-8,0,14"));
        else if (c.With is null) Compare.Children.Add(Text("No session with Game Boost yet.", 12, "Warn", margin: "0,-8,0,14"));
        if (c.Caps.Count > 0)
            Compare.Children.Add(Text(T("FPS limit detected ({0} FPS): average FPS cannot rise above it. Compare the lows and stutters.", string.Join(" / ", c.Caps)), 12, "Accent", margin: "0,-8,0,14", translate: false));
        if (sessions.FirstOrDefault(s => s.Game == c.Game && s.Limits is not null) is { } last)
        {
            var limits = FormatLimits(last.Limits!);
            var text = limits.Length > 0 ? T("FPS limit settings (last session): {0}", limits)
                : FpsLimits.Unreadable.TryGetValue(last.Exe, out var why) ? T(why)
                : T("No FPS limit found in the game settings.");
            Compare.Children.Add(Text(text, 12, "Muted", margin: "0,-8,0,14", translate: false));
        }
    }

    private static TextBlock Diff(double? without, double? with, Metric m)
    {
        if (without is null || with is null) return Text("-", 13, "Muted", translate: false);
        var d = with.Value - without.Value;
        var text = m.Absolute || without.Value == 0
            ? d.ToString("+0.#;-0.#;0") + m.Unit
            : (d / without.Value * 100).ToString("+0.#;-0.#;0") + " %";
        var good = m.HigherIsBetter ? d > 0 : d < 0;
        return Text(text, 13, Math.Abs(d) < 0.05 ? "Muted" : good ? "Good" : "Bad", bold: true, translate: false);
    }

    private static string FormatLimits(IEnumerable<FpsLimit> limits) => string.Join("  |  ", limits.Select(l =>
        $"{T(l.Source)}: {(l.Fps is null ? T("refresh rate") : l.Fps == 0 ? T("no limit") : $"{l.Fps} FPS")}"));

    private Grid SessionRow(BenchSession s)
    {
        var title = $"{s.Game}  -  {s.Start:g}  -  {Math.Round(s.DurationSec / 60)} min";
        var desc = T("{0} FPS avg  |  1% low {1}  |  0.1% low {2}  |  {3} stutters/min", s.AvgFps.ToString("N0"), s.Low1.ToString("N0"), s.Low01.ToString("N0"), s.StuttersPerMin.ToString("N1"));
        if (s.Hitches is { } h) desc += "  |  " + T("{0} hitches > 100 ms", h);
        if (s.GpuW is { } w) desc += "  |  " + T("GPU {0} W, {1} MHz", w.ToString("N0"), (s.GpuMHz ?? 0).ToString("N0"));
        if (s.AnalysisVersion < FrameAnalysis.Version) desc += "\n" + T("Recorded with the first version: stutters were counted more strictly (loading screens included).");
        if (s.ExcludedSec > 0) desc += "\n" + T("Left out: {0} s outside the game ({1}x alt-tab).", s.ExcludedSec, s.TabOuts ?? 0);
        if (s.Limits is { Count: > 0 } limits) desc += "\n" + T("FPS limit: {0}", FormatLimits(limits));

        var spark = Sparkline(s.Series);
        if (s.Series.Length > 1) spark.ToolTip = T("FPS over the session (max {0})", s.Series.Max());
        var right = new List<UIElement> { spark };
        if (s.EffectiveCap is { } cap) right.Add(Badge(T("Limit {0}", cap), "Accent", "AccentSoft"));
        right.Add(s.Boost ? Badge("Boost", "Good", "GoodSoft") : Badge("No boost", "Muted", "Panel3"));
        right.Add(Button("Delete", () =>
        {
            if (Ask(T("Delete this session?")) != MessageBoxResult.Yes) return;
            BenchStore.Delete(s.Id);
        }));
        return Row(s.Boost ? "Good" : "Muted", title, desc, right, "0,0,0,10", translate: false);
    }
}
