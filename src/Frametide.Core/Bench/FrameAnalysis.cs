using System.Globalization;

namespace Frametide.Core.Bench;

public sealed class FrameStats
{
    public int Frames { get; init; }
    public double DurationSec { get; init; }
    public double AvgFps { get; init; }
    public double Low1 { get; init; }
    public double Low01 { get; init; }
    public double AvgMs { get; init; }
    public double P99Ms { get; init; }
    public double P999Ms { get; init; }
    public int Stutters { get; init; }
    public int Hitches { get; init; }
    public double ExcludedSec { get; init; }
    public int[] Series { get; init; } = [];
    public string TimeColumn { get; init; } = "";
    public string? Error { get; init; }
}

/// <summary>Analysis of a PresentMon CSV recording.</summary>
public static class FrameAnalysis
{
    /// <summary>Version of the stutter rule; sessions are only compared with sessions of the same version.</summary>
    public const int Version = 2;

    /// <summary>
    /// Reads a PresentMon CSV (v1 or v2 columns) and uses the swap chain with the most frames (the game itself, not a
    /// launcher or overlay). Lows are percentiles: 1% low = 1000 / 99th percentile frame time. Frames that start inside
    /// an excluded interval (QPC ms; game not in front) are skipped, with 0.5 s before and 1.5 s after for the switch itself.
    /// </summary>
    public static FrameStats Analyze(string csvPath, IReadOnlyList<(double From, double To)>? excluded = null)
    {
        excluded ??= [];
        var groups = new Dictionary<string, List<float>>();
        double excludedMs = 0;
        string timeColumn;
        using (var sr = new StreamReader(csvPath))
        {
            var header = sr.ReadLine();
            if (header is null) return new FrameStats { Error = "empty file" };
            var cols = header.Split(',');
            var iFt = Array.IndexOf(cols, "FrameTime");
            if (iFt < 0) iFt = Array.IndexOf(cols, "MsBetweenPresents");
            var iPid = Array.IndexOf(cols, "ProcessID");
            var iSc = Array.IndexOf(cols, "SwapChainAddress");
            // Absolute QPC time of the frame start; a relative time (since PresentMon started) cannot be matched.
            var iT = Array.IndexOf(cols, "CPUStartQPCTimeInMs");
            if (iT < 0) iT = Array.IndexOf(cols, "CPUStartQPCTime");
            double tScale = 1;
            if (iT < 0 && (iT = Array.IndexOf(cols, "CPUStartQPC")) >= 0) tScale = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            timeColumn = iT >= 0 ? cols[iT] : "(none)";
            if (iFt < 0) return new FrameStats { Error = "no frame time column", TimeColumn = timeColumn };

            string? line;
            while ((line = sr.ReadLine()) is not null)
            {
                var f = line.Split(',');
                if (f.Length <= iFt) continue;
                if (!float.TryParse(f[iFt], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || v <= 0 || v > 5000) continue;
                if (excluded.Count > 0 && iT >= 0 && iT < f.Length && double.TryParse(f[iT], NumberStyles.Float, CultureInfo.InvariantCulture, out var t))
                {
                    t *= tScale;
                    if (excluded.Any(e => t >= e.From - 500 && t <= e.To + 1500)) { excludedMs += v; continue; }
                }
                var key = (iPid >= 0 && iPid < f.Length ? f[iPid] : "") + "|" + (iSc >= 0 && iSc < f.Length ? f[iSc] : "");
                if (!groups.TryGetValue(key, out var list)) groups[key] = list = [];
                list.Add(v);
            }
        }
        var ft = groups.Values.MaxBy(g => g.Count);
        if (ft is null || ft.Count < 2) return new FrameStats { Error = "no frames", TimeColumn = timeColumn };

        double sum = 0;
        var perSecond = new List<int>();
        foreach (var v in ft)
        {
            sum += v;
            var sec = (int)(sum / 1000.0);
            while (perSecond.Count <= sec) perSecond.Add(0);
            perSecond[sec]++;
        }
        var sorted = ft.ToArray();
        Array.Sort(sorted);
        var n = sorted.Length;

        // Stutter = a frame much slower than the frames right before it (rolling average of the last 60) that
        // recovers right after (a spike, not the start of a slower phase). Comparing with the local average keeps
        // menus and loading screens, which are slow but even, from counting. Hitch = a frame over 100 ms.
        int stutters = 0, hitches = 0;
        double window = 0;
        var recent = new Queue<float>();
        for (var i = 0; i < ft.Count; i++)
        {
            var v = ft[i];
            var avg = recent.Count > 0 ? window / recent.Count : v;
            var spike = recent.Count >= 20 && v > 2.5 * avg && v > avg + 4;
            var recovers = i + 1 >= ft.Count || v > 2 * ft[i + 1];
            if (spike && recovers) stutters++;
            if (v > 100) hitches++;
            recent.Enqueue(v);
            window += v;
            if (recent.Count > 60) window -= recent.Dequeue();
        }

        var p99 = sorted[Math.Min(n - 1, (int)Math.Ceiling(n * 0.99) - 1)];
        var p999 = sorted[Math.Min(n - 1, (int)Math.Ceiling(n * 0.999) - 1)];

        // FPS per second for the chart, at most 300 points; the last second is incomplete.
        if (perSecond.Count > 1) perSecond.RemoveAt(perSecond.Count - 1);
        var points = Math.Min(300, perSecond.Count);
        var series = new int[points];
        for (var i = 0; i < points; i++)
        {
            int a = (int)((long)i * perSecond.Count / points), b = (int)((long)(i + 1) * perSecond.Count / points);
            long s = 0;
            for (var j = a; j < b; j++) s += perSecond[j];
            series[i] = (int)(s / Math.Max(1, b - a));
        }

        return new FrameStats
        {
            Frames = n, DurationSec = sum / 1000.0, AvgMs = sum / n, AvgFps = n / (sum / 1000.0),
            P99Ms = p99, P999Ms = p999, Low1 = 1000.0 / p99, Low01 = 1000.0 / p999,
            Stutters = stutters, Hitches = hitches, ExcludedSec = excludedMs / 1000.0, Series = series, TimeColumn = timeColumn,
        };
    }

    /// <summary>FPS limit (in-game cap, V-Sync, driver limit): most of the session sits right at the same top value.</summary>
    public static int? DetectCap(IReadOnlyList<int> series)
    {
        var v = series.Where(x => x > 0).ToList();
        if (v.Count < 10) return null;
        var max = v.Max();
        var near = v.Where(x => x >= max * 0.985).Order().ToList();
        if ((double)near.Count / v.Count < 0.5) return null;
        return near[near.Count / 2];
    }
}
