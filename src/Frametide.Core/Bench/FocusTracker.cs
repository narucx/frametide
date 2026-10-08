using System.Diagnostics;
using System.Runtime.InteropServices;
using Frametide.Core.Boost;

namespace Frametide.Core.Bench;

/// <summary>
/// Tracks when the game is not the foreground window (alt-tab), so those periods can be left out of the analysis:
/// games throttle in the background. Only the owner process ID of the foreground window is read; the game process is
/// never opened. Times are on the QPC clock in ms, like PresentMon's with --qpc_time_ms.
/// </summary>
public sealed partial class FocusTracker(string exe)
{
    private readonly Lock _gate = new();
    private readonly List<(double From, double To)> _away = [];
    private HashSet<int> _pids = [];
    private bool _focused = true;
    private double _since;

    public bool Focused { get { lock (_gate) return _focused; } }

    public static double NowMs() => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;

    /// <summary>Re-reads the game's process IDs (the game may restart).</summary>
    public void UpdatePids()
    {
        var pids = new HashSet<int>();
        foreach (var p in Process.GetProcessesByName(BoostConfig.ProcessName(exe))) using (p) pids.Add(p.Id);
        lock (_gate) _pids = pids;
    }

    /// <summary>Called a few times per second while recording.</summary>
    public void Tick()
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out var pid);
        lock (_gate)
        {
            if (_pids.Count == 0) return;
            var inFront = _pids.Contains((int)pid);
            if (_focused && !inFront) { _focused = false; _since = NowMs(); }
            else if (!_focused && inFront) { _away.Add((_since, NowMs())); _focused = true; }
        }
    }

    /// <summary>Ends the tracking and returns the periods in which the game was not in front.</summary>
    public List<(double From, double To)> Stop()
    {
        lock (_gate)
        {
            if (!_focused) { _away.Add((_since, NowMs())); _focused = true; }
            return [.. _away];
        }
    }

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}
