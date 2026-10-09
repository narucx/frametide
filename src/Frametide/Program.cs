using System.Diagnostics;
using System.Security.Principal;
using Velopack;

namespace Frametide;

public static class Program
{
    /// <summary>Dev: "--preview (png) (page title) [height]" renders the window to a PNG and exits (no admin rights, temp data folder).</summary>
    public static (string Png, string Page, double Height)? Preview { get; private set; }

    /// <summary>Started by "Start with Windows": only the notification area icon, no window.</summary>
    public static bool StartInTray { get; private set; }

    [STAThread]
    public static void Main(string[] args)
    {
        // Must run first: handles Velopack's install/update/uninstall hooks and exits in those cases. The hooks run
        // without elevation, which is why the manifest does not demand administrator rights.
        VelopackApp.Build().OnBeforeUninstallFastCallback(_ => BeforeUninstall()).Run();

        if (args.Length >= 2 && args[0] == "--preview")
            Preview = (args[1], args.Length >= 3 ? args[2] : "Overview", args.Length >= 4 ? double.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture) : 0);

        // Nearly every feature needs administrator rights: start again elevated (UAC prompt), with the same arguments.
        if (Preview is null && !IsElevated())
        {
            RestartElevated(args);
            return;
        }

        if (args is [UninstallArgument])
        {
            Core.Uninstall.Cleanup();
            return;
        }

        // Sign-in task: apply a GPU profile and exit, no window and no instance lock.
        if (args.Length >= 2 && args[0] == Core.Gpu.GpuTuning.ApplyProfileArgument)
        {
            Core.Infrastructure.AppPaths.EnsureDataDir();
            Core.Gpu.GpuTests.RecoverAfterCrash();
            // Unattended: a profile tested with another driver is skipped (logged), one from another GPU is refused.
            try { Core.Gpu.GpuTuning.ApplyProfile(args[1], unattended: true); }
            catch (InvalidOperationException e) { Core.Infrastructure.Log.Error($"GPU profile at sign-in: {e.Message}"); }
            return;
        }

        StartInTray = args.Contains(Core.Windows.Autostart.TrayArgument);

        // One instance only: a later start brings the running one to the front.
        using var instance = Preview is null ? Services.SingleInstance.TryAcquire() : null;
        if (Preview is null && instance is null)
        {
            if (!StartInTray) Services.SingleInstance.SignalFirst();
            return;
        }

        var app = new App { Instance = instance };
        app.InitializeComponent();
        app.Run();
    }

    private const string UninstallArgument = "--uninstall-cleanup";

    /// <summary>
    /// Velopack runs this unelevated (also from the MSI) right before the files are removed. The cleanup needs
    /// administrator rights, so it runs in an elevated copy (UAC prompt); the hook may take up to a minute.
    /// </summary>
    private static void BeforeUninstall()
    {
        if (IsElevated()) { Core.Uninstall.Cleanup(); return; }
        var psi = new ProcessStartInfo(Environment.ProcessPath!, UninstallArgument) { UseShellExecute = true, Verb = "runas" };
        try
        {
            using var cleanup = Process.Start(psi);
            cleanup?.WaitForExit(TimeSpan.FromSeconds(45));
        }
        // Declined: the sign-in tasks stay, but they point into the removed program folder and start nothing.
        catch (System.ComponentModel.Win32Exception) { }
    }

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static void RestartElevated(string[] args)
    {
        // Our arguments contain no backslash before a quote, so wrapping in quotes and doubling inner quotes is enough.
        var arguments = string.Join(" ", args.Select(a => a.Length > 0 && !a.Any(c => c is ' ' or '"') ? a : $"\"{a.Replace("\"", "\"\"")}\""));
        var psi = new ProcessStartInfo(Environment.ProcessPath!, arguments) { UseShellExecute = true, Verb = "runas" };
        try { Process.Start(psi)?.Dispose(); }
        catch (System.ComponentModel.Win32Exception e) when (e.NativeErrorCode == 1223) { }   // UAC prompt declined
    }
}
