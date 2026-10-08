using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Frametide.Core.Infrastructure;
using Frametide.Core.Windows;

namespace Frametide.Core.Boost;

public enum LassoRule { Affinity, Priority, GamingMode }

public sealed record LassoConflict(LassoRule Rule, string Target, string Value);

public sealed record LassoState(bool Installed, bool Running, bool HasConfig, IReadOnlyList<LassoConflict> Conflicts);

/// <summary>
/// Process Lasso rules for the same games collide with Game Boost (whoever sets last wins). Detects them and can remove
/// the game rules from the Lasso configuration (with a backup).
/// </summary>
public static class ProcessLasso
{
    private static readonly string Ini = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"ProcessLasso\config\prolasso.ini");
    private static readonly string Exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Process Lasso\ProcessLasso.exe");
    private const string Service = "ProcessGovernor";

    public static LassoState Check(IEnumerable<string> gameExes)
    {
        var games = gameExes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var running = Process.GetProcessesByName("ProcessLasso").Concat(Process.GetProcessesByName("ProcessGovernor")).ToList();
        var isRunning = running.Count > 0;
        running.ForEach(p => p.Dispose());
        var text = ReadIni();
        var conflicts = new List<LassoConflict>();
        if (text is not null)
        {
            foreach (var e in Split(Value(text, "DefaultAffinitiesEx"), 3))
                if (games.Contains(e[0])) conflicts.Add(new LassoConflict(LassoRule.Affinity, e[0], e[2]));
            foreach (var e in Split(Value(text, "DefaultPriorities"), 2))
                if (games.Contains(e[0])) conflicts.Add(new LassoConflict(LassoRule.Priority, e[0], e[1]));
            if (Value(text, "GamingModeEnabled") == "true")
                conflicts.Add(new LassoConflict(LassoRule.GamingMode, "", Value(text, "TargetPowerPlan") ?? ""));
        }
        return new LassoState(File.Exists(Exe), isRunning, text is not null, conflicts);
    }

    /// <summary>Removes the affinity and priority rules for the games. Lasso is stopped meanwhile, it would write the file back on exit.</summary>
    public static bool RemoveGameRules(IEnumerable<string> gameExes)
    {
        var text = ReadIni() ?? throw new InvalidOperationException("No Process Lasso configuration found.");
        var games = gameExes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var updated = text;
        foreach (var (key, width) in new[] { ("DefaultAffinitiesEx", 3), ("DefaultPriorities", 2) })
        {
            if (Value(text, key) is not { } val) continue;
            var kept = Split(val, width).Where(e => !games.Contains(e[0])).Select(e => string.Join(",", e));
            var line = $"{key}={string.Join(",", kept)}";
            // [^\r\n]* instead of .*: "." also matches the \r of CRLF, which would be lost.
            updated = Regex.Replace(updated, $@"(?m)^{Regex.Escape(key)}=[^\r\n]*", _ => line);
        }
        if (updated == text) { Log.Info("No Lasso rules found for the games."); return false; }

        var backup = Path.Combine(AppPaths.DataDir, $"prolasso-backup-{DateTime.Now:yyyyMMdd-HHmmss}.ini");
        File.Copy(Ini, backup);
        Log.Info($"Lasso backup: {backup}");

        string? gui = null;
        foreach (var p in Process.GetProcessesByName("ProcessLasso"))
        {
            using (p)
            {
                try { gui ??= p.MainModule?.FileName; p.Kill(); p.WaitForExit(3000); }
                catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { }
            }
        }
        NativeProcess.Run("sc.exe", ["stop", Service]);
        Thread.Sleep(800);
        File.WriteAllText(Ini, updated, Encoding.Unicode);
        NativeProcess.Run("sc.exe", ["start", Service]);
        if (gui is not null) Process.Start(new ProcessStartInfo(gui, "/tray") { UseShellExecute = true })?.Dispose();
        Log.Ok("Lasso game rules removed, Lasso restarted.");
        return true;
    }

    private static string? ReadIni() => File.Exists(Ini) ? File.ReadAllText(Ini, Encoding.Unicode) : null;

    private static string? Value(string text, string key)
    {
        var m = Regex.Match(text, $"(?m)^{Regex.Escape(key)}=(.*)$");
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    /// <summary>"a.exe,x,b.exe,y" -> [a.exe, x], [b.exe, y] (or triples).</summary>
    internal static IEnumerable<string[]> Split(string? value, int width)
    {
        if (string.IsNullOrEmpty(value)) yield break;
        var parts = value.Split(',');
        for (var i = 0; i + width <= parts.Length; i += width) yield return parts[i..(i + width)];
    }
}
