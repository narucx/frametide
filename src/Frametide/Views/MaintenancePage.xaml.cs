using System.Windows;
using System.Windows.Controls;
using Frametide.Core.Infrastructure;
using Frametide.Core.Maintenance;
using Frametide.Core.Windows;
using static Frametide.Localization.Loc;
using static Frametide.UiKit;

namespace Frametide.Views;

public partial class MaintenancePage : UserControl
{
    private readonly MainWindow _main;

    public MaintenancePage(MainWindow main)
    {
        _main = main;
        InitializeComponent();
        TranslateTree(this);

        RestoreButton.Click += async (_, _) =>
        {
            // An exception is already shown by RunAsync (ok is then false).
            var (ok, error) = await _main.RunAsync("Restore point", () => (true, RestorePoint.Create("Frametide")));
            if (!ok) return;
            if (error is null) Info(T("Restore point created."));
            else Info(T("The restore point could not be created. Details in the log.") + "\n\n" + T(error), MessageBoxImage.Warning);
        };
        SfcButton.Click += (_, _) =>
        {
            // Own console window: the user sees the progress and the result; it can take a long time.
            const string script = "title Frametide: system file check & echo DISM is checking the component store ... & DISM /Online /Cleanup-Image /RestoreHealth"
                                  + " & echo. & echo SFC is checking system files ... & sfc /scannow & echo. & echo Done. You can close this window. & pause";
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(NativeProcess.SystemProgram("cmd.exe"), "/c " + script) { UseShellExecute = true })?.Dispose();
            Log.Info("DISM + SFC started in a separate window.");
        };
        StartupScan.Click += async (_, _) => ShowStartup(await _main.RunAsync("Reading autostart", StartupItems.All) ?? []);
        CleanScan.Click += async (_, _) => ShowCleanup(await _main.RunAsync("Cleanup scan", Cleanup.Scan) ?? []);
        CleanRun.Click += async (_, _) =>
        {
            var ids = Checked<CleanupItem>(CleanList).Select(i => i.Category.Id).ToList();
            if (ids.Count == 0 || Ask(T("Permanently delete {0} category(ies)?", ids.Count), icon: MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            var freed = await _main.RunAsync("Cleanup", () => Cleanup.Clean(ids));
            CleanList.Children.Clear();
            CleanRun.IsEnabled = false;
            Info(T("Freed: {0}", Size(freed)));
        };
        AppScan.Click += async (_, _) => ShowApps(await _main.RunAsync("Scanning apps", PreinstalledApps.Find) ?? []);
        AppRecommended.Click += (_, _) =>
        {
            foreach (var c in AppList.Children.OfType<CheckBox>()) c.IsChecked = ((PreinstalledApp)c.Tag).Recommended;
        };
        AppRemove.Click += async (_, _) =>
        {
            var apps = Checked<PreinstalledApp>(AppList).ToList();
            if (apps.Count == 0) return;
            if (Ask(T("Remove {0} app(s) for all users?", apps.Count) + "\n\n" + string.Join("\n", apps.Select(a => $"{T(a.Name)}: {string.Join(", ", a.FamilyNames.Select(f => f.Split('_')[0]))}")), icon: MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            await _main.RunAsync("Removing apps", () => PreinstalledApps.Remove(apps));
            ShowApps(await _main.RunAsync("Scanning apps", PreinstalledApps.Find) ?? []);
        };
        GhostScan.Click += async (_, _) => ShowGhosts(await _main.RunAsync("Scanning ghost devices", GhostDevices.Find) ?? []);
        GhostRemove.Click += async (_, _) =>
        {
            var ids = Checked<GhostDevice>(GhostList).Select(g => g.InstanceId).ToList();
            if (ids.Count == 0 || Ask(T("Remove {0} device entry(ies)?", ids.Count) + "\n\n"
                + T("Devices that are only unplugged or switched off right now are listed too. They are set up again when connected, but lose their settings (e.g. audio, monitor colour profile, Bluetooth pairing)."),
                icon: MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            await _main.RunAsync("Removing ghost devices", () => GhostDevices.Remove(ids));
            ShowGhosts(await _main.RunAsync("Scanning ghost devices", GhostDevices.Find) ?? []);
        };
    }

    private static IEnumerable<T> Checked<T>(Panel panel) => panel.Children.OfType<CheckBox>().Where(c => c.IsChecked == true).Select(c => (T)c.Tag);

    private static string Size(long bytes) => bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):N1} GB" : bytes >= 1L << 20 ? $"{bytes / (double)(1L << 20):N0} MB" : $"{bytes / 1024.0:N0} KB";

    private void ShowStartup(IReadOnlyList<StartupItem> items)
    {
        StartupList.Children.Clear();
        if (items.Count == 0) { StartupList.Children.Add(Text("No autostart programs found.", 13, "Good")); return; }
        foreach (var item in items)
        {
            var current = item;
            var check = new CheckBox { Content = T("On"), IsChecked = item.Enabled, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            var desc = string.Join("  |  ", new[] { item.Publisher, T(item.Source) }.Where(s => s.Length > 0));
            if (item.Command.Length > 0) desc += "\n" + item.Command;
            var row = Row(item.Enabled ? "Good" : "Muted", item.Name, desc, [check], "0,0,0,10", translate: false);
            check.Click += async (_, _) =>
            {
                var on = check.IsChecked == true;
                var ok = await _main.RunAsync("Autostart", () => { StartupItems.SetEnabled(current, on); return true; });
                if (!ok) { check.IsChecked = !on; return; }
                current = current with { Enabled = on };
                ((System.Windows.Shapes.Ellipse)row.Children[0]).Fill = Brush(on ? "Good" : "Muted");
            };
            StartupList.Children.Add(row);
        }
    }

    private void ShowCleanup(IReadOnlyList<CleanupItem> items)
    {
        CleanList.Children.Clear();
        foreach (var i in items)
            CleanList.Children.Add(new CheckBox
            {
                Content = $"{T(i.Category.Name)}  -  {Size(i.Bytes)}", Tag = i, Margin = new Thickness(0, 4, 16, 4),
                IsChecked = i.Bytes > 0 && i.Category.Id is not "shader" and not "recycle",   // those two only on purpose
            });
        CleanRun.IsEnabled = items.Count > 0;
    }

    private void ShowApps(IReadOnlyList<PreinstalledApp> apps)
    {
        AppList.Children.Clear();
        AppRecommended.IsEnabled = AppRemove.IsEnabled = apps.Count > 0;
        if (apps.Count == 0) { AppList.Children.Add(Text("None of the listed apps are installed.", 13, "Good")); return; }
        foreach (var a in apps)
            AppList.Children.Add(new CheckBox
            {
                Content = a.Recommended ? $"{T(a.Name)}  {T("(recommended)")}" : T(a.Name), Tag = a, Margin = new Thickness(0, 4, 16, 4),
            });
    }

    private void ShowGhosts(IReadOnlyList<GhostDevice> devices)
    {
        GhostList.Children.Clear();
        GhostRemove.IsEnabled = devices.Count > 0;
        if (devices.Count == 0) { GhostList.Children.Add(Text("No ghost devices found.", 13, "Good")); return; }
        foreach (var d in devices)
            GhostList.Children.Add(new CheckBox
            {
                Content = $"[{d.Class}] {d.Name}  -  " + (d.LastSeen is { } seen ? T("last seen {0:d}", seen.ToLocalTime()) : T("last seen: unknown")),
                Tag = d, Margin = new Thickness(0, 4, 16, 4),
                IsChecked = d.Suggested,
            });
    }
}
