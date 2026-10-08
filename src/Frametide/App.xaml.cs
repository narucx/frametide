using System.Windows;
using Frametide.Core.Infrastructure;
using Frametide.Localization;

namespace Frametide;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        if (Program.Preview is not null) AppPaths.UseDataDir(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "frametide-preview"));
        else AppPaths.EnsureDataDir();
        Loc.Load(Settings.Language);
        Log.Info($"Frametide started (language {Loc.Code}).");
        base.OnStartup(e);
    }
}
