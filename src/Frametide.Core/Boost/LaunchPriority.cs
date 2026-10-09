using System.Text.Json;
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

    /// <param name="journal">Called with all entries so far before each registry change, so the originals are on disk first.</param>
    public static List<IfeoEntry> Set(IEnumerable<GameEntry> games, Action<IReadOnlyList<IfeoEntry>>? journal = null)
    {
        var done = new List<IfeoEntry>();
        foreach (var g in games.DistinctBy(g => g.Exe, StringComparer.OrdinalIgnoreCase))
        {
            if (ClassOf(g.Priority) is not { } cls || !BoostConfig.IsValidExe(g.Exe)) continue;
            var exeKey = $@"{Root}\{g.Exe}";
            var perfKey = $@"{exeKey}\PerfOptions";
            var prev = Reg.Get(perfKey, "CpuPriorityClass");
            // A value of another type could not be put back exactly, so it is left alone.
            if (prev.Exists && prev.Kind != RegistryValueKind.DWord)
            {
                Log.Warn($"Launch priority for {g.Exe} skipped: CpuPriorityClass already exists as {prev.Kind}.");
                continue;
            }
            var entry = new IfeoEntry
            {
                Exe = g.Exe, ExeKeyCreated = !Reg.KeyExists(exeKey), PerfKeyCreated = !Reg.KeyExists(perfKey),
                PrevExists = prev.Exists, PrevValue = prev.Value is int v ? v : null,
            };
            done.Add(entry);
            try
            {
                journal?.Invoke(done);
                Reg.Set(perfKey, "CpuPriorityClass", RegistryValueKind.DWord, cls);
            }
            catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
                done.Remove(entry);
                Log.Warn($"Launch priority for {g.Exe} failed: {e.Message}");
            }
        }
        if (done.Count > 0) Log.Ok($"Launch priority set for {done.Count} game(s) (applies to games started from now on).");
        return done;
    }

    /// <summary>Restores in reverse order (the last change first). Returns the entries that could not be restored.</summary>
    public static List<IfeoEntry> Restore(IEnumerable<IfeoEntry> entries)
    {
        var count = 0;
        var failed = new List<IfeoEntry>();
        foreach (var e in entries.Reverse())
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
                failed.Insert(0, e);   // keeps the original order
                Log.Warn($"Restoring launch priority for {e.Exe}: {ex.Message}");
            }
        }
        if (count > 0) Log.Ok("Launch priorities removed.");
        return failed;
    }

    // While Auto Game Boost is on, the launch priorities stay set permanently: auto mode only notices the game once it
    // is already running, so the setting has to exist before the game starts.

    public static IReadOnlyList<IfeoEntry> Persistent
    {
        get
        {
            try { return JsonFile.Read<IfeoState>(AppPaths.IfeoState)?.Ifeo ?? []; }
            catch (JsonException) { return []; }
        }
    }

    public static void EnablePersistent(BoostConfig cfg)
    {
        if (!TryReadPersistent(out var state)) return;
        // Entries that could not be restored stay in front of the new ones: restored last, so their originals win.
        List<IfeoEntry> kept = state is null ? [] : RestoreAndKeepFailed(state.Ifeo);
        var set = Set(cfg.Games, done => JsonFile.Write(AppPaths.IfeoState, new IfeoState { Ifeo = [.. kept, .. done] }));
        if (kept.Count == 0 && set.Count == 0) JsonFile.Delete(AppPaths.IfeoState);
        else JsonFile.Write(AppPaths.IfeoState, new IfeoState { Ifeo = [.. kept, .. set] });
    }

    public static void DisablePersistent()
    {
        if (!TryReadPersistent(out var state)) return;
        if (state is not null && RestoreAndKeepFailed(state.Ifeo).Count > 0) return;
        JsonFile.Delete(AppPaths.IfeoState);
    }

    /// <summary>Restores the entries; the ones that failed are written back to the backup, so it never loses them.</summary>
    private static List<IfeoEntry> RestoreAndKeepFailed(List<IfeoEntry> entries)
    {
        var failed = Restore(entries);
        if (failed.Count > 0)
        {
            JsonFile.Write(AppPaths.IfeoState, new IfeoState { Ifeo = failed });
            Log.Warn($"{failed.Count} launch priority value(s) could not be restored. They stay in the backup and are retried next time.");
        }
        return failed;
    }

    /// <summary>False when the backup exists but cannot be read: then nothing is changed, the originals would be lost.</summary>
    private static bool TryReadPersistent(out IfeoState? state)
    {
        try
        {
            state = JsonFile.Read<IfeoState>(AppPaths.IfeoState);
            return true;
        }
        catch (JsonException e)
        {
            state = null;
            Log.Warn($"The launch priority backup ({AppPaths.IfeoState}) cannot be read, launch priorities are left unchanged: {e.Message}");
            return false;
        }
    }
}
