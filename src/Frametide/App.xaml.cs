using System.Windows;
using Frametide.Core.Boost;
using Frametide.Core.Infrastructure;
using Frametide.Core.Windows;
using Frametide.Localization;
using Frametide.Services;
using static Frametide.Localization.Loc;

namespace Frametide;

/// <summary>
/// Lives in the notification area; the window is created when needed and closed again (closing it does not exit,
/// unless "keep running in the tray" is off).
/// </summary>
public partial class App : Application
{
    private TrayIcon? _tray;
    private BackgroundLoop? _loop;
    private MainWindow? _window;
    private bool _trayTipShown;

    internal SingleInstance? Instance { get; init; }

    public static new App Current => (App)Application.Current;

    public bool Exiting { get; private set; }

    public static bool CloseToTray
    {
        get => Settings.GetBool("CloseToTray", true);
        set => Settings.Set("CloseToTray", value);
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (Program.Preview is not null)
        {
            AppPaths.UseDataDir(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "frametide-preview"));
            Loc.Load(Settings.Language);
            ShowMain();
            return;
        }
        AppPaths.EnsureDataDir();
        Loc.Load(Settings.Language);
        Log.Info($"Frametide started (language {Loc.Code}{(Program.StartInTray ? ", tray" : "")}).");
        if (GameBoost.IsActive) Log.Warn("Game Boost was still active at startup (e.g. after a crash). STOP rolls everything back.");

        _tray = new TrayIcon(this);
        _loop = new BackgroundLoop(_tray);
        _loop.Start();
        Instance?.Listen(() => Dispatcher.BeginInvoke(ShowMain));
        _ = Task.Run(RefreshAutostart);
        if (Program.StartInTray) _trayTipShown = true;
        else ShowMain();
    }

    public void ShowMain()
    {
        if (Exiting) return;
        if (_window is null)
        {
            _window = new MainWindow();
            _window.Closed += OnWindowClosed;
            MainWindow = _window;
            _window.Show();
        }
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    /// <summary>Language switch: the window is rebuilt with the new texts.</summary>
    public void ReplaceMain(MainWindow fresh)
    {
        var old = _window;
        if (old is not null) old.Closed -= OnWindowClosed;
        _window = fresh;
        fresh.Closed += OnWindowClosed;
        MainWindow = fresh;
        fresh.Show();
        old?.Close();
        _tray?.UpdateTexts();
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        _window = null;
        if (Exiting || Program.Preview is not null) return;
        if (!CloseToTray) { ExitApp(); return; }
        if (!_trayTipShown)
        {
            _trayTipShown = true;
            _tray?.Tip("Frametide", T("Frametide keeps running in the notification area. Right-click the icon for the menu."));
        }
    }

    public void ExitApp()
    {
        if (Exiting) return;
        if (GameBoost.IsActive)
        {
            var answer = MessageBox.Show(T("Game Boost is still active.\n\nYes = stop Game Boost and exit (everything is rolled back).\nNo = exit and keep Game Boost active (suspended apps stay frozen until you press STOP next time)."),
                "Frametide", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Cancel) return;
            if (answer == MessageBoxResult.Yes)
            {
                try { GameBoost.Stop(); } catch (Exception ex) { Log.Error($"Stop on exit: {ex.Message}"); }
            }
        }
        Exiting = true;
        _loop?.Stop();
        _window?.Close();
        _tray?.Dispose();
        Log.Info("Frametide closed.");
        Shutdown();
    }

    public static bool StartWithWindows => Settings.GetBool("StartWithWindows", false);

    public static void SetStartWithWindows(bool on)
    {
        if (on) Autostart.Enable(Environment.ProcessPath!); else Autostart.Disable();
        Settings.Set("StartWithWindows", on);
    }

    /// <summary>After an update or a move the task may point to an old path: register it again.</summary>
    private static void RefreshAutostart()
    {
        try
        {
            if (StartWithWindows && !string.Equals(Autostart.RegisteredCommand(), Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
                Autostart.Enable(Environment.ProcessPath!);
        }
        catch (Exception e) { Log.Warn($"Start with Windows: {e.Message}"); }
    }
}
