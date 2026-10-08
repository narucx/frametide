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
        // Must run first: handles Velopack's install/update/uninstall hooks and exits in those cases.
        VelopackApp.Build().Run();

        if (args.Length >= 2 && args[0] == "--preview")
            Preview = (args[1], args.Length >= 3 ? args[2] : "Overview", args.Length >= 4 ? double.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture) : 0);
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
}
