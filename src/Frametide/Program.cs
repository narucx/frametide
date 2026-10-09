using System.Diagnostics;
using System.Runtime.InteropServices;
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
        // Runs elevated: DLLs loaded by name only come from System32 or the program folder, never from the current
        // folder or PATH (a standard user can change both).
        SetDefaultDllDirectories(LoadLibrarySearchApplicationDir | LoadLibrarySearchSystem32);

        // Must run first: handles Velopack's install/update/uninstall hooks and exits in those cases. The hooks run
        // without elevation, which is why the manifest does not demand administrator rights.
        VelopackApp.Build().OnBeforeUninstallFastCallback(_ => BeforeUninstall()).Run();

        if (args.Length >= 2 && args[0] == "--preview")
            Preview = (Path.GetFullPath(args[1]), args.Length >= 3 ? args[2] : "Overview", args.Length >= 4 ? double.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture) : 0);

        // Nearly every feature needs administrator rights: start again elevated (UAC prompt), with the same arguments.
        if (Preview is null && !IsElevated())
        {
            RestartElevated(args);
            return;
        }

        // Programs started by bare name are looked up in the current folder first: make it one users cannot write to.
        Directory.SetCurrentDirectory(Environment.SystemDirectory);

        if (args is [UninstallArgument])
        {
            Core.Uninstall.Cleanup();
            return;
        }

        // Sign-in task: apply a GPU profile and exit, no window and no instance lock.
        if (args.Length >= 2 && args[0] == Core.Gpu.GpuTuning.ApplyProfileArgument)
        {
            Core.Infrastructure.AppPaths.EnsureDataDir();
            try { Core.Gpu.GpuTuning.ApplyProfile(args[1]); }
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
        var arguments = string.Join(" ", args.Select(Core.Windows.NativeProcess.Quote));
        var psi = new ProcessStartInfo(Environment.ProcessPath!, arguments) { UseShellExecute = true, Verb = "runas" };
        try { Process.Start(psi)?.Dispose(); }
        catch (System.ComponentModel.Win32Exception e) when (e.NativeErrorCode == 1223) { }   // UAC prompt declined
    }

    private const uint LoadLibrarySearchApplicationDir = 0x200, LoadLibrarySearchSystem32 = 0x800;

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool SetDefaultDllDirectories(uint directoryFlags);
}
