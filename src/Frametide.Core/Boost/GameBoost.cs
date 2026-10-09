using System.Diagnostics;
using Frametide.Core.Gpu;
using Frametide.Core.Hardware;
using Frametide.Core.Infrastructure;
using Frametide.Core.Windows;

namespace Frametide.Core.Boost;

public sealed record SuspendedProcess(int Pid, string Name, string? Start);

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

    /// <summary>Raised after START and STOP (on the calling thread).</summary>
    public static event Action? Changed;

    public static BoostState? State => JsonFile.Read<BoostState>(AppPaths.BoostState);

    public static bool IsActive => File.Exists(AppPaths.BoostState);

    public static bool IsProtected(string exe, BoostConfig cfg)
    {
        var name = BoostConfig.ProcessName(exe);
        return Protected.Contains(name) || cfg.Games.Any(g => g.ProcessName == name);
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
                LaunchPriority.Restore(running.Ifeo);
                running.Ifeo = [];
                Save(running);
            }
            if (on) LaunchPriority.EnablePersistent(cfg); else LaunchPriority.DisablePersistent();
            Log.Ok(on ? "Auto Game Boost enabled." : "Auto Game Boost disabled.");
        }
        Changed?.Invoke();
    }

    public static void Start(bool auto = false, string? trigger = null)
    {
        lock (Gate)
        {
            if (IsActive) { Log.Warn("Game Boost is already running."); return; }
            var cfg = BoostConfig.Load();
            var state = new BoostState { Auto = auto, Trigger = trigger };
            Log.Info(auto ? $"=== Game Boost START (auto: {trigger}) ===" : "=== Game Boost START ===");

            if (cfg.PowerPlan != BoostConfig.KeepPowerPlan)
            {
                var active = PowerCfg.GetSchemes().FirstOrDefault(s => s.Active);
                if (active is not null && !active.Guid.Equals(cfg.PowerPlan, StringComparison.OrdinalIgnoreCase))
                {
                    if (PowerCfg.SetActive(cfg.PowerPlan)) { state.PrevScheme = active.Guid; Log.Ok($"Power plan switched (was: {active.Name})."); }
                    else Log.Warn("Could not switch the power plan (does the selected plan still exist?).");
                }
            }

            // In auto mode the launch priorities are set permanently (see LaunchPriority).
            if (!cfg.AutoBoost) state.Ifeo = LaunchPriority.Set(cfg.Games);
            Save(state);   // early, so STOP can always clean up

            foreach (var exe in cfg.KillList)
            {
                if (IsProtected(exe, cfg)) { Log.Warn($"{exe} is protected, not closed."); continue; }
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
                if (IsProtected(exe, cfg)) { Log.Warn($"{exe} is protected, not suspended."); continue; }
                foreach (var p in Process.GetProcessesByName(BoostConfig.ProcessName(exe)))
                {
                    using (p)
                    {
                        var rc = ProcessControl.Suspend(p.Id);
                        if (rc == 0) state.Suspended.Add(new SuspendedProcess(p.Id, p.ProcessName, ProcessControl.StartTime(p)));
                        else Log.Warn($"Suspending {p.ProcessName} ({p.Id}) failed: 0x{rc:X8}");
                    }
                }
            }
            if (state.Suspended.Count > 0) Log.Ok($"Suspended: {state.Suspended.Count} process(es).");

            if (cfg.GpuProfile.Length > 0)
            {
                try { GpuTuning.ApplyProfile(cfg.GpuProfile); state.GpuApplied = true; }
                catch (InvalidOperationException e) { Log.Error($"GPU profile: {e.Message}"); }
            }

            if (cfg.ClearShaderCache) ShaderCache.Clear();
            if (cfg.FlushDns)
            {
                var r = NativeProcess.Run("ipconfig.exe", ["/flushdns"]);
                if (r.ExitCode == 0) Log.Ok("DNS cache flushed."); else Log.Warn("Flushing the DNS cache failed.");
            }

            Save(state);
            Log.Ok("Game Boost active. Waiting for the game to set priority and affinity.");
        }
        Changed?.Invoke();
        Watch();
    }

    public static void Stop()
    {
        lock (Gate)
        {
            if (State is not { } state) { Log.Info("Game Boost is not active."); return; }
            var cfg = BoostConfig.Load();
            Log.Info("=== Game Boost STOP ===");

            var resumed = 0;
            foreach (var s in state.Suspended)
            {
                try
                {
                    using var p = Process.GetProcessById(s.Pid);
                    // PIDs are reused: only resume when name and start time still match.
                    if (p.ProcessName == s.Name && ProcessControl.StartTime(p) == s.Start && ProcessControl.Resume(s.Pid) == 0) resumed++;
                }
                catch (Exception e) when (e is ArgumentException or InvalidOperationException) { }   // process is gone
            }
            if (state.Suspended.Count > 0) Log.Ok($"Resumed: {resumed} of {state.Suspended.Count} process(es).");

            if (state.PrevScheme is not null)
            {
                if (PowerCfg.SetActive(state.PrevScheme)) Log.Ok("Power plan restored."); else Log.Warn("Could not restore the power plan.");
            }

            if (state.GpuApplied && !cfg.GpuKeepAfterStop)
            {
                try { GpuTuning.Reset(); GpuTuning.SetActive(""); Log.Ok("GPU reset to default."); }
                catch (InvalidOperationException e) { Log.Warn($"GPU reset: {e.Message}"); }
            }

            LaunchPriority.Restore(state.Ifeo);
            // Auto mode keeps launch priorities permanently: make sure they survive this STOP.
            if (cfg.AutoBoost && state.Ifeo.Count > 0) LaunchPriority.EnablePersistent(cfg);

            JsonFile.Delete(AppPaths.BoostState);
            Log.Ok("Game Boost stopped.");
        }
        Changed?.Invoke();
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
                        Log.Ok($"{g.Name} detected (PID {p.Id}): {Tune(p, g, viaLaunch.Contains(g.Exe))}");
                    }
                }
            }
            if (changed) Save(state);
        }
    }

    private static string Tune(Process p, GameEntry g, bool viaLaunch)
    {
        var info = new List<string>();
        if (g.Priority != GamePriority.Normal)
        {
            if (viaLaunch) info.Add($"priority {g.Priority} via launch priority (process not touched)");
            else
            {
                try
                {
                    p.PriorityClass = g.Priority == GamePriority.High ? ProcessPriorityClass.High : ProcessPriorityClass.AboveNormal;
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
                try { p.ProcessorAffinity = (IntPtr)(long)mask; info.Add($"affinity 0x{mask:X}"); }
                catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { info.Add("affinity unchanged (process is protected)"); }
            }
        }
        return info.Count > 0 ? string.Join(", ", info) : "nothing to change";
    }

    private static void Save(BoostState state) => JsonFile.Write(AppPaths.BoostState, state);
}
