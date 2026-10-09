using Frametide.Core.Boost;
using Frametide.Core.Gpu;
using Frametide.Core.Infrastructure;
using Frametide.Core.Windows;

namespace Frametide.Core;

/// <summary>
/// What uninstalling removes: only what makes no sense without Frametide (sign-in tasks, a running Game Boost, the
/// launch priorities of auto mode). Tweaks, profiles, benchmarks and the journal stay in the data folder, so a
/// reinstall can still revert every tweak.
/// </summary>
public static class Uninstall
{
    /// <summary>Needs administrator rights.</summary>
    public static void Cleanup()
    {
        AppPaths.EnsureDataDir();
        Log.Info("Uninstalling: removing sign-in tasks, Game Boost and launch priorities.");
        Try("Game Boost", () => { if (GameBoost.IsActive) GameBoost.Stop(); });
        Try("Auto Game Boost", () => { if (BoostConfig.Load().AutoBoost) GameBoost.SetAutoBoost(false); });
        Try("Start with Windows", () => Autostart.Set(false, ""));
        Try("GPU profile at sign-in", () =>
        {
            if (GpuTuning.SignInProfile.Length > 0 || LogonTask.Command(GpuTuning.TaskName) is not null) GpuTuning.SetSignInProfile("", "");
        });
        Try("Task folder", LogonTask.DeleteFolder);
        Try("Notification name", () => Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\AppUserModelId\velopack.Frametide", throwOnMissingSubKey: false));
    }

    private static void Try(string what, Action action)
    {
        try { action(); }
        catch (Exception e) { Log.Error($"Uninstall, {what}: {e.Message}"); }
    }
}
