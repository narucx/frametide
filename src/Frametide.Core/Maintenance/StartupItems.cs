using System.Diagnostics;
using System.Text.RegularExpressions;
using Frametide.Core.Infrastructure;
using Frametide.Core.Windows;
using Microsoft.Win32;

namespace Frametide.Core.Maintenance;

public enum StartupKind { Registry, Folder, Task }

public sealed record StartupItem(StartupKind Kind, string Name, string Command, string Source, bool Enabled, string Publisher,
    string? ApprovedKey = null, string? ApprovedName = null, string? TaskPath = null);

/// <summary>
/// Programs that start with Windows: Run keys, Startup folders and non-Microsoft scheduled tasks with a sign-in or boot
/// trigger. Disabling works like the Startup tab in Task Manager (StartupApproved values) or by disabling the task,
/// so nothing is deleted and everything can be switched back on.
/// </summary>
public static partial class StartupItems
{
    private const string Approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";

    private static readonly (string Path, string ApprovedKey, string Source)[] RunKeys =
    [
        (@"HKCU:\Software\Microsoft\Windows\CurrentVersion\Run", $@"HKCU:\{Approved}\Run", "Registry (user)"),
        (@"HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run", $@"HKLM:\{Approved}\Run", "Registry (all users)"),
        (@"HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", $@"HKLM:\{Approved}\Run32", "Registry (all users, 32-bit)"),
    ];

    public static IReadOnlyList<StartupItem> All()
    {
        var items = new List<StartupItem>();
        foreach (var (path, approved, source) in RunKeys)
        {
            var (root, sub) = Reg.Parse(path);
            using (root)
            using (var key = root.OpenSubKey(sub))
            {
                if (key is null) continue;
                foreach (var name in key.GetValueNames().Where(n => n.Length > 0))
                {
                    var cmd = key.GetValue(name)?.ToString() ?? "";
                    items.Add(new StartupItem(StartupKind.Registry, name, cmd, source, IsApproved(approved, name), Publisher(ExePath(cmd)), approved, name));
                }
            }
        }

        foreach (var (folder, approved, source) in new[]
                 {
                     (Environment.GetFolderPath(Environment.SpecialFolder.Startup), $@"HKCU:\{Approved}\StartupFolder", "Startup folder (user)"),
                     (Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), $@"HKLM:\{Approved}\StartupFolder", "Startup folder (all users)"),
                 })
        {
            if (!Directory.Exists(folder)) continue;
            foreach (var file in new DirectoryInfo(folder).EnumerateFiles().Where(f => !f.Name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)))
            {
                var target = file.Extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase) ? ShortcutTarget(file.FullName) ?? file.FullName : file.FullName;
                items.Add(new StartupItem(StartupKind.Folder, Path.GetFileNameWithoutExtension(file.Name), target, source,
                    IsApproved(approved, file.Name), Publisher(target), approved, file.Name));
            }
        }

        try { items.AddRange(Tasks()); }
        catch (Exception e) when (IsAccessError(e)) { Log.Warn($"Scheduled tasks could not be read: {e.Message}"); }
        return items.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static void SetEnabled(StartupItem item, bool enabled)
    {
        if (item.Kind == StartupKind.Task)
        {
            dynamic service = TaskService();
            dynamic task = service.GetFolder(item.TaskPath!).GetTask(item.Name);
            task.Enabled = enabled;
        }
        else
        {
            // StartupApproved: 12 bytes, first byte even = enabled, odd = disabled (then the FILETIME of disabling).
            var bytes = new byte[12];
            bytes[0] = (byte)(enabled ? 2 : 3);
            if (!enabled) BitConverter.GetBytes(DateTime.Now.ToFileTime()).CopyTo(bytes, 4);
            Reg.Set(item.ApprovedKey!, item.ApprovedName!, RegistryValueKind.Binary, bytes);
        }
        Log.Ok($"Autostart {(enabled ? "enabled" : "disabled")}: {item.Name} ({item.Source}).");
    }

    private static bool IsApproved(string approvedKey, string name) =>
        Reg.Get(approvedKey, name).Value is not byte[] { Length: > 0 } v || v[0] % 2 == 0;

    /// <summary>Scheduled tasks outside \Microsoft\ that run at sign-in (trigger 9) or boot (trigger 8).</summary>
    private static List<StartupItem> Tasks()
    {
        var items = new List<StartupItem>();
        dynamic service;
        try { service = TaskService(); }
        catch (System.Runtime.InteropServices.COMException) { return items; }
        var folders = new Stack<dynamic>();
        folders.Push(service.GetFolder(@"\"));
        while (folders.Count > 0)
        {
            var folder = folders.Pop();
            string path = folder.Path;
            if (path.StartsWith(@"\Microsoft", StringComparison.OrdinalIgnoreCase) || path.StartsWith(@"\Frametide", StringComparison.OrdinalIgnoreCase)) continue;
            // Folders and tasks with restricted permissions (anti-cheat, OEM tools) are skipped, not fatal.
            var tasks = new List<dynamic>();
            try
            {
                foreach (var sub in folder.GetFolders(0)) folders.Push(sub);
                foreach (var task in folder.GetTasks(1)) tasks.Add(task);   // TASK_ENUM_HIDDEN
            }
            catch (Exception e) when (IsAccessError(e)) { continue; }
            foreach (var task in tasks)
            {
                try
                {
                    var definition = task.Definition;
                    var atStart = false;
                    foreach (var trigger in definition.Triggers) if ((int)trigger.Type is 8 or 9) atStart = true;
                    if (!atStart) continue;
                    string exe = "", args = "";
                    foreach (var action in definition.Actions)
                    {
                        if ((int)action.Type != 0) continue;   // TASK_ACTION_EXEC
                        exe = action.Path ?? "";
                        args = action.Arguments ?? "";
                        break;
                    }
                    items.Add(new StartupItem(StartupKind.Task, (string)task.Name, $"{exe} {args}".Trim(), "Scheduled task", (bool)task.Enabled,
                        Publisher(ExePath(exe)), TaskPath: path));
                }
                catch (Exception e) when (IsAccessError(e)) { }   // no access to this task
            }
        }
        return items;
    }

    private static bool IsAccessError(Exception e) =>
        e is System.Runtime.InteropServices.COMException or UnauthorizedAccessException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException;

    private static dynamic TaskService()
    {
        dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!)!;
        service.Connect();
        return service;
    }

    private static string? ShortcutTarget(string lnk)
    {
        try
        {
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", throwOnError: true)!)!;
            string target = shell.CreateShortcut(lnk).TargetPath;
            return target.Length > 0 ? target : null;
        }
        catch (System.Runtime.InteropServices.COMException) { return null; }
    }

    /// <summary>Program path from a command line ("C:\x\a.exe" -arg, C:\x\a.exe -arg, %ProgramFiles%\...).</summary>
    internal static string ExePath(string command)
    {
        var c = Environment.ExpandEnvironmentVariables(command).Trim();
        if (QuotedPath().Match(c) is { Success: true } q) return q.Groups[1].Value;
        if (ExeInCommand().Match(c) is { Success: true } e) return e.Groups[1].Value;
        return c.Split(' ')[0];
    }

    private static string Publisher(string path)
    {
        try { return File.Exists(path) ? FileVersionInfo.GetVersionInfo(path).CompanyName?.Trim() ?? "" : ""; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return ""; }
    }

    [GeneratedRegex("^\"([^\"]+)\"")]
    private static partial Regex QuotedPath();

    [GeneratedRegex(@"^(.+?\.exe)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ExeInCommand();
}
