using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Frametide.Core.Boost;
using Frametide.Core.Gpu;
using Frametide.Core.Hardware;
using static Frametide.Localization.Loc;
using static Frametide.UiKit;

namespace Frametide.Views;

public partial class GpuPage : UserControl
{
    private readonly MainWindow _main;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private GpuInfo? _info;
    private GpuTestProgress? _uv;
    private GpuTestProgress? _test;
    private int _roundsShown = -1;

    public GpuPage(MainWindow main)
    {
        _main = main;
        InitializeComponent();
        TranslateTree(this);

        ManOffset.ValueChanged += (_, _) => ManOffsetText.Text = $"{(int)ManOffset.Value:+#;-#;0} MHz";
        ManOffsetText.Text = "0 MHz";
        ManName.Text = T("My profile");
        ManPower.ValueChanged += (_, _) => ManPowerText.Text = $"{(int)ManPower.Value} W";
        ManApply.Click += async (_, _) => await ApplyManualAsync();
        ManReset.Click += async (_, _) =>
        {
            await _main.RunAsync("GPU to default", () => { GpuTuning.Reset(); GpuTuning.SetActive(""); });
            await LoadAsync();
        };
        ManSave.Click += (_, _) =>
        {
            var name = GpuProfiles.CleanName(ManName.Text);
            if (name.Length == 0 || _info is null) return;
            int.TryParse(ManMax.Text, out var max);
            GpuProfiles.Save(new GpuProfile
            {
                Name = name, MaxClock = Math.Max(0, max), OffsetMHz = (int)ManOffset.Value, PowerLimitW = PowerLimitOrZero(),
                Gpu = _info.Name, Driver = _info.Driver, Created = DateTime.Now,
            });
        };
        UvStart.Click += (_, _) => StartUndervolt();
        UvCancel.Click += (_, _) => GpuTests.Cancel();
        TestCancel.Click += (_, _) => GpuTests.Cancel();
        NvReload.Click += async (_, _) => await LoadNvAsync();
        NvOptimize.Click += async (_, _) =>
        {
            var globalNormal = NvGlobalCheck.IsChecked == true;
            if (await _main.RunAsync("NVIDIA profiles", () => { NvidiaProfiles.Optimize(globalNormal); return true; }))
                Info(T("NVIDIA profiles optimized. Takes effect the next time each game starts."));
            await LoadNvAsync();
        };
        NvUndo.Click += async (_, _) =>
        {
            if (Ask(T("Restore the NVIDIA profiles to the values before Frametide changed them?")) != MessageBoxResult.Yes) return;
            await _main.RunAsync("NVIDIA profiles", NvidiaProfiles.Undo);
            await LoadNvAsync();
        };

        _timer.Tick += (_, _) => Tick();
        Loaded += async (_, _) =>
        {
            GpuProfiles.Changed += OnProfilesChanged;
            GpuTuning.Changed += OnProfilesChanged;
            _timer.Start();
            await LoadAsync();
        };
        Unloaded += (_, _) =>
        {
            GpuProfiles.Changed -= OnProfilesChanged;
            GpuTuning.Changed -= OnProfilesChanged;
            _timer.Stop();
        };
    }

    private void OnProfilesChanged() => Dispatcher.BeginInvoke(ShowProfiles);

    private async Task LoadAsync()
    {
        var (info, snap) = await _main.RunAsync("Reading the GPU", () => (Nvidia.GetInfo(), Nvidia.GetSnapshot()));
        _info = info;
        if (info is null)
        {
            GpuName.Text = T("No NVIDIA GPU found. GPU tuning is only available for NVIDIA.");
            foreach (var card in new[] { UvCard, ManualCard, ProfilesCard, NvCard }) card.Visibility = Visibility.Collapsed;
            return;
        }
        _ = LoadNvAsync();
        GpuName.Text = T("{0}  |  Driver {1}  |  Offset range {2} to +{3} MHz", info.Name, info.Driver, info.OffsetMinMHz, info.OffsetMaxMHz);
        if (!info.OffsetSupported) { UvStart.IsEnabled = false; Show(UvStatus, T("The driver does not support clock offsets through NVML.")); }
        ManPower.Minimum = info.PowerMinW;
        ManPower.Maximum = info.PowerMaxW;
        ManPower.Value = snap?.PowerLimitW ?? info.PowerDefaultW;
        ManPowerText.Text = $"{(int)ManPower.Value} W";
        // Continue showing a test that runs while the page was closed.
        if (GpuTests.Running && GpuTests.Current is { } running && _uv is null && _test is null) _uv = running;
        ShowProfiles();
        Tick();
    }

    /// <summary>Reads the driver profiles. Errors (e.g. an old driver) are shown in the card, not as a message box.</summary>
    private async Task LoadNvAsync()
    {
        NvList.Children.Clear();
        NvStatus? st = null;
        string? error = null;
        await Task.Run(() =>
        {
            try { st = NvidiaProfiles.Status(); }
            catch (InvalidOperationException e) { error = e.Message; }
        });
        if (st is null)
        {
            NvList.Children.Add(Text(T("NVIDIA driver profiles are not available: {0}", error), 12, "Muted", translate: false));
            NvOptimize.IsEnabled = NvUndo.IsEnabled = false;
            return;
        }
        NvList.Children.Add(Row(st.GlobalPState == 1 ? "Warn" : "Good", T("Global profile"),
            T("Power management: {0}  |  Shader cache: {1}  |  V-Sync: {2}", PowerText(st.GlobalPState), ShaderText(st.GlobalShaderCache), VsyncText(st.GlobalVsync)),
            [], "0,0,0,10", translate: false));
        foreach (var g in st.Games)
        {
            var desc = g.Profile is null
                ? T("No driver profile for {0} yet. Frametide creates 'Frametide - {1}'.", g.Exe, g.Game)
                : T("Profile '{0}': power management {1}, low latency mode {2}", g.Profile, PowerText(g.PState), PrerenderText(g.Prerender));
            UIElement[] right = g.Optimized ? [Badge("Optimized", "Good", "GoodSoft")] : [];
            NvList.Children.Add(Row(g.Optimized ? "Good" : "Muted", g.Game, desc, right, "0,0,0,8", translate: false));
        }
        NvOptimize.IsEnabled = st.Games.Count > 0;
        NvUndo.IsEnabled = st.HasUndo;
    }

    private static string PowerText(uint? v) => v switch
    {
        null => T("Driver default"), 0 => "Adaptive", 1 => T("Prefer maximum performance"), 2 => T("Driver controlled"),
        3 => T("Prefer consistent performance"), 5 => "Normal", _ => v.ToString()!,
    };

    private static string PrerenderText(uint? v) => v switch { null => T("Driver default"), 0 => T("Off (app decides)"), 1 => T("On"), _ => T("{0} frames", v) };

    private static string ShaderText(uint? v) => v switch { null => T("Driver default"), uint.MaxValue => T("Unlimited"), 0 => T("Off"), _ => $"{v} MB" };

    private static string VsyncText(uint? v) => v switch
    {
        null => T("Driver default"), 0x08416747 => T("Forced off"), NvidiaProfiles.VsyncForcedOn => T("Forced on"), 0x60925292 => T("App decides"), _ => $"0x{v:X}",
    };

    private void Tick()
    {
        if (_info is not null && Nvidia.GetSnapshot() is { } s)
        {
            LiveClock.Text = $"{s.ClockMHz} MHz";
            LiveTemp.Text = $"{s.TempC} °C";
            LivePower.Text = $"{s.PowerW:N0} / {s.PowerLimitW} W";
            LiveUtil.Text = $"{s.Util} %";
            LiveMem.Text = $"{s.MemClockMHz} MHz";
            LiveOffset.Text = $"{s.OffsetMHz:+#;-#;0} MHz";
            LiveVolt.Text = s.VoltageV > 0 ? $"{s.VoltageV:N3} V" : "-";
        }
        UpdateUndervolt();
        UpdateTest();
    }

    private void UpdateTuneState()
    {
        if (_info is null) return;
        var (kind, name) = GpuTuning.Active();
        TuneState.Text = kind switch { TuneKind.Profile => name, TuneKind.Manual => T("Manual"), TuneKind.Unknown => T("Changed by another tool"), _ => T("Default") };
        TuneState.Foreground = Brush(kind == TuneKind.Default ? "Text" : "Good");
    }

    /// <summary>Status lines take no space while empty.</summary>
    private static void Show(TextBlock line, string text)
    {
        line.Text = text;
        line.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private int PowerLimitOrZero() => _info is not null && (int)ManPower.Value == _info.PowerDefaultW ? 0 : (int)ManPower.Value;

    private bool Busy()
    {
        if (GpuTests.Running) { Info(T("A GPU test is already running."), MessageBoxImage.Warning); return true; }
        if (GameBoost.IsActive) { Info(T("Please stop Game Boost first."), MessageBoxImage.Warning); return true; }
        return false;
    }

    private async Task ApplyManualAsync()
    {
        int.TryParse(ManMax.Text, out var max);
        var offset = (int)ManOffset.Value;
        var power = PowerLimitOrZero();
        if (offset > 0 && max <= 0 && Ask(T("A positive offset without a clock lock is an overclock, not an undervolt. Apply anyway?"), icon: MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await _main.RunAsync("Applying GPU tuning", () => { GpuTuning.Apply(Math.Max(0, max), offset, power); GpuTuning.SetActive(GpuTuning.ManualMarker); });
    }

    private static bool TryRead(TextBox box, string name, int min, int max, out int value)
    {
        if (int.TryParse(box.Text, out value) && value >= min && value <= max) return true;
        Info(T("Invalid value for {0}: allowed {1}-{2}.", name, min, max), MessageBoxImage.Warning);
        return false;
    }

    private void StartUndervolt()
    {
        if (!TryRead(UvTarget, T("Target clock"), 0, 3500, out var target) || !TryRead(UvStep, T("Step"), 15, 100, out var step)
            || !TryRead(UvRound, T("Round"), 20, 600, out var round) || !TryRead(UvMax, T("Max offset"), 30, 1000, out var maxOffset)
            || !TryRead(UvTemp, T("Temperature limit"), 60, 90, out var temp)) return;
        if (target is > 0 and < 1000) { Info(T("Invalid value for {0}: allowed {1}-{2}.", T("Target clock"), 1000, 3500), MessageBoxImage.Warning); return; }
        if (Busy()) return;
        var minutes = (int)Math.Ceiling(((maxOffset / (step * 2.0) + 2) * (round + 4) + round * 2) / 60);
        if (Ask(T("Smart undervolt uses the built-in GPU stress test. Your GPU runs at full load the whole time.\n\n1. Close MSI Afterburner and other tuning tools.\n2. Close games and do not use the PC during the test.\n\nDuration: about 5-{0} minutes. A short black screen during a driver reset is normal.", minutes),
                MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK) return;
        _uv = GpuTests.StartUndervolt(new UndervoltOptions(target, step, round, maxOffset, temp));
        _roundsShown = -1;
        UvResult.Visibility = Visibility.Collapsed;
        UvLiveGrid.Visibility = Visibility.Visible;
        UpdateUndervolt();
    }

    private void UpdateUndervolt()
    {
        if (_uv is not { } p) { UvStart.IsEnabled = _info?.OffsetSupported == true; return; }
        UvStart.IsEnabled = p.Done;
        UvCancel.IsEnabled = !p.Done;
        Show(UvStatus, p.Status);
        Show(UvWarning, p.Warning);
        var current = p.Phase switch { TestPhase.Stock => 0, TestPhase.Probe => 1, TestPhase.Replay => 2, TestPhase.Done => 3, _ => -1 };
        var phases = new[] { PhaseStock, PhaseProbe, PhaseReplay };
        for (var i = 0; i < phases.Length; i++)
            phases[i].Background = Brush(current < 0 ? "Panel2" : i < current ? "GoodSoft" : i == current && !p.Done ? "AccentSoft" : "Panel2");

        if (p.Live is { } d)
        {
            UvLClock.Text = $"{d.Clock} MHz";
            UvLVolt.Text = d.Voltage > 0 ? $"{d.Voltage:N3} V" : "-";
            UvLPower.Text = $"{d.Power:N0} W";
            UvLTemp.Text = $"{d.Temp} °C";
            UvLErr.Text = d.Errors.ToString();
            UvLErr.Foreground = Brush(d.Errors > 0 ? "Bad" : "Good");
            Show(UvLive, $"{d.Label} | {T("offset")} {d.Offset:+#;-#;0} MHz | {d.Elapsed}/{d.Seconds} s");
        }
        else Show(UvLive, "");

        var rounds = p.Rounds;
        if (rounds.Count != _roundsShown)
        {
            _roundsShown = rounds.Count;
            UvRounds.Children.Clear();
            foreach (var r in rounds)
            {
                var v = r.Voltage > 0 ? $"{r.Voltage:N3} V" : "  -  ";
                var text = r.Stable
                    ? $"{r.Label,-12} {r.Offset,5:+0;-0;0} MHz   {r.MedClock,5} MHz   {v}   {r.AvgPower,6:N1} W   max {r.MaxTemp} °C"
                    : $"{r.Label,-12} {r.Offset,5:+0;-0;0} MHz   {T("unstable")}: {r.Reason}";
                var row = Row(r.Stable ? "Good" : "Bad", text, null, [], "0,0,0,2", translate: false, boldTitle: false);
                ((TextBlock)((StackPanel)row.Children[1]).Children[0]).FontFamily = new FontFamily("Consolas");
                UvRounds.Children.Add(row);
            }
        }

        if (!p.Done) return;
        UvLiveGrid.Visibility = Visibility.Collapsed;
        _uv = null;
        UpdateTuneState();
        if (p.Result is { } result)
        {
            GpuProfiles.Save(result);
            ShowResult(result);
            Info(T("Profile saved: {0}\n\nApply it (Saved profiles > Apply) and play a few rounds. If you get crashes or graphics glitches, run the smart undervolt again with a smaller max offset.", result.Name));
        }
    }

    private void ShowResult(GpuProfile r)
    {
        RStockW.Text = $"{r.StockAvgPowerW:N0}";
        RUvW.Text = $"{r.AvgPowerW:N0}";
        RStockV.Text = r.StockVoltage is > 0 ? $"{r.StockVoltage:N3}" : "-";
        RUvV.Text = r.Voltage is > 0 ? $"{r.Voltage:N3}" : "-";
        RStockC.Text = $"{r.StockAvgClock:N0}";
        RUvC.Text = $"{r.AvgClock:N0}";
        RSaved.Text = $"{r.PowerSavedW:N0} W";
        RDrop.Text = r.VoltageDropMv is { } mv ? $"-{mv:N0} mV" : "-";
        RTemp.Text = $"{r.MaxTemp:N0} °C";
        UvResult.Visibility = Visibility.Visible;
    }

    private void StartProfileTest(string name)
    {
        if (!TryRead(TestSec, T("Duration"), 30, 7200, out var seconds) || Busy()) return;
        int.TryParse(UvTemp.Text, out var temp);
        _test = GpuTests.StartProfileTest(name, seconds, temp is >= 60 and <= 90 ? temp : 83);
        TestStatus.Foreground = Brush("Text");
        UpdateTest();
    }

    private void UpdateTest()
    {
        if (_test is not { } p) return;
        TestCancel.IsEnabled = !p.Done;
        Show(TestStatus, p.Status);
        Show(TestLive, p.Live is { } d
            ? $"{d.Clock} MHz | {(d.Voltage > 0 ? $"{d.Voltage:N3} V" : "-")} | {d.Power:N0} W | {d.Temp} °C | {T("errors")} {d.Errors} | {d.Elapsed}/{d.Seconds} s"
            : "");
        if (!p.Done) return;
        TestStatus.Foreground = Brush(p.TestResult is { Stable: true } ? "Good" : "Bad");
        _test = null;
        ShowProfiles();
    }

    private void ShowProfiles()
    {
        UpdateTuneState();
        ProfileList.Children.Clear();
        var profiles = GpuProfiles.All();
        if (profiles.Count == 0) { ProfileList.Children.Add(Text("No profiles yet. Run the smart undervolt or save manual values.", 13, "Muted")); return; }
        var (kind, activeName) = GpuTuning.Active();
        var active = kind == TuneKind.Profile ? activeName : "";
        var signIn = GpuTuning.SignInProfile;
        foreach (var p in profiles)
        {
            var desc = T("Max {0} MHz, offset {1:+#;-#;0} MHz", p.MaxClock, p.OffsetMHz);
            if (p.Voltage is > 0) desc += " | " + T("{0:N3} V instead of {1:N3} V", p.Voltage, p.StockVoltage);
            if (p.PowerLimitW > 0) desc += ", " + T("power limit {0} W", p.PowerLimitW);
            if (p.StockAvgPowerW is > 0) desc += " | " + T("Stress test: {0:N0} W -> {1:N0} W", p.StockAvgPowerW, p.AvgPowerW);
            if (p.Driver is { Length: > 0 }) desc += " | " + T("Driver {0}", p.Driver);
            var title = p.Name;
            if (active == p.Name) title += "  " + T("(active now)");
            if (signIn == p.Name) title += "  " + T("(applied at sign-in)");
            var name = p.Name;
            var right = new List<UIElement>
            {
                Button("Apply", async () => await _main.RunAsync("Applying GPU profile", () => GpuTuning.ApplyProfile(name)), "Primary"),
                Button("Stress test", () => StartProfileTest(name)),
                Button(signIn == name ? T("Not at sign-in") : T("At sign-in"), async () =>
                    await _main.RunAsync("GPU profile at sign-in", () => GpuTuning.SetSignInProfile(signIn == name ? "" : name, Environment.ProcessPath!))),
                Button("Delete", async () =>
                {
                    if (Ask(T("Delete profile '{0}'?", name)) != MessageBoxResult.Yes) return;
                    await _main.RunAsync("Deleting GPU profile", () =>
                    {
                        if (GpuTuning.SignInProfile == name) GpuTuning.SetSignInProfile("", Environment.ProcessPath!);
                        GpuProfiles.Delete(name);
                    });
                }, "Danger"),
            };
            ProfileList.Children.Add(Row(active == p.Name ? "Good" : "Accent", title, desc, right, translate: false));
        }
    }
}
