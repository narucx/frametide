using System.Windows.Threading;
using Frametide.Core.Bench;
using Frametide.Core.Boost;
using Frametide.Core.Infrastructure;
using static Frametide.Localization.Loc;

namespace Frametide.Services;

/// <summary>
/// Runs every 3 seconds, also without a window: handles newly started games while Game Boost is active, starts and
/// stops Auto Game Boost, records benchmarks and keeps the tray icon up to date. Only the process list is read.
/// </summary>
public sealed class BackgroundLoop : IDisposable
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly AutoBoost _auto = new();
    private readonly BenchRecorder _bench = new();
    private readonly Timer _focusTimer;
    private readonly TrayIcon _tray;
    private bool _running;
    private bool _analyzing;

    public BackgroundLoop(TrayIcon tray)
    {
        _tray = tray;
        _timer.Tick += async (_, _) => await TickAsync();
        _focusTimer = new Timer(_ => _bench.FocusTick());
        GameBoost.Changed += () => _timer.Dispatcher.BeginInvoke(UpdateTray);
    }

    public void Start()
    {
        UpdateTray();
        _timer.Start();
        _focusTimer.Change(TimeSpan.Zero, TimeSpan.FromMilliseconds(250));
    }

    public void Stop()
    {
        _timer.Stop();
        _focusTimer.Change(Timeout.Infinite, Timeout.Infinite);
    }

    private async Task TickAsync()
    {
        if (_running) return;
        _running = true;
        try
        {
            await AutoBoostAsync();
            var finished = await Task.Run(() => _analyzing ? null : _bench.Tick());
            if (finished is not null) _ = AnalyzeAsync(finished);
        }
        catch (Exception e) { Log.Warn($"Background check: {e.Message}"); }
        finally { _running = false; }
        UpdateTray();
    }

    private async Task AutoBoostAsync()
    {
        var (action, games) = await Task.Run(() =>
        {
            if (GameBoost.IsActive) GameBoost.Watch();
            var cfg = BoostConfig.Load();
            if (!cfg.AutoBoost || BoostRunner.Busy) return (AutoBoostAction.None, "");
            var running = GameBoost.RunningGames(cfg);
            return (_auto.Decide(running.Count > 0, GameBoost.State, DateTime.Now, BenchRecorder.SkipBoostOnce), string.Join(", ", running));
        });
        switch (action)
        {
            case AutoBoostAction.Start when await BoostRunner.RunAsync(() => GameBoost.Start(auto: true, trigger: games)):
                _tray.Tip(T("Game Boost started"), T("Detected: {0}", games));
                break;
            case AutoBoostAction.Stop when await BoostRunner.RunAsync(GameBoost.Stop):
                _tray.Tip(T("Game Boost stopped"), T("Game closed, everything was rolled back."));
                break;
            case AutoBoostAction.SkipFinished:
                BenchRecorder.SkipBoostOnce = false;
                Log.Ok("Session without Game Boost finished, Auto Game Boost is active again.");
                BenchStore.NotifyChanged();
                break;
        }
    }

    private async Task AnalyzeAsync(Recording rec)
    {
        _analyzing = true;
        try
        {
            if (await Task.Run(() => _bench.Complete(rec)) is { } s)
                _tray.Tip(T("Benchmark saved: {0}", s.Game), T("{0} FPS avg, 1% low {1}, 0.1% low {2}", s.AvgFps.ToString("N0"), s.Low1.ToString("N0"), s.Low01.ToString("N0")));
        }
        catch (Exception e) { Log.Error($"Benchmark {rec.Game}: {e.Message}"); }
        finally { _analyzing = false; }
    }

    private void UpdateTray()
    {
        try { _tray.Update(GameBoost.State, BoostConfig.Load().AutoBoost); }
        catch (Exception e) when (e is System.IO.IOException or System.Text.Json.JsonException) { }
    }

    public void Dispose()
    {
        Stop();
        _focusTimer.Dispose();
        _bench.Dispose();
    }
}
