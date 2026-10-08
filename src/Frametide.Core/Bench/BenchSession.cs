using System.Text.Json;
using System.Text.Json.Serialization;
using Frametide.Core.Infrastructure;

namespace Frametide.Core.Bench;

/// <summary>Summary of one recorded game session (bench\sessions\(id).json). Fields added later are nullable.</summary>
public sealed class BenchSession
{
    public string Id { get; set; } = "";
    public string Game { get; set; } = "";
    public string Exe { get; set; } = "";
    public DateTime Start { get; set; }
    public double DurationSec { get; set; }
    public int Frames { get; set; }
    public double AvgFps { get; set; }
    public double Low1 { get; set; }
    public double Low01 { get; set; }
    public double AvgMs { get; set; }
    public double P99Ms { get; set; }
    public double P999Ms { get; set; }
    public double StuttersPerMin { get; set; }
    public int? Hitches { get; set; }
    public int? Cap { get; set; }

    /// <summary>Stutter rule the session was analyzed with (<see cref="FrameAnalysis.Version"/>); missing = 1.</summary>
    public int? Analysis { get; set; }

    public double? ExcludedSec { get; set; }
    public int? TabOuts { get; set; }
    public List<FpsLimit>? Limits { get; set; }
    public bool Boost { get; set; }
    public string? GpuProfile { get; set; }
    public double? GpuW { get; set; }
    public double? GpuMHz { get; set; }
    public double? GpuC { get; set; }
    public double? GpuUtil { get; set; }
    public double? CpuLoad { get; set; }
    public int[] Series { get; set; } = [];

    [JsonIgnore]
    public int AnalysisVersion => Math.Max(1, Analysis ?? 1);

    [JsonIgnore]
    public int? EffectiveCap => Cap ?? FrameAnalysis.DetectCap(Series);
}

/// <summary>Averages of one side (without or with Game Boost) of a game, weighted by playing time.</summary>
public sealed record BenchSide(int Count, int Minutes, IReadOnlyDictionary<string, double?> Values)
{
    public double? this[string key] => Values.GetValueOrDefault(key);
}

public sealed record BenchComparison(string Game, BenchSide? Without, BenchSide? With, IReadOnlyList<int> Caps);

public static class BenchStore
{
    public static readonly string[] Metrics = ["AvgFps", "Low1", "Low01", "StuttersPerMin", "GpuW", "GpuMHz", "GpuC"];

    private static string SessionsDir => Path.Combine(AppPaths.BenchDir, "sessions");

    /// <summary>Raised after a session was saved or deleted, or a benchmark setting changed.</summary>
    public static event Action? Changed;

    public static void NotifyChanged() => Changed?.Invoke();

    /// <summary>All sessions, newest first. Files that cannot be read are skipped.</summary>
    public static IReadOnlyList<BenchSession> Sessions()
    {
        if (!Directory.Exists(SessionsDir)) return [];
        var list = new List<BenchSession>();
        foreach (var file in Directory.EnumerateFiles(SessionsDir, "*.json").OrderDescending(StringComparer.Ordinal))
        {
            try { if (JsonFile.Read<BenchSession>(file) is { } s) list.Add(s); }
            catch (Exception e) when (e is JsonException or IOException or InvalidOperationException) { Log.Warn($"Benchmark session {Path.GetFileName(file)} could not be read: {e.Message}"); }
        }
        return list;
    }

    public static void Save(BenchSession s)
    {
        Directory.CreateDirectory(SessionsDir);
        JsonFile.Write(Path.Combine(SessionsDir, $"{s.Id}.json"), s);
        Changed?.Invoke();
    }

    public static void Delete(string id)
    {
        if (!IsValidId(id)) return;
        File.Delete(Path.Combine(SessionsDir, $"{id}.json"));
        Changed?.Invoke();
    }

    /// <summary>Session IDs are timestamps (yyyyMMdd-HHmmss); anything else is not a file name Frametide created.</summary>
    internal static bool IsValidId(string id) => id.Length > 0 && id.All(c => char.IsAsciiDigit(c) || c == '-');

    /// <summary>Per game: sessions without and with Game Boost, averaged and weighted by duration.</summary>
    public static IReadOnlyList<BenchComparison> Compare(IEnumerable<BenchSession> sessions)
    {
        var result = new List<BenchComparison>();
        foreach (var game in sessions.GroupBy(s => s.Game))
        {
            // Stutters are only compared between sessions analyzed with the same rule (the newest one present).
            var version = game.Max(s => s.AnalysisVersion);
            BenchSide? Side(bool boost)
            {
                var set = game.Where(s => s.Boost == boost).ToList();
                if (set.Count == 0) return null;
                var values = new Dictionary<string, double?>();
                foreach (var key in Metrics)
                {
                    double sum = 0, weight = 0;
                    foreach (var s in set)
                    {
                        if (Value(s, key) is not { } v) continue;
                        if (key == "StuttersPerMin" && s.AnalysisVersion != version) continue;
                        sum += v * s.DurationSec;
                        weight += s.DurationSec;
                    }
                    values[key] = weight > 0 ? Math.Round(sum / weight, 1) : null;
                }
                return new BenchSide(set.Count, (int)Math.Round(set.Sum(s => s.DurationSec) / 60), values);
            }
            var caps = game.Select(s => s.EffectiveCap).OfType<int>().Distinct().ToList();
            result.Add(new BenchComparison(game.Key, Side(false), Side(true), caps));
        }
        return result;
    }

    private static double? Value(BenchSession s, string key) => key switch
    {
        "AvgFps" => s.AvgFps, "Low1" => s.Low1, "Low01" => s.Low01, "StuttersPerMin" => s.StuttersPerMin,
        "GpuW" => s.GpuW, "GpuMHz" => s.GpuMHz, "GpuC" => s.GpuC, _ => null,
    };
}
