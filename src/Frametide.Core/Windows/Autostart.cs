using System.Security.Principal;
using System.Text;
using System.Xml.Linq;
using Frametide.Core.Infrastructure;

namespace Frametide.Core.Windows;

/// <summary>Scheduled tasks that start Frametide at sign-in with highest privileges (the app is elevated, so no UAC prompt).</summary>
public static class LogonTask
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

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

    public static void Register(string name, string description, string exePath, string arguments, TimeSpan delay, TimeSpan? timeLimit = null)
    {
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
}

/// <summary>"Start with Windows": Frametide starts in the notification area at sign-in.</summary>
public static class Autostart
{
    public const string TaskName = @"\Frametide\Autostart";
    public const string TrayArgument = "--tray";

    public static string? RegisteredCommand() => LogonTask.Command(TaskName);

    public static void Enable(string exePath)
    {
        LogonTask.Register(TaskName, "Starts Frametide in the notification area at sign-in.", exePath, TrayArgument, TimeSpan.FromSeconds(20));
        Log.Ok("Start with Windows enabled (notification area).");
    }

    public static void Disable()
    {
        if (RegisteredCommand() is null) return;
        LogonTask.Delete(TaskName);
        Log.Ok("Start with Windows disabled.");
    }
}
