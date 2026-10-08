using Velopack;

namespace Frametide;

public static class Program
{
    /// <summary>Dev: "--preview (png) (page title)" renders the window to a PNG and exits (no admin rights, temp data folder).</summary>
    public static (string Png, string Page)? Preview { get; private set; }

    [STAThread]
    public static void Main(string[] args)
    {
        // Must run first: handles Velopack's install/update/uninstall hooks and exits in those cases.
        VelopackApp.Build().Run();

        if (args.Length >= 2 && args[0] == "--preview") Preview = (args[1], args.Length >= 3 ? args[2] : "Overview");

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
