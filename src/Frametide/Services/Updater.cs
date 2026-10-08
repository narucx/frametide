using System.Reflection;
using Frametide.Core.Infrastructure;
using Velopack;
using Velopack.Sources;

namespace Frametide.Services;

/// <summary>Updates from GitHub Releases (Velopack). Only active in an installed copy, not in a dev build.</summary>
public sealed class Updater
{
    public const string RepoUrl = "https://github.com/narucx/frametide";

    private static readonly bool RunningPrerelease =
        typeof(Updater).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0].Contains('-') == true;

    /// <summary>
    /// Also offer beta versions. The newest version wins either way, so a beta always gets the next stable release
    /// (1.0.0 is newer than 1.0.0-beta.2). Default: on while a beta is installed.
    /// </summary>
    public static bool IncludeBetas
    {
        get => Settings.GetBool("BetaUpdates", RunningPrerelease);
        set => Settings.Set("BetaUpdates", value);
    }

    private readonly UpdateManager _manager = new(new GithubSource(RepoUrl, accessToken: null, prerelease: IncludeBetas));

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
