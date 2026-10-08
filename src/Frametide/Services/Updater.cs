using System.Reflection;
using Velopack;
using Velopack.Sources;

namespace Frametide.Services;

/// <summary>Updates from GitHub Releases (Velopack). Only active in an installed copy, not in a dev build.</summary>
public sealed class Updater
{
    public const string RepoUrl = "https://github.com/narucx/frametide";

    // A pre-release (e.g. 1.0.0-beta.1) also gets newer pre-releases; a stable version only stable releases.
    private static readonly bool IsPrerelease =
        typeof(Updater).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0].Contains('-') == true;

    private readonly UpdateManager _manager = new(new GithubSource(RepoUrl, accessToken: null, prerelease: IsPrerelease));

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
