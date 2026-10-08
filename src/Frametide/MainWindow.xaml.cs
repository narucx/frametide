using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Frametide.Core.Infrastructure;
using Frametide.Localization;
using Frametide.Views;
using static Frametide.Localization.Loc;
using static Frametide.UiKit;

namespace Frametide;

public partial class MainWindow : Window
{
    private sealed record Page(string Title, string Sub, Func<UIElement> Create);

    private readonly Dictionary<string, UIElement> _pages = [];
    private readonly List<string> _busy = [];
    private readonly Page[] _nav;
    private TextBox? _logBox;

    public TweaksPage Tweaks { get; }

    public MainWindow()
    {
        InitializeComponent();
        TranslateTree(this);
        Tweaks = new TweaksPage(this);
        _nav =
        [
            new("Overview", "System, live readings and hints at a glance.", () => new OverviewPage(this)),
            new("Tweaks & profiles", "Every tweak on/off individually. Original values are backed up, \"Revert\" restores exactly the previous state.", () => Tweaks),
            new("Game Boost", "Temporary optimizations only while you play. STOP rolls everything back.", () => new GameBoostPage(this)),
            new("Benchmark", "FPS, 1% lows and stutters of your real game sessions, without and with Game Boost.", Placeholder),
            new("GPU & undervolt", "Live readings, automatic undervolting and profiles (NVIDIA, via NVML).", Placeholder),
            new("CS2", "Launch options, video settings, NVIDIA profile and server blocker.", Placeholder),
            new("Maintenance", "Restore point, system files, cleanup and bloatware.", Placeholder),
            new("Log", "Everything Frametide has changed.", CreateLogPage),
        ];

        var version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "?";
        FooterText.Text = T("Frametide v{0}  -  vibecoded by Henri  -  no warranty, use at your own risk", version);
        PrereleaseBadge.Visibility = version.Contains('-') ? Visibility.Visible : Visibility.Collapsed;   // only for e.g. 1.0.0-beta

        foreach (var page in _nav)
        {
            var nav = new RadioButton { Content = T(page.Title), GroupName = "nav", Style = (Style)FindResource("Nav") };
            nav.Checked += (_, _) => ShowPage(page);
            NavPanel.Children.Add(nav);
        }

        foreach (var (code, name) in Languages) LangCombo.Items.Add(new ComboBoxItem { Content = name, Tag = code });
        LangCombo.SelectedItem = LangCombo.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == Code);
        LangCombo.SelectionChanged += (_, _) => SwitchLanguage();

        Log.LineAdded += OnLogLine;
        Closed += (_, _) => Log.LineAdded -= OnLogLine;

        ((RadioButton)NavPanel.Children[0]).IsChecked = true;
        Loaded += async (_, _) =>
        {
            if (Program.Preview is { } preview) { await RenderPreviewAsync(preview.Png, preview.Page, preview.Height); return; }
            _ = CheckForUpdateAsync();
            await Tweaks.CheckAsync();
        };
    }

    /// <summary>Shows an "Update to vX" button when GitHub Releases has a newer version (installed copies only).</summary>
    private async Task CheckForUpdateAsync()
    {
        var updater = new Services.Updater();
        try
        {
            if (await updater.CheckAsync() is not { } update) return;
            var version = update.TargetFullRelease.Version.ToString();
            UpdateButton.Content = T("Update to v{0}", version);
            UpdateButton.Visibility = Visibility.Visible;
            UpdateButton.Click += async (_, _) =>
            {
                UpdateButton.IsEnabled = false;
                Log.Info($"Updating to v{version}.");
                try { await updater.DownloadAndRestartAsync(update, p => Dispatcher.BeginInvoke(() => UpdateButton.Content = T("Downloading update ... {0} %", p))); }
                catch (Exception e)
                {
                    Log.Error($"Update failed: {e.Message}");
                    Info(T("The update could not be installed: {0}", e.Message), MessageBoxImage.Warning);
                    UpdateButton.IsEnabled = true;
                    UpdateButton.Content = T("Update to v{0}", version);
                }
            };
        }
        catch (Exception e) { Log.Warn($"Update check failed: {e.Message}"); }
    }

    /// <summary>Dev: shows a page, waits for its data, saves the window as PNG and exits.</summary>
    private async Task RenderPreviewAsync(string png, string page, double height)
    {
        await Tweaks.CheckAsync();
        Navigate(page);
        if (height > 0) { MaxHeight = height; Height = height; }
        await Task.Delay(1500);
        UpdateLayout();
        var root = (FrameworkElement)Content;
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bmp.Render(root);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        using (var fs = System.IO.File.Create(png)) enc.Save(fs);
        Application.Current.Shutdown();
    }

    private void OnLogLine(string line) => Dispatcher.BeginInvoke(() =>
    {
        StatusText.Text = line;
        _logBox?.AppendText(line + Environment.NewLine);
        _logBox?.ScrollToEnd();
    });

    private void ShowPage(Page page)
    {
        PageTitle.Text = T(page.Title);
        PageSub.Text = T(page.Sub);
        if (!_pages.TryGetValue(page.Title, out var content)) _pages[page.Title] = content = page.Create();
        PageHost.Content = content;
        PageScroll.ScrollToTop();
    }

    public void Navigate(string title) =>
        NavPanel.Children.OfType<RadioButton>().ElementAt(Array.FindIndex(_nav, p => p.Title == title)).IsChecked = true;

    private static UIElement Placeholder() => Card(new StackPanel
    {
        Children =
        {
            Text("Coming soon", 16, bold: true, margin: "0,0,0,4"),
            Text("This page is not available in this version yet.", 13, "Muted"),
        },
    });

    private UIElement CreateLogPage()
    {
        _logBox = new TextBox
        {
            IsReadOnly = true, FontFamily = new System.Windows.Media.FontFamily("Consolas"), FontSize = 12, Height = 560,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, TextWrapping = TextWrapping.Wrap,
            Text = string.Join(Environment.NewLine, Log.Snapshot()) + Environment.NewLine,
        };
        var open = Button("Open data folder (backups)", () => System.Diagnostics.Process.Start("explorer.exe", AppPaths.DataDir), margin: "0,10,0,0");
        open.HorizontalAlignment = HorizontalAlignment.Left;
        return new StackPanel { Children = { _logBox, open } };
    }

    /// <summary>Runs work off the UI thread and shows it in the status bar. Errors are logged and shown.</summary>
    public async Task<TResult> RunAsync<TResult>(string name, Func<TResult> work)
    {
        _busy.Add(T(name));
        UpdateBusy();
        try { return await Task.Run(work); }
        catch (Exception e)
        {
            Log.Error($"{name}: {e.Message}");
            Info(e.Message, MessageBoxImage.Error);
            return default!;
        }
        finally
        {
            _busy.Remove(T(name));
            UpdateBusy();
        }
    }

    public Task RunAsync(string name, Action work) => RunAsync<bool>(name, () => { work(); return true; });

    private void UpdateBusy() => BusyText.Text = _busy.Count > 0 ? T("Running: {0} ...", string.Join(", ", _busy)) : "";

    private void SwitchLanguage()
    {
        if (LangCombo.SelectedItem is not ComboBoxItem { Tag: string code } || code == Code) return;
        Settings.Language = code;
        Loc.Load(code);
        var fresh = new MainWindow { Left = Left, Top = Top, Width = Width, Height = Height, WindowState = WindowState };
        App.Current.ReplaceMain(fresh);
    }

}
