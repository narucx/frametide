using System.Windows.Threading;
using Frametide.Core.Boost;
using Frametide.Core.Infrastructure;
using static Frametide.Localization.Loc;

namespace Frametide.Services;

/// <summary>
/// Runs every 3 seconds, also without a window: handles newly started games while Game Boost is active, starts and
/// stops Auto Game Boost, and keeps the tray icon up to date. Only the process list is read.
/// </summary>
public sealed class BackgroundLoop
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly AutoBoost _auto = new();
    private readonly TrayIcon _tray;
    private bool _running;

    public BackgroundLoop(TrayIcon tray)
    {
        _tray = tray;
        _timer.Tick += async (_, _) => await TickAsync();
        GameBoost.Changed += () => _timer.Dispatcher.BeginInvoke(UpdateTray);
    }

    public void Start()
    {
        UpdateTray();
        _timer.Start();
    }

    public void Stop() => _timer.Stop();

    private async Task TickAsync()
    {
        if (_running) return;
        _running = true;
        try
        {
            var (action, games) = await Task.Run(() =>
            {
                if (GameBoost.IsActive) GameBoost.Watch();
                var cfg = BoostConfig.Load();
                if (!cfg.AutoBoost || BoostRunner.Busy) return (AutoBoostAction.None, "");
                var running = GameBoost.RunningGames(cfg);
                return (_auto.Decide(running.Count > 0, GameBoost.State, DateTime.Now), string.Join(", ", running));
            });
            if (action == AutoBoostAction.Start && await BoostRunner.RunAsync(() => GameBoost.Start(auto: true, trigger: games)))
                _tray.Tip(T("Game Boost started"), T("Detected: {0}", games));
            else if (action == AutoBoostAction.Stop && await BoostRunner.RunAsync(GameBoost.Stop))
                _tray.Tip(T("Game Boost stopped"), T("Game closed, everything was rolled back."));
        }
        catch (Exception e) { Log.Warn($"Background check: {e.Message}"); }
        finally { _running = false; }
        UpdateTray();
    }

    private void UpdateTray()
    {
        try { _tray.Update(GameBoost.State, BoostConfig.Load().AutoBoost); }
        catch (Exception e) when (e is System.IO.IOException or System.Text.Json.JsonException) { }
    }
}
