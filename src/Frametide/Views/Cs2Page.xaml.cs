using System.Windows;
using System.Windows.Controls;
using Frametide.Core.Games;
using Frametide.Core.Gpu;
using Frametide.Core.Hardware;
using static Frametide.Localization.Loc;
using static Frametide.UiKit;

namespace Frametide.Views;

public partial class Cs2Page : UserControl
{
    private readonly MainWindow _main;
    private Cs2User? _user;
    private IReadOnlyList<RelayRegion> _regions = [];

    public Cs2Page(MainWindow main)
    {
        _main = main;
        InitializeComponent();
        TranslateTree(this);

        Reload.Click += async (_, _) => await LoadAsync();
        OpenCfg.Click += (_, _) =>
        {
            if (_user is not null && System.IO.Directory.Exists(_user.CfgDir)) System.Diagnostics.Process.Start("explorer.exe", _user.CfgDir)?.Dispose();
        };
        NvApply.Click += async (_, _) =>
        {
            await _main.RunAsync("NVIDIA profile for CS2", NvidiaProfiles.OptimizeCs2);
            await LoadNvAsync();
        };
        SrvLoad.Click += async (_, _) =>
        {
            SrvLoad.IsEnabled = false;
            _regions = await _main.RunAsync("Loading CS2 servers", () => ServerBlocker.LoadAsync().GetAwaiter().GetResult()) ?? [];
            SrvLoad.IsEnabled = true;
            ShowRegions();
        };
        SrvApply.Click += async (_, _) =>
        {
            var block = SrvList.Children.OfType<CheckBox>().Where(c => c.IsChecked == true).Select(c => ((RelayRegion)c.Tag).Code)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var regions = _regions;
            await _main.RunAsync("Server blocker", () => ServerBlocker.Apply(regions, block));
            Info(T("{0} region(s) blocked.", block.Count));
        };
        SrvClear.Click += async (_, _) =>
        {
            await _main.RunAsync("Server blocker", ServerBlocker.UnblockAll);
            foreach (var c in SrvList.Children.OfType<CheckBox>()) c.IsChecked = false;
        };

        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        var (dir, users) = await _main.RunAsync("Reading CS2", () => (Cs2.GameDir(), Cs2.Users()));
        users ??= [];
        _user = Cs2.CurrentUser(users);
        var account = _user is null ? T("No Steam account with a CS2 configuration found")
            : _user.Active ? T("Shown Steam account: {0} (currently logged in)", _user.Id)
            : T("Shown Steam account: {0} (most recently used, Steam is not running)", _user.Id);
        if (users.Count > 1) account += " " + T("({0} accounts with CS2 data on this PC)", users.Count);
        PathText.Text = dir is null ? T("CS2 not found (Steam libraries searched).") : $"{dir}\n{account}";
        OpenCfg.IsEnabled = _user is not null;

        LaunchText.Text = string.IsNullOrWhiteSpace(_user?.LaunchOptions) ? T("(no launch options set, good)") : _user!.LaunchOptions;
        LaunchHints.Children.Clear();
        foreach (var h in Cs2.AnalyzeLaunchOptions(_user?.LaunchOptions))
        {
            var dot = h.Level switch { HintLevel.Bad => "Bad", HintLevel.Warn => "Warn", HintLevel.Ok => "Good", _ => "Accent" };
            LaunchHints.Children.Add(Row(dot, h.Option, T(h.Text), [], "0,0,0,6", translate: false));
        }

        VideoGrid.Children.Clear();
        IReadOnlyList<VideoSetting> video = _user is null ? [] : Cs2.VideoSettings(_user.VideoFile);
        if (video.Count == 0) VideoGrid.Children.Add(Text("cs2_video.txt not found (start CS2 once).", 13, "Muted"));
        foreach (var v in video)
            VideoGrid.Children.Add(Row(null, T(v.Name), null, [Text(v.Translate ? T(v.Value) : v.Value, 13, "Accent", bold: true, translate: false)], "0,0,24,6", translate: false, boldTitle: false));

        await LoadNvAsync();
    }

    private async Task LoadNvAsync()
    {
        var hasNvidia = await Task.Run(() => Nvidia.GetInfo() is not null);
        NvCard.Visibility = hasNvidia ? Visibility.Visible : Visibility.Collapsed;
        if (!hasNvidia) return;
        bool? optimized = null;
        await Task.Run(() =>
        {
            try { optimized = NvidiaProfiles.Cs2Optimized(); }
            catch (InvalidOperationException) { }
        });
        NvState.Text = optimized switch { true => T("Applied."), false => T("Not applied yet."), _ => T("NVIDIA driver profiles are not available.") };
        NvState.Foreground = Brush(optimized == true ? "Good" : "Muted");
        NvApply.IsEnabled = optimized == false;
    }

    private void ShowRegions()
    {
        SrvList.Children.Clear();
        foreach (var r in _regions)
        {
            var ping = r.PingMs is { } ms ? $"{ms} ms" : T("no ping");
            SrvList.Children.Add(new CheckBox { Content = $"{r.Description} ({r.Code})  -  {ping}", IsChecked = r.Blocked, Tag = r, Margin = new Thickness(0, 4, 16, 4) });
        }
        SrvApply.IsEnabled = _regions.Count > 0;
    }
}
