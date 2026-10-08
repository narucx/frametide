using Frametide.Core.Infrastructure;

namespace Frametide.Tests;

internal static class TestData
{
    /// <summary>Data folder for everything that is not part of a test's own folder (e.g. log lines of other tests).</summary>
    private static readonly string SharedDir = Path.Combine(Path.GetTempPath(), "frametide-tests");

    /// <summary>Tests never write into the real data folder (C:\ProgramData\Frametide), not even log lines.</summary>
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void UseTestDataFolder() => AppPaths.UseDataDir(SharedDir);

    /// <summary>
    /// Points the app data folder away from <paramref name="dir"/>, then deletes it. Tests running in parallel may still be
    /// writing a log line into it for a moment, so deleting is retried briefly.
    /// </summary>
    public static void Release(string dir)
    {
        AppPaths.UseDataDir(SharedDir);
        for (var attempt = 0; ; attempt++)
        {
            try { Directory.Delete(dir, recursive: true); return; }
            catch (IOException) when (attempt < 20) { Thread.Sleep(50); }
        }
    }
}
