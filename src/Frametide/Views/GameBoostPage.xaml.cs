using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Frametide.Core.Boost;
using Frametide.Core.Gpu;
using Frametide.Core.Hardware;
using Frametide.Core.Windows;
using Frametide.Services;
using static Frametide.Localization.Loc;
using static Frametide.UiKit;

namespace Frametide.Views;

public partial class GameBoostPage : UserControl
{
    private static readonly (string Text, GamePriority Value)[] Priorities =
        [("Normal", GamePriority.Normal), ("Above normal", GamePriority.AboveNormal), ("High", GamePriority.High)];

    private static readonly (string Text, CoreAffinity Value)[] Affinities =
        [("All cores", CoreAffinity.All), ("Without core 0", CoreAffinity.NoCore0), ("P-cores only", CoreAffinity.PCores)];

    private readonly MainWindow _main;
    private readonly BoostConfig _cfg = BoostConfig.Load();
    private bool _loading = true;
    private bool _fillingGpu;

    public GameBoostPage(MainWindow main)
    {
        _main = main;
        InitializeComponent();
        TranslateTree(this);

        DnsCheck.IsChecked = _cfg.FlushDns;
        ShaderCheck.IsChecked = _cfg.ClearShaderCache;
        KillBox.Text = string.Join(Environment.NewLine, _cfg.KillList);
        SuspendBox.Text = string.Join(Environment.NewLine, _cfg.SuspendList);
        TrayCheck.IsChecked = App.CloseToTray;
        AutostartCheck.IsChecked = Autostart.Wanted && App.ProtectedInstall;
        // Per-user installs (and dev builds) must not be started elevated at sign-in: see LogonTask.
        AutostartCheck.IsEnabled = App.ProtectedInstall;
        AutostartHint.Visibility = App.ProtectedInstall ? Visibility.Collapsed : Visibility.Visible;
        ShowGames();
        UpdateState();

        Toggle.Click += async (_, _) =>
        {
            Toggle.IsEnabled = false;
            if (!GameBoost.IsActive) await SaveAsync();
            await BoostRunner.ToggleAsync();
            Toggle.IsEnabled = true;
        };
        AutoCheck.Click += async (_, _) =>
        {
            var on = AutoCheck.IsChecked == true;
            await _main.RunAsync("Auto Game Boost", () => GameBoost.SetAutoBoost(on));
        };
        TrayCheck.Click += (_, _) => App.CloseToTray = TrayCheck.IsChecked == true;
        AutostartCheck.Click += async (_, _) =>
        {
            var on = AutostartCheck.IsChecked == true;
            var ok = await _main.RunAsync("Start with Windows", () => { Autostart.Set(on, Environment.ProcessPath!); return true; });
            if (!ok) AutostartCheck.IsChecked = !on;
        };
        PlanCombo.SelectionChanged += async (_, _) =>
        {
            if (_loading || PlanCombo.SelectedItem is not ComboBoxItem { Tag: string plan }) return;
            _cfg.PowerPlan = plan;
            await SaveAsync();
        };
        DnsCheck.Click += async (_, _) => { _cfg.FlushDns = DnsCheck.IsChecked == true; await SaveAsync(); };
        ShaderCheck.Click += async (_, _) => { _cfg.ClearShaderCache = ShaderCheck.IsChecked == true; await SaveAsync(); };
        GpuKeepCheck.IsChecked = _cfg.GpuKeepAfterStop;
        GpuKeepCheck.Click += async (_, _) => { _cfg.GpuKeepAfterStop = GpuKeepCheck.IsChecked == true; await SaveAsync(); };
        GpuCombo.SelectionChanged += async (_, _) =>
        {
            if (_fillingGpu || GpuCombo.SelectedItem is not ComboBoxItem { Tag: string profile }) return;
            _cfg.GpuProfile = profile;
            await SaveAsync();
        };
        KillBox.LostFocus += async (_, _) => await SaveAsync();
        SuspendBox.LostFocus += async (_, _) => await SaveAsync();

        AddGame.Click += async (_, _) => await AddTypedGameAsync();
        NewGame.KeyDown += async (_, e) => { if (e.Key == Key.Enter) await AddTypedGameAsync(); };
        ScanGames.Click += async (_, _) => await ScanAsync();
        ScanClose.Click += (_, _) => ScanBox.Visibility = Visibility.Collapsed;
        ScanAdd.Click += async (_, _) =>
        {
            var picked = ScanList.Children.OfType<CheckBox>().Where(c => c.IsChecked == true).Select(c => (InstalledGame)c.Tag).ToList();
            foreach (var g in picked) _cfg.Games.Add(new GameEntry { Name = g.Name, Exe = g.Exe });
            ScanBox.Visibility = Visibility.Collapsed;
            ShowGames();
            await SaveAsync();
            Info(T("{0} game(s) added.", picked.Count));
        };

        LassoRefresh.Click += async (_, _) => await CheckLassoAsync();
        LassoFix.Click += async (_, _) =>
        {
            if (Ask(T("Process Lasso is stopped briefly, its rules (affinity/priority) for your games are removed, then Lasso starts again. A backup of its configuration goes to the data folder. Continue?")) != MessageBoxResult.Yes) return;
            var exes = _cfg.Games.Select(g => g.Exe).ToList();
            await _main.RunAsync("Removing Lasso rules", () => ProcessLasso.RemoveGameRules(exes));
            await CheckLassoAsync();
        };

        Loaded += async (_, _) =>
        {
            GameBoost.Changed += OnBoostChanged;
            GpuProfiles.Changed += OnGpuProfilesChanged;
            UpdateState();
            await LoadPlansAsync();
            _hasNvidia = await _main.RunAsync("Reading the GPU", () => Nvidia.GetInfo() is not null);
            FillGpuProfiles();
            await CheckLassoAsync();
        };
        Unloaded += (_, _) =>
        {
            GameBoost.Changed -= OnBoostChanged;
            GpuProfiles.Changed -= OnGpuProfilesChanged;
        };
    }

    private bool _hasNvidia;

    private void OnBoostChanged() => Dispatcher.BeginInvoke(UpdateState);

    private void OnGpuProfilesChanged() => Dispatcher.BeginInvoke(FillGpuProfiles);

    /// <summary>GPU profile on START: only with an NVIDIA GPU; a deleted profile falls back to "None".</summary>
    private void FillGpuProfiles()
    {
        foreach (var e in new FrameworkElement[] { GpuLabel, GpuCombo, GpuKeepCheck }) e.Visibility = _hasNvidia ? Visibility.Visible : Visibility.Collapsed;
        _fillingGpu = true;
        GpuCombo.Items.Clear();
        GpuCombo.Items.Add(new ComboBoxItem { Content = T("None"), Tag = "" });
        foreach (var p in GpuProfiles.All()) GpuCombo.Items.Add(new ComboBoxItem { Content = p.Name, Tag = p.Name });
        GpuCombo.SelectedItem = GpuCombo.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == _cfg.GpuProfile) ?? GpuCombo.Items[0];
        _fillingGpu = false;
    }

    private void UpdateState()
    {
        var state = GameBoost.State;
        StateText.Text = state is null ? T("Game Boost is off") : T("Game Boost running since {0}", state.StartedAt.ToString("HH:mm"));
        Toggle.Content = state is null ? "START" : "STOP";
        Toggle.Style = (Style)FindResource(state is null ? "Primary" : "Danger");
        AutoCheck.IsChecked = BoostConfig.Load().AutoBoost;
    }

    private async Task LoadPlansAsync()
    {
        var plans = await _main.RunAsync("Reading power plans", PowerCfg.GetSchemes) ?? [];
        _loading = true;
        PlanCombo.Items.Clear();
        PlanCombo.Items.Add(new ComboBoxItem { Content = T("Do not change"), Tag = BoostConfig.KeepPowerPlan });
        foreach (var p in plans) PlanCombo.Items.Add(new ComboBoxItem { Content = p.Name, Tag = p.Guid });
        PlanCombo.SelectedItem = PlanCombo.Items.Cast<ComboBoxItem>().FirstOrDefault(i => string.Equals((string)i.Tag, _cfg.PowerPlan, StringComparison.OrdinalIgnoreCase))
                                 ?? PlanCombo.Items[0];
        _loading = false;
    }

    private void ShowGames()
    {
        GameList.Children.Clear();
        if (_cfg.Games.Count == 0) GameList.Children.Add(Text("No games yet. Scan your installed games or add one by its .exe name.", 13, "Muted", margin: "0,0,0,4"));
        foreach (var g in _cfg.Games)
        {
            var prio = Combo(Priorities, g.Priority, v => g.Priority = v);
            var aff = Combo(Affinities, g.Affinity, v => g.Affinity = v);
            var remove = Button("Remove", async () =>
            {
                _cfg.Games.Remove(g);
                ShowGames();
                await SaveAsync();
            });
            GameList.Children.Add(Row("Accent", g.Name, g.Exe, [prio, aff, remove], "0,0,0,8", translate: false));
        }
    }

    private ComboBox Combo<TValue>((string Text, TValue Value)[] options, TValue current, Action<TValue> set)
    {
        var box = new ComboBox { Width = 170, Margin = new Thickness(8, 0, 0, 0) };
        foreach (var (text, value) in options) box.Items.Add(new ComboBoxItem { Content = T(text), Tag = value });
        box.SelectedItem = box.Items.Cast<ComboBoxItem>().First(i => Equals(i.Tag, current));
        box.SelectionChanged += async (_, _) =>
        {
            if (box.SelectedItem is not ComboBoxItem { Tag: TValue v }) return;
            set(v);
            await SaveAsync();
        };
        return box;
    }

    private async Task AddTypedGameAsync()
    {
        var exe = NewGame.Text.Trim();
        if (exe.Length == 0) return;
        if (!exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) exe += ".exe";
        if (!BoostConfig.IsValidExe(exe)) { Info(T("Enter the file name of the game, e.g. game.exe (no folder)."), MessageBoxImage.Warning); return; }
        if (!_cfg.Games.Any(g => g.Exe.Equals(exe, StringComparison.OrdinalIgnoreCase)))
            _cfg.Games.Add(new GameEntry { Name = System.IO.Path.GetFileNameWithoutExtension(exe), Exe = exe });
        NewGame.Text = "";
        ShowGames();
        await SaveAsync();
    }

    private async Task ScanAsync()
    {
        ScanGames.IsEnabled = false;
        var found = await _main.RunAsync("Scanning games", GameScanner.Scan) ?? [];
        ScanGames.IsEnabled = true;
        var have = _cfg.Games.Select(g => g.Exe).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var fresh = found.Where(g => !have.Contains(g.Exe)).ToList();
        ScanList.Children.Clear();
        if (fresh.Count == 0) ScanList.Children.Add(Text("No new games found. All installed games are already in the list.", 13, "Good"));
        foreach (var g in fresh)
            ScanList.Children.Add(new CheckBox { Content = $"{g.Name}  -  {g.Exe}  ({g.Source})", IsChecked = true, Tag = g, Margin = new Thickness(0, 4, 16, 4) });
        ScanAdd.IsEnabled = fresh.Count > 0;
        ScanBox.Visibility = Visibility.Visible;
    }

    /// <summary>Takes the text boxes over and saves everything.</summary>
    private async Task SaveAsync()
    {
        if (_loading) return;
        _cfg.KillList = BoostConfig.ParseExeList(KillBox.Text);
        _cfg.SuspendList = BoostConfig.ParseExeList(SuspendBox.Text);
        var cfg = _cfg;
        await _main.RunAsync("Saving", () => GameBoost.SaveConfig(cfg));
        SavedText.Visibility = Visibility.Visible;
    }

    private async Task CheckLassoAsync()
    {
        var exes = _cfg.Games.Select(g => g.Exe).ToList();
        var state = await _main.RunAsync("Checking Process Lasso", () => ProcessLasso.Check(exes));
        if (state is null) return;
        LassoConflicts.Children.Clear();
        LassoCard.Visibility = state.Installed || state.HasConfig ? Visibility.Visible : Visibility.Collapsed;
        var run = state.Running ? T("is running") : T("is installed but not running");
        LassoFix.IsEnabled = state.Conflicts.Any(c => c.Rule != LassoRule.GamingMode);
        if (state.Conflicts.Count == 0)
        {
            LassoText.Text = T("Process Lasso {0}, but has no rules for your games. No conflict.", run);
            return;
        }
        LassoText.Text = T("Process Lasso {0} and has its own rules for your games. They collide with Game Boost (whoever sets last wins). Either remove Process Lasso, Game Boost covers it, or remove only the game rules here (a backup is created).", run);
        foreach (var c in state.Conflicts)
        {
            var text = c.Rule switch
            {
                LassoRule.Affinity => T("CPU affinity for {0}: cores {1}", c.Target, c.Value),
                LassoRule.Priority => T("Priority for {0}: {1}", c.Target, c.Value),
                _ => T("Lasso gaming mode active (power plan: {0})", c.Value),
            };
            LassoConflicts.Children.Add(Row("Warn", text, null, [], "0,0,0,4", translate: false));
        }
    }
}
