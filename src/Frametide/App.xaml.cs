using System.Windows;
using Frametide.Core.Boost;
using Frametide.Core.Gpu;
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
        L.Translator = (text, args) => T(text, args);   // status texts from Core in the UI language
        Core.Bench.FpsLimits.Providers.Add(NvidiaProfiles.FpsLimitsFor);
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
        _ = Task.Run(SecureSignInTasks);
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
        if (GpuTests.Running)
        {
            // Exiting mid-test would leave the GPU locked or at the maximum power limit.
            if (MessageBox.Show(T("A GPU test is still running. Exiting cancels it and resets the GPU to default. Exit anyway?"),
                    "Frametide", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            GpuTests.CancelAndWait(TimeSpan.FromSeconds(15));
        }
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
        _loop?.Dispose();
        _window?.Close();
        _tray?.Dispose();
        Log.Info("Frametide closed.");
        Shutdown();
    }

    /// <summary>Installed for all users: only administrators can change the program folder (needed for sign-in tasks).</summary>
    public static bool ProtectedInstall => _protectedInstall.Value;
    private static readonly Lazy<bool> _protectedInstall = new(() => AdminOnly.IsProtectedProgram(Environment.ProcessPath!));

    /// <summary>After an update or a move the tasks may point to an old path; tasks for an unsafe folder are removed.</summary>
    private static void SecureSignInTasks()
    {
        try
        {
            LogonTask.Secure(Environment.ProcessPath!);
            Autostart.Restore(Environment.ProcessPath!);
            GpuTuning.RestoreSignInTask(Environment.ProcessPath!);
        }
        catch (Exception e) { Log.Warn($"Sign-in tasks: {e.Message}"); }
    }
}
