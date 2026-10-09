using Frametide.Core.Infrastructure;

namespace Frametide.Core.Boost;

/// <summary>DirectX and driver shader caches of the current user. Games rebuild them, which stutters for a while.</summary>
public static class ShaderCache
{
    private static readonly string[] Dirs = [@"D3DSCache", @"NVIDIA\DXCache", @"NVIDIA\GLCache", @"AMD\DxCache", @"AMD\DxcCache", @"AMD\VkCache"];

    public static long Clear()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        long freed = 0;
        foreach (var dir in Dirs.Select(d => Path.Combine(local, d)).Where(Directory.Exists))
        {
            // The user can change these folders: never follow a junction out of them (the app runs elevated).
            if (Windows.SafeDelete.RealRoot(dir) is not { } real) { Log.Warn($"Shader cache: skipped {dir}, it is a link to another folder."); continue; }
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", options))
                if (Windows.SafeDelete.Delete(f.FullName, real) is var len and >= 0) freed += len;   // -1: in use
        }
        Log.Ok($"Shader cache cleared ({freed / 1048576.0:N0} MB). The first minutes in game may stutter while it rebuilds.");
        return freed;
    }
}
