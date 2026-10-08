using System.Diagnostics;
using Frametide.Core.Boost;
using Frametide.Core.Hardware;
using Frametide.Core.Infrastructure;
using Frametide.Core.Windows;

namespace Frametide.Core.Bench;

/// <summary>A running (or finished, not yet analyzed) recording. Saved next to the raw CSV, so it can be analyzed later.</summary>
public sealed class Recording
{
    public string Id { get; set; } = "";
    public string Game { get; set; } = "";
    public string Exe { get; set; } = "";
    public DateTime Start { get; set; }
    public string Csv { get; set; } = "";
    public int PmPid { get; set; }
    public string? PmStart { get; set; }
    public DateTime? NoGameSince { get; set; }
    public List<FpsLimit> Limits { get; set; } = [];
    public bool BoostExpected { get; set; }
}

/// <summary>
/// Records games from the Game Boost list automatically: starts PresentMon when a listed game runs, samples GPU/CPU
/// and the Game Boost state, and turns the recording into a session summary when the game has closed.
/// </summary>
public sealed class BenchRecorder : IDisposable
{
    public const int MinSeconds = 60;

    private sealed record Sample(bool Focused, bool Boost, double? GpuW, double? GpuMHz, double? GpuC, double? GpuUtil, double? CpuLoad);

    private readonly Lock _gate = new();
    private readonly List<Sample> _samples = [];
    private readonly HashSet<string> _tried = [];
    private readonly CpuMonitor _cpu = new();
    private FocusTracker? _focus;
    private string? _liveId;
    private DateTime _retryAt;

    public static bool RecordEnabled
    {
        get => Settings.GetBool("BenchRecord", true);
        set => Settings.Set("BenchRecord", value);
    }

    /// <summary>"Next session WITHOUT Game Boost": Auto Game Boost skips one game session, for a comparison.</summary>
    public static bool SkipBoostOnce
    {
        get => Settings.GetBool("BenchSkipBoostOnce", false);
        set => Settings.Set("BenchSkipBoostOnce", value);
    }

    private static string RecordingFile => Path.Combine(AppPaths.BenchDir, "recording.json");
    private static string RawDir => Path.Combine(AppPaths.BenchDir, "raw");

    public static Recording? Current => JsonFile.Read<Recording>(RecordingFile);

    /// <summary>Called every few seconds. Returns a recording that has finished and needs <see cref="Complete"/>, otherwise null.</summary>
    public Recording? Tick()
    {
        if (Current is not { } rec)
        {
            if (FindUnanalyzed() is { } earlier) return earlier;
            if (!RecordEnabled || PresentMon.ExePath is null || DateTime.Now < _retryAt) return null;
            if (RunningGame() is { } game)
            {
                try { StartRecording(game); }
                catch (Exception e)
                {
                    _retryAt = DateTime.Now.AddMinutes(5);
                    Log.Warn($"Benchmark: PresentMon could not be started: {e.Message}");
                }
            }
            return null;
        }

        var gameRunning = IsRunning(rec.Exe);
        if (!RecorderRunning(rec))
        {
            // PresentMon ended within seconds while the game still runs: it failed (e.g. the trace session is blocked).
            if (gameRunning && DateTime.Now - rec.Start < TimeSpan.FromSeconds(30))
            {
                _retryAt = DateTime.Now.AddMinutes(5);
                Log.Warn("Benchmark: PresentMon stopped right after the start, next try in 5 minutes.");
            }
            return rec;
        }

        FocusTracker focus;
        lock (_gate)
        {
            if (_focus is null || _liveId != rec.Id) { _focus = new FocusTracker(rec.Exe); _liveId = rec.Id; _samples.Clear(); }
            focus = _focus;
        }
        focus.UpdatePids();
        var gpu = Nvidia.GetSnapshot();
        var cpu = _cpu.Sample();
        lock (_gate)
            _samples.Add(new Sample(focus.Focused, GameBoost.IsActive, gpu?.PowerW, gpu?.ClockMHz, gpu?.TempC, gpu?.Util, cpu?.Load));

        // Safety net: PresentMon waits for the game; if the game is gone for 20 s, end the recording.
        if (gameRunning) return null;
        if (rec.NoGameSince is null) { rec.NoGameSince = DateTime.Now; JsonFile.Write(RecordingFile, rec); }
        else if (DateTime.Now - rec.NoGameSince >= TimeSpan.FromSeconds(20))
        {
            try { using var pm = Process.GetProcessById(rec.PmPid); pm.Kill(); }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
        return null;
    }

    /// <summary>Foreground check, a few times per second while recording.</summary>
    public void FocusTick()
    {
        FocusTracker? focus;
        lock (_gate) focus = _focus;
        focus?.Tick();
    }

    /// <summary>Analyzes a finished recording and saves the session. The raw recording is only deleted after a successful analysis.</summary>
    public BenchSession? Complete(Recording rec)
    {
        List<Sample> samples = [];
        List<(double From, double To)> away = [];
        lock (_gate)
        {
            if (_liveId == rec.Id)
            {
                samples = [.. _samples];
                away = _focus?.Stop() ?? [];
                _samples.Clear();
                _focus = null;
                _liveId = null;
            }
        }
        if (Current?.Id == rec.Id) File.Delete(RecordingFile);
        if (!File.Exists(rec.Csv)) { Log.Warn($"Benchmark {rec.Game}: no data recorded."); RemoveRaw(rec); return null; }

        FrameStats r;
        try { r = FrameAnalysis.Analyze(rec.Csv, away); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException or OutOfMemoryException)
        {
            Log.Error($"Benchmark {rec.Game}: analysis failed, the recording is kept and analyzed again after the next start ({e.Message}).");
            return null;
        }
        if (r.Error is not null || r.DurationSec < MinSeconds)
        {
            Log.Info($"Benchmark {rec.Game}: session too short or no frames ({r.Error ?? $"{r.DurationSec:N0} s"}), not saved.");
            RemoveRaw(rec);
            return null;
        }

        var focused = samples.Where(s => s.Focused).ToList();
        double? Avg(Func<Sample, double?> pick) => focused.Select(pick).OfType<double>().ToList() is { Count: > 0 } v ? Math.Round(v.Average(), 1) : null;
        var boostShare = samples.Count > 0 ? samples.Count(s => s.Boost) / (double)samples.Count : rec.BoostExpected ? 1 : 0;
        var session = new BenchSession
        {
            Id = rec.Id, Game = rec.Game, Exe = rec.Exe, Start = rec.Start, DurationSec = Math.Round(r.DurationSec),
            Frames = r.Frames, AvgFps = Math.Round(r.AvgFps, 1), Low1 = Math.Round(r.Low1, 1), Low01 = Math.Round(r.Low01, 1),
            AvgMs = Math.Round(r.AvgMs, 2), P99Ms = Math.Round(r.P99Ms, 2), P999Ms = Math.Round(r.P999Ms, 2),
            StuttersPerMin = Math.Round(r.Stutters / (r.DurationSec / 60), 1), Hitches = r.Hitches,
            Cap = FrameAnalysis.DetectCap(r.Series), Analysis = FrameAnalysis.Version,
            ExcludedSec = Math.Round(r.ExcludedSec), TabOuts = away.Count, Limits = rec.Limits,
            Boost = boostShare >= 0.5, GpuProfile = Settings.GetString("ActiveGpuTune", ""),
            GpuW = Avg(s => s.GpuW), GpuMHz = Avg(s => s.GpuMHz), GpuC = Avg(s => s.GpuC), GpuUtil = Avg(s => s.GpuUtil), CpuLoad = Avg(s => s.CpuLoad),
            Series = r.Series,
        };
        BenchStore.Save(session);
        RemoveRaw(rec);
        Log.Info($"Benchmark: time column '{r.TimeColumn}', {away.Count} alt-tab interval(s), {r.ExcludedSec:N0} s left out.");
        Log.Ok($"Benchmark {rec.Game}: {r.AvgFps:N0} FPS avg, 1% low {r.Low1:N0}, 0.1% low {r.Low01:N0} ({r.DurationSec / 60:N0} min, {(session.Boost ? "with" : "without")} Game Boost).");
        return session;
    }

    private static GameEntry? RunningGame()
    {
        var games = BoostConfig.Load().Games.GroupBy(g => g.ProcessName).ToDictionary(g => g.Key, g => g.First());
        foreach (var p in Process.GetProcesses())
            using (p)
                if (games.TryGetValue(p.ProcessName.ToLowerInvariant(), out var g)) return g;
        return null;
    }

    private static bool IsRunning(string exe)
    {
        var procs = Process.GetProcessesByName(BoostConfig.ProcessName(exe));
        foreach (var p in procs) p.Dispose();
        return procs.Length > 0;
    }

    private void StartRecording(GameEntry game)
    {
        Directory.CreateDirectory(RawDir);
        var now = DateTime.Now;
        var id = now.ToString("yyyyMMdd-HHmmss");
        var csv = Path.Combine(RawDir, $"{id}.csv");
        using var pm = PresentMon.Start(game.Exe, csv);
        var cfg = BoostConfig.Load();
        var rec = new Recording
        {
            Id = id, Game = game.Name, Exe = game.Exe, Start = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerSecond), Csv = csv,
            PmPid = pm.Id, PmStart = ProcessControl.StartTime(pm), Limits = [.. FpsLimits.Read(game.Exe)],
            BoostExpected = GameBoost.IsActive || (cfg.AutoBoost && !SkipBoostOnce),
        };
        JsonFile.Write(RecordingFile, rec);
        JsonFile.Write(Path.ChangeExtension(csv, ".json"), rec);   // kept next to the raw file for a later analysis
        Log.Ok($"Benchmark: recording {game.Name} (PresentMon, frame times only, the game is not touched).");
    }

    private static bool RecorderRunning(Recording rec)
    {
        if (rec.PmPid == 0) return false;
        try
        {
            using var p = Process.GetProcessById(rec.PmPid);
            return ProcessControl.StartTime(p) == rec.PmStart;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException) { return false; }
    }

    /// <summary>A raw recording whose analysis never happened (Frametide closed, analysis failed); each is tried once per start.</summary>
    private Recording? FindUnanalyzed()
    {
        if (!Directory.Exists(RawDir)) return null;
        foreach (var csv in Directory.EnumerateFiles(RawDir, "*.csv"))
        {
            var id = Path.GetFileNameWithoutExtension(csv);
            if (InUse(csv) || !_tried.Add(id)) continue;
            Recording? rec = null;
            try { rec = JsonFile.Read<Recording>(Path.ChangeExtension(csv, ".json")); }
            catch (System.Text.Json.JsonException) { }
            if (rec is null) continue;
            rec.Csv = csv;   // the file found here, whatever path the info file names
            Log.Info($"Benchmark: analyzing an earlier recording of {rec.Game} ({rec.Start:g}).");
            return rec;
        }
        return null;
    }

    private static bool InUse(string path)
    {
        try { using var _ = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read); return false; }
        catch (IOException) { return true; }
    }

    private static void RemoveRaw(Recording rec)
    {
        File.Delete(rec.Csv);
        File.Delete(Path.ChangeExtension(rec.Csv, ".json"));
    }

    public void Dispose() => _cpu.Dispose();
}
