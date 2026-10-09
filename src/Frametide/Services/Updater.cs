using System.Reflection;
using Frametide.Core.Infrastructure;
using Velopack;
using Velopack.Sources;

namespace Frametide.Services;

/// <summary>
/// Updates through Velopack from the feed on GitHub Pages (the releases only carry the MSI). Only active in an
/// installed copy, not in a dev build.
/// </summary>
public sealed class Updater
{
    /// <summary>"stable" has stable releases only, "beta" has betas and stable releases.</summary>
    public const string FeedUrl = "https://narucx.github.io/frametide/";

    /// <summary>The running version, e.g. "0.1.0-beta.6" (without the commit hash).</summary>
    public static string CurrentVersion { get; } =
        typeof(Updater).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "?";

    private static readonly bool RunningPrerelease = CurrentVersion.Contains('-');

    /// <summary>
    /// Also offer beta versions. The beta feed also has the stable releases, so a beta always gets the next stable
    /// release (1.0.0 is newer than 1.0.0-beta.2). Default: on while a beta is installed.
    /// </summary>
    public static bool IncludeBetas
    {
        get => Settings.GetBool("BetaUpdates", RunningPrerelease);
        set => Settings.Set("BetaUpdates", value);
    }

    private readonly UpdateManager _manager = new(new SimpleWebSource(FeedUrl + (IncludeBetas ? "beta/" : "stable/")));

    public bool IsInstalled => _manager.IsInstalled;

    /// <summary>Returns the available update, or null when there is none (or this is not an installed copy).</summary>
    public async Task<UpdateInfo?> CheckAsync()
    {
        if (!_manager.IsInstalled) return null;
        return await _manager.CheckForUpdatesAsync();
    }

    public async Task DownloadAndRestartAsync(UpdateInfo update, Action<int>? progress = null)
    {
        await _manager.DownloadUpdatesAsync(update, progress);
        _manager.ApplyUpdatesAndRestart(update.TargetFullRelease);
    }
}
