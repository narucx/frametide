using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Frametide.Core.Hardware;
using Frametide.Core.Infrastructure;
using Frametide.Core.Tweaks;
using Frametide.Core.Windows;
using static Frametide.Localization.Loc;
using static Frametide.UiKit;

namespace Frametide.Views;

/// <summary>System info, GPU/CPU live readings (only while the page is visible), hints and the system check.</summary>
public partial class OverviewPage : UserControl
{
    private readonly MainWindow _main;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly CpuMonitor _cpu = new();
    private GpuInfo? _gpu;
    private int _tick;

    public OverviewPage(MainWindow main)
    {
        _main = main;
        InitializeComponent();
        TranslateTree(this);
        HwinfoDownload.Click += (_, _) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://www.hwinfo.com/download/") { UseShellExecute = true });
        ApplyRecommended.Click += (_, _) => main.Tweaks.ApplyProfileWithPrompt(TweakProfile.Recommended);
        GoTweaks.Click += (_, _) => main.Navigate("Tweaks & profiles");
        CreateRestore.Click += async (_, _) =>
        {
            var error = await main.RunAsync("Restore point", () => RestorePoint.Create("Frametide"));
            if (error is null) Info(T("Restore point created."));
            else Info(T("The restore point could not be created. Details in the log.") + "\n\n" + T(error), MessageBoxImage.Warning);
        };
        main.Tweaks.Checked += ShowHints;
        _timer.Tick += (_, _) => UpdateLive();
        IsVisibleChanged += (_, _) => { if (IsVisible) { UpdateLive(); _timer.Start(); } else _timer.Stop(); };
        Unloaded += (_, _) => _timer.Stop();
        Loaded += async (_, _) => await LoadInfoAsync();
        ShowHints();
    }

    private async Task LoadInfoAsync()
    {
        var (info, checks, gpu) = await _main.RunAsync("System info", () =>
        {
            var i = SystemInfoReader.Read();
            return (i, SystemInfoReader.Checks(i), Nvidia.GetInfo());
        });
        if (info is null) return;
        _gpu = gpu;
        InfoCpu.Text = System.Text.RegularExpressions.Regex.Replace(info.CpuName, @"\(R\)|\(TM\)|CPU|Processor", "").Replace("  ", " ").Trim();
        var cores = info.Hybrid ? T("{0}P + {1}E cores", info.PCores, info.ECores) : T("{0} cores", info.Cores);
        InfoCpu2.Text = $"{cores} | {T("{0} threads", info.Threads)}" + (info.Microcode is { } mc ? $" | Microcode 0x{mc:X}" : "");
        InfoGpu.Text = (info.GpuName ?? "-").Replace("NVIDIA GeForce ", "");
        InfoGpu2.Text = info.GpuDriver is null ? "" : T("Driver {0}", info.GpuDriver);
        InfoRam.Text = $"{info.RamGB} GB";
        var vendor = info.Memory.FirstOrDefault()?.Manufacturer ?? "";
        InfoRam2.Text = $"{T("{0} stick(s)", info.Memory.Count)} | {info.RamSpeed} MT/s" + (vendor.Length > 0 ? $" | {vendor}" : "");
        InfoOs.Text = info.OsName;
        InfoOs2.Text = $"{info.OsVersion} | Build {info.OsBuild}";
        GpuCard.Visibility = gpu is null ? Visibility.Collapsed : Visibility.Visible;

        Checks.Children.Clear();
        foreach (var c in checks)
        {
            var color = c.Level switch { CheckLevel.Ok => "Good", CheckLevel.Warn => "Warn", CheckLevel.Bad => "Bad", _ => "Accent" };
            Checks.Children.Add(Row(color, T(c.Name), T(c.Text, c.TextArgs), [Text(T(c.Value), 13, color, bold: true, translate: false)], "0,0,0,10", translate: false));
        }
        ShowHints();
    }

    private void UpdateLive()
    {
        if (_gpu is not null && Nvidia.GetSnapshot() is { } g)
        {
            GpuTemp.Text = $"{g.TempC} °C";
            GpuPower.Text = $"{g.PowerW:N0} W";
            GpuClock.Text = $"{g.ClockMHz} MHz";
            GpuUtil.Text = $"{g.Util} %";
            GpuOffset.Text = $"{g.OffsetMHz:+#;-#;0} MHz";
        }
        if (_cpu.Sample() is { } c)
        {
            CpuLoad.Text = $"{c.Load} %";
            CpuAvg.Text = $"{c.AvgMHz} MHz";
            CpuMax.Text = $"{c.MaxMHz} MHz";
        }
        if (_tick++ % 2 == 0)
        {
            var h = Hwinfo.ReadCpu();
            HwinfoHint.Visibility = h is null ? Visibility.Visible : Visibility.Collapsed;
            if (h is not null)
            {
                CpuTemp.Text = h.TempC is { } t ? $"{t:N0} °C" : "-";
                CpuTemp.Foreground = Brush(h.TempC >= 90 ? "Bad" : h.TempC >= 80 ? "Warn" : "Text");
                CpuPower.Text = h.PowerW is { } p ? $"{p:N0} W" : "-";
                CpuVcore.Text = h.Vcore is { } v ? $"{v:N3} V" : "-";
            }
        }
    }

    private void ShowHints()
    {
        Hints.Children.Clear();
        var hidden = (Settings.GetNode("HiddenHints") as JsonArray)?.Select(n => (string?)n).ToHashSet() ?? [];
        var t = _main.Tweaks;
        Hints.Children.Add(Row("Accent", t.SummaryText.Length > 0 ? t.SummaryText : T("Checking ..."), null, [], "0,0,0,6", translate: false, boldTitle: false));
        if (t.RepairCount > 0)
            Hints.Children.Add(Row("Warn", T("{0} harmful tweak(s) from other tools found. See 'Tweaks & profiles'.", t.RepairCount), null, [], "0,0,0,6", translate: false, boldTitle: false));
        if (_gpu is not null && Nvidia.GetSnapshot() is { } g && g.PowerLimitW != _gpu.PowerDefaultW && !hidden.Contains("gpu-powerlimit"))
        {
            var hide = Button("Hide", () =>
            {
                var list = (Settings.GetNode("HiddenHints") as JsonArray) ?? [];
                list.Add("gpu-powerlimit");
                Settings.Set("HiddenHints", list);
                ShowHints();
            });
            Hints.Children.Add(Row("Accent", T("GPU power limit is set to {0} W (default {1} W). It was changed by a tool.", g.PowerLimitW, _gpu.PowerDefaultW), null, [hide], "0,0,0,6", translate: false, boldTitle: false));
        }
    }
}
