using System.Diagnostics;
using System.Text.Json;
using Frametide.Core.Gpu;
using Frametide.Core.Hardware;
using Frametide.Core.Infrastructure;
using Frametide.Core.Windows;

namespace Frametide.Core.Boost;

public sealed record SuspendedProcess(int Pid, string Name, string? Start);

/// <summary>A running game whose priority or affinity was changed, with the values before (null = not changed).</summary>
public sealed record TunedProcess(int Pid, string Name, string Start, int? Priority, long? Affinity);

/// <summary>What START changed. Saved to disk, so STOP can roll back even after a crash of Frametide or the PC.</summary>
public sealed class BoostState
{
    public DateTime StartedAt { get; set; } = DateTime.Now;
    public bool Auto { get; set; }
    public string? Trigger { get; set; }
    public string? PrevScheme { get; set; }
    public List<IfeoEntry> Ifeo { get; set; } = [];
    public List<SuspendedProcess> Suspended { get; set; } = [];
    public bool GpuApplied { get; set; }
    public List<TunedProcess> Tuned { get; set; } = [];

    /// <summary>Game process IDs already handled by the watcher.</summary>
    public List<int> Handled { get; set; } = [];
}

/// <summary>Game Boost: temporary optimizations while playing. START applies them, STOP rolls everything back.</summary>
public static class GameBoost
{
    private static readonly Lock Gate = new();

    /// <summary>Never killed or suspended (system, shell, anti-virus, launchers games depend on, Frametide itself).</summary>
    public static readonly IReadOnlySet<string> Protected = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "system", "idle", "registry", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "svchost", "dwm",
        "explorer", "audiodg", "fontdrvhost", "sihost", "ctfmon", "conhost", "powershell", "pwsh", "taskmgr",
        "steam", "steamwebhelper", "steamservice", "nvcontainer", "nvdisplay.container", "memcompression",
        "securityhealthservice", "msmpeng", "frametide",
    };

    /// <summary>
    /// Anti-cheat and game launchers, never killed or suspended either: a closed or frozen anti-cheat makes the game quit
    /// or gets the player kicked, launchers are needed by the games they started.
    /// </summary>
    public static readonly IReadOnlySet<string> AntiCheat = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "vgc", "vgtray", "easyanticheat", "easyanticheat_eos", "start_protected_game", "beservice", "beservice_x64",
        "faceit", "faceitservice", "faceitclient", "eaanticheat.gameservice", "eaanticheat.gameservicelauncher",
        "pnkbstra", "pnkbstrb", "sguard64", "sguardsvc64",
        "riotclientservices", "riotclientux", "riotclientuxrender", "epicgameslauncher", "epiconlineservices",
        "epiconlineserviceshost", "eadesktop", "eabackgroundservice", "battle.net", "upc", "uplaywebcore", "galaxyclient",
    };

    /// <summary>Raised after START and STOP (on the calling thread).</summary>
    public static event Action? Changed;

    /// <summary>Null when Game Boost is off. An empty or damaged state file counts as off: START and STOP move it aside.</summary>
    public static BoostState? State
    {
        get
        {
            try { return JsonFile.Read<BoostState>(AppPaths.BoostState); }
            catch (JsonException) { return null; }
        }
    }

    public static bool IsActive => State is not null;

    public static bool IsProtected(string exe, BoostConfig cfg) => ProtectionReason(exe, cfg) is not null;

    /// <summary>Why a process is never killed or suspended; null when it may be.</summary>
    public static string? ProtectionReason(string exe, BoostConfig cfg)
    {
        var name = BoostConfig.ProcessName(exe);
        if (AntiCheat.Contains(name)) return "anti-cheat or game launcher, closing or freezing it can end the game or get you kicked";
        if (Protected.Contains(name)) return "needed by Windows, the game or Frametide";
        if (cfg.Games.Any(g => g.ProcessName == name)) return "it is in your game list";
        return null;
    }

    /// <summary>Names of listed games running right now. Reads the process list only, no process is opened.</summary>
    public static IReadOnlyList<string> RunningGames(BoostConfig cfg)
    {
        var want = cfg.Games.GroupBy(g => g.ProcessName).ToDictionary(g => g.Key, g => g.First().Name);
        var found = new List<string>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
                if (want.TryGetValue(p.ProcessName.ToLowerInvariant(), out var name) && !found.Contains(name)) found.Add(name);
        }
        return found;
    }

    /// <summary>Saves the settings; in auto mode the permanent launch priorities follow the game list.</summary>
    public static void SaveConfig(BoostConfig cfg)
    {
        lock (Gate)
        {
            cfg.AutoBoost = BoostConfig.Load().AutoBoost;   // only SetAutoBoost changes it (the tray menu may have)
            cfg.Save();
            if (cfg.AutoBoost) LaunchPriority.EnablePersistent(cfg);
        }
        Changed?.Invoke();
    }

    public static void SetAutoBoost(bool on)
    {
        lock (Gate)
        {
            var cfg = BoostConfig.Load();
            cfg.AutoBoost = on;
            cfg.Save();
            // A running manual boost hands its launch priorities over, otherwise they would be backed up as "original" values.
            if (on && State is { Ifeo.Count: > 0 } running)
            {
                running.Ifeo = LaunchPriority.Restore(running.Ifeo);
                Save(running);
            }
            if (on) LaunchPriority.EnablePersistent(cfg); else LaunchPriority.DisablePersistent();
            Log.Ok(on ? "Auto Game Boost enabled." : "Auto Game Boost disabled.");
        }
        Changed?.Invoke();
    }

    public static void Start(bool auto = false, string? trigger = null)
    {
        try
        {
            lock (Gate)
            {
                SetAsideUnreadable();
                if (IsActive) { Log.Warn("Game Boost is already running."); return; }
                var cfg = BoostConfig.Load();
                var state = new BoostState { Auto = auto, Trigger = trigger };
                Log.Info(auto ? $"=== Game Boost START (auto: {trigger}) ===" : "=== Game Boost START ===");
                // Every change is on disk before it is made, so STOP can clean up after an error or a crash at any point.
                Save(state);
                try { Apply(cfg, state); }
                finally { Save(state); }
                Log.Ok("Game Boost active. Waiting for the game to set priority and affinity.");
            }
        }
        finally { Changed?.Invoke(); }
        Watch();
    }

    private static void Apply(BoostConfig cfg, BoostState state)
    {
        if (cfg.PowerPlan != BoostConfig.KeepPowerPlan)
        {
            var active = PowerCfg.GetSchemes().FirstOrDefault(s => s.Active);
            if (active is not null && !active.Guid.Equals(cfg.PowerPlan, StringComparison.OrdinalIgnoreCase))
            {
                state.PrevScheme = active.Guid;
                Save(state);
                if (PowerCfg.SetActive(cfg.PowerPlan)) Log.Ok($"Power plan switched (was: {active.Name}).");
                else
                {
                    state.PrevScheme = null;
                    Save(state);
                    Log.Warn("Could not switch the power plan (does the selected plan still exist?).");
                }
            }
        }

        // In auto mode the launch priorities are set permanently (see LaunchPriority).
        if (!cfg.AutoBoost)
        {
            state.Ifeo = LaunchPriority.Set(cfg.Games, done => { state.Ifeo = [.. done]; Save(state); });
            Save(state);
        }

        foreach (var exe in cfg.KillList)
        {
            if (ProtectionReason(exe, cfg) is { } why) { Log.Warn($"{exe} is protected ({why}), not closed."); continue; }
            var procs = Process.GetProcessesByName(BoostConfig.ProcessName(exe));
            var killed = 0;
            foreach (var p in procs)
            {
                using (p)
                {
                    try { p.Kill(); killed++; }
                    catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { Log.Warn($"Closing {exe} ({p.Id}) failed: {e.Message}"); }
                }
            }
            if (killed > 0) Log.Ok($"Closed: {exe} ({killed}x).");
        }

        foreach (var exe in cfg.SuspendList)
        {
            if (ProtectionReason(exe, cfg) is { } why) { Log.Warn($"{exe} is protected ({why}), not suspended."); continue; }
            foreach (var p in Process.GetProcessesByName(BoostConfig.ProcessName(exe)))
            {
                using (p)
                {
                    // Without the start time the process could not be told apart from a later one with the same PID.
                    if (ProcessControl.StartTime(p) is not { } start) { Log.Warn($"{p.ProcessName} ({p.Id}) not suspended: it could not be recognised again for resuming."); continue; }
                    var entry = new SuspendedProcess(p.Id, p.ProcessName, start);
                    state.Suspended.Add(entry);
                    Save(state);
                    var rc = ProcessControl.Suspend(p.Id);
                    if (rc == 0) continue;
                    state.Suspended.Remove(entry);
                    Save(state);
                    Log.Warn($"Suspending {p.ProcessName} ({p.Id}) failed: 0x{rc:X8}");
                }
            }
        }
        if (state.Suspended.Count > 0) Log.Ok($"Suspended: {state.Suspended.Count} process(es).");

        if (cfg.GpuProfile.Length > 0)
        {
            // Recorded first: a profile applied only partly is reset on STOP as well.
            state.GpuApplied = true;
            Save(state);
            try { GpuTuning.ApplyProfile(cfg.GpuProfile); }
            catch (InvalidOperationException e) { Log.Error($"GPU profile: {e.Message}"); }
        }

        if (cfg.ClearShaderCache) ShaderCache.Clear();
        if (cfg.FlushDns)
        {
            var r = NativeProcess.Run("ipconfig.exe", ["/flushdns"]);
            if (r.ExitCode == 0) Log.Ok("DNS cache flushed."); else Log.Warn("Flushing the DNS cache failed.");
        }
    }

    public static void Stop()
    {
        try
        {
            lock (Gate)
            {
                SetAsideUnreadable();
                if (State is not { } state) { Log.Info("Game Boost is not active."); return; }
                var cfg = BoostConfig.Load();
                Log.Info("=== Game Boost STOP ===");

                RestoreTuned(state.Tuned);

                var stillSuspended = new List<SuspendedProcess>();
                var resumed = 0;
                foreach (var s in state.Suspended)
                {
                    // PIDs are reused: only resume when name and start time still match.
                    if (s.Start is null) { Log.Warn($"{s.Name} ({s.Pid}) not resumed: its start time is unknown, the PID may belong to another process now."); continue; }
                    try
                    {
                        using var p = Process.GetProcessById(s.Pid);
                        if (p.ProcessName != s.Name || ProcessControl.StartTime(p) != s.Start) continue;
                        var rc = ProcessControl.Resume(s.Pid);
                        if (rc == 0) resumed++;
                        else { stillSuspended.Add(s); Log.Warn($"Resuming {s.Name} ({s.Pid}) failed: 0x{rc:X8}"); }
                    }
                    catch (Exception e) when (e is ArgumentException or InvalidOperationException) { }   // process is gone
                }
                if (state.Suspended.Count > 0) Log.Ok($"Resumed: {resumed} of {state.Suspended.Count} process(es).");

                string? prevScheme = null;
                if (state.PrevScheme is not null)
                {
                    if (PowerCfg.SetActive(state.PrevScheme)) Log.Ok("Power plan restored.");
                    else if (PowerCfg.GetSchemes().Any(s => s.Guid.Equals(state.PrevScheme, StringComparison.OrdinalIgnoreCase)))
                    {
                        prevScheme = state.PrevScheme;
                        Log.Warn("Could not restore the power plan.");
                    }
                    else Log.Warn("Could not restore the power plan, it no longer exists.");
                }

                if (state.GpuApplied && !cfg.GpuKeepAfterStop)
                {
                    try { GpuTuning.Reset(); GpuTuning.SetActive(""); Log.Ok("GPU reset to default."); }
                    catch (InvalidOperationException e) { Log.Warn($"GPU reset: {e.Message}"); }
                }

                var ifeoFailed = LaunchPriority.Restore(state.Ifeo);
                // Auto mode keeps launch priorities permanently: make sure they survive this STOP.
                if (cfg.AutoBoost && state.Ifeo.Count > 0) LaunchPriority.EnablePersistent(cfg);

                if (stillSuspended.Count == 0 && prevScheme is null && ifeoFailed.Count == 0)
                {
                    File.Delete(AppPaths.BoostState);
                    Log.Ok("Game Boost stopped.");
                    return;
                }
                // Only what failed stays, so the next STOP retries exactly that.
                state.Suspended = stillSuspended;
                state.PrevScheme = prevScheme;
                state.Ifeo = ifeoFailed;
                state.GpuApplied = false;
                state.Tuned = [];
                state.Handled = [];
                Save(state);
                Log.Warn("Game Boost stopped, but some changes could not be rolled back. They are kept: press STOP again to retry.");
            }
        }
        finally { Changed?.Invoke(); }
    }

    /// <summary>An empty or damaged state file is moved aside, otherwise it would block Game Boost.</summary>
    private static void SetAsideUnreadable()
    {
        var path = AppPaths.BoostState;
        if (!File.Exists(path)) return;
        try { if (JsonFile.Read<BoostState>(path) is not null) return; }
        catch (JsonException) { }
        var aside = $"{path}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
        File.Move(path, aside, overwrite: true);
        Log.Warn($"The Game Boost state file was empty or damaged and was moved to {Path.GetFileName(aside)}. What that session changed cannot be "
                 + "rolled back automatically: check the power plan, and restart apps that may still be frozen.");
    }

    /// <summary>Puts back the priority and affinity of game processes changed by <see cref="Tune"/> (best effort).</summary>
    private static void RestoreTuned(IEnumerable<TunedProcess> tuned)
    {
        var count = 0;
        foreach (var t in tuned)
        {
            try
            {
                using var p = Process.GetProcessById(t.Pid);
                if (p.ProcessName != t.Name || ProcessControl.StartTime(p) != t.Start) continue;
                if (t.Priority is { } prio) p.PriorityClass = (ProcessPriorityClass)prio;
                if (t.Affinity is { } mask) p.ProcessorAffinity = (IntPtr)mask;
                count++;
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
        if (count > 0) Log.Ok($"Priority and affinity put back for {count} game process(es).");
    }

    /// <summary>
    /// Handles games started while Game Boost is active (called every few seconds). The priority normally comes from
    /// the launch priority, so the game process is not opened. A process handle is only used for a custom core
    /// affinity, or for the priority when no launch priority covers the game (it was running before START).
    /// </summary>
    public static void Watch()
    {
        lock (Gate)
        {
            if (State is not { } state) return;
            var cfg = BoostConfig.Load();
            var viaLaunch = state.Ifeo.Concat(cfg.AutoBoost ? LaunchPriority.Persistent : [])
                .Select(e => e.Exe).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var changed = false;
            foreach (var g in cfg.Games)
            {
                foreach (var p in Process.GetProcessesByName(g.ProcessName))
                {
                    using (p)
                    {
                        if (state.Handled.Contains(p.Id)) continue;
                        state.Handled.Add(p.Id);
                        changed = true;
                        Log.Ok($"{g.Name} detected (PID {p.Id}): {Tune(p, g, viaLaunch.Contains(g.Exe), state)}");
                    }
                }
            }
            if (changed) Save(state);
        }
    }

    private static string Tune(Process p, GameEntry g, bool viaLaunch, BoostState state)
    {
        var info = new List<string>();
        int? prevPriority = null;
        long? prevAffinity = null;
        if (g.Priority != GamePriority.Normal)
        {
            if (viaLaunch) info.Add($"priority {g.Priority} via launch priority (process not touched)");
            else
            {
                try
                {
                    var before = p.PriorityClass;
                    p.PriorityClass = g.Priority == GamePriority.High ? ProcessPriorityClass.High : ProcessPriorityClass.AboveNormal;
                    prevPriority = (int)before;
                    info.Add($"priority {g.Priority}");
                }
                catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    info.Add("priority unchanged (process is protected, e.g. by anti-cheat; the launch priority applies to games started while Game Boost is active)");
                }
            }
        }
        if (g.Affinity != CoreAffinity.All)
        {
            var m = CpuTopology.ReadMasks();
            ulong mask = g.Affinity switch
            {
                CoreAffinity.NoCore0 => m.All & ~m.Core0,
                CoreAffinity.PCores when m.Hybrid => m.PCores,
                _ => 0,
            };
            if (mask != 0)
            {
                try
                {
                    var before = (long)p.ProcessorAffinity;
                    p.ProcessorAffinity = (IntPtr)(long)mask;
                    prevAffinity = before;
                    info.Add($"affinity 0x{mask:X}");
                }
                catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { info.Add("affinity unchanged (process is protected)"); }
            }
        }
        // Put back on STOP; a process that cannot be recognised again (no start time) keeps its values.
        if ((prevPriority is not null || prevAffinity is not null) && ProcessControl.StartTime(p) is { } start)
            state.Tuned.Add(new TunedProcess(p.Id, p.ProcessName, start, prevPriority, prevAffinity));
        return info.Count > 0 ? string.Join(", ", info) : "nothing to change";
    }

    private static void Save(BoostState state) => JsonFile.Write(AppPaths.BoostState, state);
}
