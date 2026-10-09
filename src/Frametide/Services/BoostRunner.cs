using Frametide.Core.Boost;
using Frametide.Core.Infrastructure;

namespace Frametide.Services;

/// <summary>Runs Game Boost START/STOP off the UI thread, one at a time (page, tray menu and auto mode share it).</summary>
public static class BoostRunner
{
    private static int _busy;
    private static volatile bool _closed;

    public static bool Busy => Volatile.Read(ref _busy) != 0;

    /// <summary>False when another START/STOP was still running, or after <see cref="CloseAndWait"/>.</summary>
    public static async Task<bool> RunAsync(Action work)
    {
        if (_closed || Interlocked.Exchange(ref _busy, 1) == 1) return false;
        if (_closed) { Volatile.Write(ref _busy, 0); return false; }
        try { await Task.Run(work); return true; }
        catch (Exception e) { Log.Error($"Game Boost: {e.Message}"); return false; }
        finally { Volatile.Write(ref _busy, 0); }
    }

    /// <summary>
    /// For exiting: refuses new runs and waits until a running START/STOP (e.g. a background auto START) has finished.
    /// False when it is still running after <paramref name="timeout"/>. <see cref="Reopen"/> undoes it when the exit is cancelled.
    /// </summary>
    public static bool CloseAndWait(TimeSpan timeout)
    {
        _closed = true;
        return SpinWait.SpinUntil(() => !Busy, timeout);
    }

    public static void Reopen() => _closed = false;

    public static Task<bool> ToggleAsync() => GameBoost.IsActive ? RunAsync(GameBoost.Stop) : RunAsync(() => GameBoost.Start());
}
