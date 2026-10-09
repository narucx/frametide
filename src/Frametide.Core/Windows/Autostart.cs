using System.Security.Principal;
using System.Text;
using System.Xml.Linq;
using Frametide.Core.Infrastructure;

namespace Frametide.Core.Windows;

/// <summary>
/// Scheduled tasks that start Frametide at sign-in with highest privileges (the app is elevated, so no UAC prompt).
/// They only ever start a program in a folder that standard users cannot change (installed for all users), otherwise
/// any program running as the user could swap the exe and get administrator rights at the next sign-in.
/// </summary>
public static class LogonTask
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    /// <summary>All sign-in tasks Frametide creates (in the task folder <see cref="Folder"/>).</summary>
    public static readonly string[] All = [Autostart.TaskName, Gpu.GpuTuning.TaskName];

    public const string Folder = "Frametide";

    /// <summary>Program the task starts, or null when there is no task.</summary>
    public static string? Command(string name) => Query(name)?.Descendants(Ns + "Command").FirstOrDefault()?.Value;

    public static string? Arguments(string name) => Query(name)?.Descendants(Ns + "Arguments").FirstOrDefault()?.Value;

    private static XDocument? Query(string name)
    {
        var r = NativeProcess.Run("schtasks.exe", ["/Query", "/TN", name, "/XML"]);
        if (r.ExitCode != 0) return null;
        try { return XDocument.Parse(r.Output); }
        catch (System.Xml.XmlException) { return null; }
    }

    /// <summary>Throws when <paramref name="exePath"/> may not be started with administrator rights at sign-in.</summary>
    public static void EnsureAllowed(string exePath)
    {
        if (AdminOnly.ProgramProblem(exePath) is not { } problem) return;
        Log.Warn($"Sign-in task refused: {problem}.");
        throw new InvalidOperationException(L.T("This needs Frametide installed for all users (Frametide-win.msi from the releases page). This copy is in a folder that standard users can change, so Windows must not start it with administrator rights at sign-in."));
    }

    public static void Register(string name, string description, string exePath, string arguments, TimeSpan delay, TimeSpan? timeLimit = null)
    {
        EnsureAllowed(exePath);
        using var user = WindowsIdentity.GetCurrent();
        var xml = new XElement(Ns + "Task", new XAttribute("version", "1.3"),
            new XElement(Ns + "RegistrationInfo", new XElement(Ns + "Description", description)),
            new XElement(Ns + "Triggers",
                new XElement(Ns + "LogonTrigger",
                    new XElement(Ns + "Enabled", "true"),
                    new XElement(Ns + "UserId", user.Name),
                    new XElement(Ns + "Delay", System.Xml.XmlConvert.ToString(delay)))),
            new XElement(Ns + "Principals",
                new XElement(Ns + "Principal", new XAttribute("id", "Author"),
                    new XElement(Ns + "UserId", user.User!.Value),
                    new XElement(Ns + "LogonType", "InteractiveToken"),
                    new XElement(Ns + "RunLevel", "HighestAvailable"))),
            new XElement(Ns + "Settings",
                new XElement(Ns + "MultipleInstancesPolicy", "IgnoreNew"),
                new XElement(Ns + "DisallowStartIfOnBatteries", "false"),
                new XElement(Ns + "StopIfGoingOnBatteries", "false"),
                new XElement(Ns + "ExecutionTimeLimit", timeLimit is { } t ? System.Xml.XmlConvert.ToString(t) : "PT0S"),
                // The default (7) starts the program with below-normal CPU and I/O priority.
                new XElement(Ns + "Priority", "5")),
            new XElement(Ns + "Actions", new XAttribute("Context", "Author"),
                new XElement(Ns + "Exec",
                    new XElement(Ns + "Command", exePath),
                    new XElement(Ns + "Arguments", arguments))));
        Create(name, xml);
    }

    private static void Create(string name, XElement xml)
    {
        var file = Path.Combine(AppPaths.DataDir, $"task-{Guid.NewGuid():N}.xml");
        File.WriteAllText(file, "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" + xml, Encoding.Unicode);
        try
        {
            var r = NativeProcess.Run("schtasks.exe", ["/Create", "/TN", name, "/XML", file, "/F"]);
            if (r.ExitCode != 0) throw new InvalidOperationException($"Could not create the scheduled task: {(r.Error + r.Output).Trim()}");
        }
        finally { File.Delete(file); }
    }

    public static void Delete(string name)
    {
        if (Query(name) is null) return;
        var r = NativeProcess.Run("schtasks.exe", ["/Delete", "/TN", name, "/F"]);
        if (r.ExitCode != 0) throw new InvalidOperationException($"Could not remove the scheduled task: {(r.Error + r.Output).Trim()}");
    }

    /// <summary>Removes the empty task folder (uninstall).</summary>
    public static void DeleteFolder()
    {
        dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!)!;
        service.Connect();
        try { service.GetFolder(@"\").DeleteFolder(Folder, 0); }
        catch (System.Runtime.InteropServices.COMException) { }   // not there, or still has tasks
    }

    /// <summary>
    /// Startup check of the sign-in tasks: after an update or a move they are pointed to this program. When they
    /// would start a program that standard users can change (installed per user, or left over from an older
    /// version), they are removed.
    /// </summary>
    public static void Secure(string exePath)
    {
        var exeProblem = AdminOnly.ProgramProblem(exePath);
        foreach (var name in All)
        {
            if (Query(name) is not { } task || task.Descendants(Ns + "Command").FirstOrDefault() is not { } command) continue;
            if (exeProblem is null)
            {
                if (string.Equals(command.Value, exePath, StringComparison.OrdinalIgnoreCase)) continue;
                Log.Info($"Sign-in task {name}: {command.Value} -> {exePath}.");
                command.Value = exePath;
                Create(name, task.Root!);
            }
            else if (AdminOnly.DefiniteProgramProblem(command.Value) is { } problem)
            {
                Delete(name);
                Log.Warn($"Removed the sign-in task {name}: it would start {command.Value} with administrator rights, but {problem}.");
            }
        }
    }
}

/// <summary>"Start with Windows": Frametide starts in the notification area at sign-in.</summary>
public static class Autostart
{
    public const string TaskName = @"\Frametide\Autostart";
    public const string TrayArgument = "--tray";

    public static string? RegisteredCommand() => LogonTask.Command(TaskName);

    /// <summary>The user's choice. The task itself can be missing while Frametide runs from a folder that is not safe.</summary>
    public static bool Wanted => Settings.GetBool("StartWithWindows", false);

    public static void Set(bool on, string exePath)
    {
        if (on) Enable(exePath); else Disable();
        Settings.Set("StartWithWindows", on);
    }

    private static void Enable(string exePath)
    {
        LogonTask.Register(TaskName, "Starts Frametide in the notification area at sign-in.", exePath, TrayArgument, TimeSpan.FromSeconds(20));
        Log.Ok("Start with Windows enabled (notification area).");
    }

    private static void Disable()
    {
        if (RegisteredCommand() is null) return;
        LogonTask.Delete(TaskName);
        Log.Ok("Start with Windows disabled.");
    }

    /// <summary>Registers the task again when it is wanted but missing (e.g. removed while Frametide ran from an unsafe folder).</summary>
    public static void Restore(string exePath)
    {
        if (Wanted && RegisteredCommand() is null && AdminOnly.IsProtectedProgram(exePath)) Enable(exePath);
    }
}
