using System.Security.Principal;
using System.Text;
using System.Xml.Linq;
using Frametide.Core.Infrastructure;

namespace Frametide.Core.Windows;

/// <summary>
/// "Start with Windows": a scheduled task at sign-in with highest privileges, so the elevated app starts without a UAC prompt.
/// </summary>
public static class Autostart
{
    public const string TaskName = @"\Frametide\Autostart";
    public const string TrayArgument = "--tray";

    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    /// <summary>Program the task starts, or null when there is no task.</summary>
    public static string? RegisteredCommand()
    {
        var r = NativeProcess.Run("schtasks.exe", ["/Query", "/TN", TaskName, "/XML"]);
        if (r.ExitCode != 0) return null;
        try { return XDocument.Parse(r.Output).Descendants(Ns + "Command").FirstOrDefault()?.Value; }
        catch (System.Xml.XmlException) { return null; }
    }

    public static void Enable(string exePath)
    {
        using var user = WindowsIdentity.GetCurrent();
        var xml = new XDocument(
            new XElement(Ns + "Task", new XAttribute("version", "1.3"),
                new XElement(Ns + "RegistrationInfo", new XElement(Ns + "Description", "Starts Frametide in the notification area at sign-in.")),
                new XElement(Ns + "Triggers",
                    new XElement(Ns + "LogonTrigger",
                        new XElement(Ns + "Enabled", "true"),
                        new XElement(Ns + "UserId", user.Name),
                        new XElement(Ns + "Delay", "PT20S"))),
                new XElement(Ns + "Principals",
                    new XElement(Ns + "Principal", new XAttribute("id", "Author"),
                        new XElement(Ns + "UserId", user.User!.Value),
                        new XElement(Ns + "LogonType", "InteractiveToken"),
                        new XElement(Ns + "RunLevel", "HighestAvailable"))),
                new XElement(Ns + "Settings",
                    new XElement(Ns + "MultipleInstancesPolicy", "IgnoreNew"),
                    new XElement(Ns + "DisallowStartIfOnBatteries", "false"),
                    new XElement(Ns + "StopIfGoingOnBatteries", "false"),
                    new XElement(Ns + "ExecutionTimeLimit", "PT0S"),
                    // The default (7) starts the program with below-normal CPU and I/O priority.
                    new XElement(Ns + "Priority", "5")),
                new XElement(Ns + "Actions", new XAttribute("Context", "Author"),
                    new XElement(Ns + "Exec",
                        new XElement(Ns + "Command", exePath),
                        new XElement(Ns + "Arguments", TrayArgument)))));

        var file = Path.Combine(AppPaths.DataDir, "autostart-task.xml");
        File.WriteAllText(file, "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" + xml, Encoding.Unicode);
        try
        {
            var r = NativeProcess.Run("schtasks.exe", ["/Create", "/TN", TaskName, "/XML", file, "/F"]);
            if (r.ExitCode != 0) throw new InvalidOperationException($"Could not create the scheduled task: {(r.Error + r.Output).Trim()}");
        }
        finally { File.Delete(file); }
        Log.Ok("Start with Windows enabled (notification area).");
    }

    public static void Disable()
    {
        if (RegisteredCommand() is null) return;
        var r = NativeProcess.Run("schtasks.exe", ["/Delete", "/TN", TaskName, "/F"]);
        if (r.ExitCode != 0) throw new InvalidOperationException($"Could not remove the scheduled task: {(r.Error + r.Output).Trim()}");
        Log.Ok("Start with Windows disabled.");
    }
}
