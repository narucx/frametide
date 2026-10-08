using Frametide.Core.Boost;
using Frametide.Core.Infrastructure;

namespace Frametide.Services;

/// <summary>Runs Game Boost START/STOP off the UI thread, one at a time (page, tray menu and auto mode share it).</summary>
public static class BoostRunner
{
    private static int _busy;

    public static bool Busy => Volatile.Read(ref _busy) != 0;

    /// <summary>False when another START/STOP was still running.</summary>
    public static async Task<bool> RunAsync(Action work)
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return false;
        try { await Task.Run(work); return true; }
        catch (Exception e) { Log.Error($"Game Boost: {e.Message}"); return false; }
        finally { Volatile.Write(ref _busy, 0); }
    }

    public static Task<bool> ToggleAsync() => GameBoost.IsActive ? RunAsync(GameBoost.Stop) : RunAsync(() => GameBoost.Start());
}
