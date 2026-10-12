using System.ComponentModel;
using System.Diagnostics;

namespace Frametide.Core.Bench;

/// <summary>Time a Windows background job was busy during a session, and the minute it was first seen.</summary>
public sealed record BackgroundJob(string Name, int Seconds, int FromMinute);

/// <summary>
/// Detects Windows background jobs that can cost frames (updates, virus scans, indexing) by the CPU time their
/// processes use. Only the CPU time is read, the processes are not touched.
/// </summary>
public sealed class BackgroundActivity
{
    /// <summary>Busy = at least a quarter of one CPU core since the last sample.</summary>
    public const double BusyShare = 0.25;

    public static readonly IReadOnlyDictionary<string, string> Known = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["tiworker"] = "Windows Update (installing)",
        ["trustedinstaller"] = "Windows Update (installing)",
        ["mousocoreworker"] = "Windows Update",
        ["usocoreworker"] = "Windows Update",
        ["wuauclt"] = "Windows Update",
        ["msmpeng"] = "Virus scan (Microsoft Defender)",
        ["mpcmdrun"] = "Virus scan (Microsoft Defender)",
        ["searchindexer"] = "Search indexing",
        ["compattelrunner"] = "Windows compatibility check",
        ["dismhost"] = "Windows component servicing",
        ["mrt"] = "Malicious Software Removal Tool",
    };

    private Dictionary<int, TimeSpan> _last = [];
    private DateTime _lastAt;

    /// <summary>Names of the jobs busy since the last call; the first call only takes the starting point.</summary>
    public IReadOnlyList<string> Sample()
    {
        var now = DateTime.UtcNow;
        var cpu = new Dictionary<int, TimeSpan>();
        var busy = new Dictionary<string, TimeSpan>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                if (!Known.TryGetValue(p.ProcessName, out var name)) continue;
                try { cpu[p.Id] = p.TotalProcessorTime; }
                catch (Exception e) when (e is Win32Exception or InvalidOperationException or NotSupportedException) { continue; }
                if (_last.TryGetValue(p.Id, out var before))
                    busy[name] = busy.GetValueOrDefault(name) + (cpu[p.Id] - before);
            }
        }
        var seconds = (now - _lastAt).TotalSeconds;
        var first = _lastAt == default;
        _last = cpu;
        _lastAt = now;
        if (first || seconds <= 0) return [];
        return Busy(busy, seconds);
    }

    internal static IReadOnlyList<string> Busy(IReadOnlyDictionary<string, TimeSpan> cpuByJob, double seconds) =>
        cpuByJob.Where(kv => kv.Value.TotalSeconds / seconds >= BusyShare).Select(kv => kv.Key).Order(StringComparer.Ordinal).ToList();

    /// <summary>Per job: busy time (samples × interval) and the first minute it was seen.</summary>
    public static List<BackgroundJob> Summarize(IReadOnlyList<(DateTime At, IReadOnlyList<string> Jobs)> samples, DateTime start, double intervalSec) =>
        samples.SelectMany(s => s.Jobs.Select(j => (Job: j, s.At)))
            .GroupBy(x => x.Job)
            .Select(g => new BackgroundJob(g.Key, (int)Math.Round(g.Count() * intervalSec), Math.Max(0, (int)(g.Min(x => x.At) - start).TotalMinutes)))
            .OrderBy(j => j.FromMinute).ThenBy(j => j.Name, StringComparer.Ordinal)
            .ToList();
}
