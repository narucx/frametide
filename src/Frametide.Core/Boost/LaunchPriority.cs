using Frametide.Core.Infrastructure;
using Frametide.Core.Windows;
using Microsoft.Win32;

namespace Frametide.Core.Boost;

/// <summary>What was in the registry before a launch priority was set, so it can be put back exactly.</summary>
public sealed class IfeoEntry
{
    public string Exe { get; set; } = "";
    public bool ExeKeyCreated { get; set; }
    public bool PerfKeyCreated { get; set; }
    public bool PrevExists { get; set; }
    public int? PrevValue { get; set; }
}

public sealed class IfeoState
{
    public List<IfeoEntry> Ifeo { get; set; } = [];
}

/// <summary>
/// Game priority through Image File Execution Options (<c>PerfOptions\CpuPriorityClass</c>). Windows applies it itself
/// when the process starts, so the game process is never opened. This also works for games protected by anti-cheat,
/// which block priority changes on the running process.
/// </summary>
public static class LaunchPriority
{
    /// <summary>Registry root; tests point it to HKCU.</summary>
    internal static string Root { get; set; } = @"HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";

    // CpuPriorityClass values: 1 Idle, 2 Normal, 3 High, 5 Below normal, 6 Above normal.
    private static int? ClassOf(GamePriority p) => p switch { GamePriority.AboveNormal => 6, GamePriority.High => 3, _ => null };

    public static List<IfeoEntry> Set(IEnumerable<GameEntry> games)
    {
        var done = new List<IfeoEntry>();
        foreach (var g in games)
        {
            if (ClassOf(g.Priority) is not { } cls || !BoostConfig.IsValidExe(g.Exe)) continue;
            var exeKey = $@"{Root}\{g.Exe}";
            var perfKey = $@"{exeKey}\PerfOptions";
            var prev = Reg.Get(perfKey, "CpuPriorityClass");
            var entry = new IfeoEntry
            {
                Exe = g.Exe, ExeKeyCreated = !Reg.KeyExists(exeKey), PerfKeyCreated = !Reg.KeyExists(perfKey),
                PrevExists = prev.Exists, PrevValue = prev.Value is int v ? v : null,
            };
            try
            {
                Reg.Set(perfKey, "CpuPriorityClass", RegistryValueKind.DWord, cls);
                done.Add(entry);
            }
            catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
                Log.Warn($"Launch priority for {g.Exe} failed: {e.Message}");
            }
        }
        if (done.Count > 0) Log.Ok($"Launch priority set for {done.Count} game(s) (applies to games started from now on).");
        return done;
    }

    public static void Restore(IEnumerable<IfeoEntry> entries)
    {
        var count = 0;
        foreach (var e in entries)
        {
            if (!BoostConfig.IsValidExe(e.Exe)) continue;
            var exeKey = $@"{Root}\{e.Exe}";
            var perfKey = $@"{exeKey}\PerfOptions";
            try
            {
                if (e.PrevExists && e.PrevValue is { } prev) Reg.Set(perfKey, "CpuPriorityClass", RegistryValueKind.DWord, prev);
                else
                {
                    Reg.Remove(perfKey, "CpuPriorityClass");
                    // Only keys created by Frametide and empty now are removed.
                    if (e.PerfKeyCreated) Reg.DeleteKeyIfEmpty(perfKey);
                    if (e.ExeKeyCreated) Reg.DeleteKeyIfEmpty(exeKey);
                }
                count++;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
                Log.Warn($"Restoring launch priority for {e.Exe}: {ex.Message}");
            }
        }
        if (count > 0) Log.Ok("Launch priorities removed.");
    }

    // While Auto Game Boost is on, the launch priorities stay set permanently: auto mode only notices the game once it
    // is already running, so the setting has to exist before the game starts.

    public static IReadOnlyList<IfeoEntry> Persistent => JsonFile.Read<IfeoState>(AppPaths.IfeoState)?.Ifeo ?? [];

    public static void EnablePersistent(BoostConfig cfg)
    {
        DisablePersistent();
        JsonFile.Write(AppPaths.IfeoState, new IfeoState { Ifeo = Set(cfg.Games) });
    }

    public static void DisablePersistent()
    {
        if (JsonFile.Read<IfeoState>(AppPaths.IfeoState) is { } state) Restore(state.Ifeo);
        JsonFile.Delete(AppPaths.IfeoState);
    }
}
