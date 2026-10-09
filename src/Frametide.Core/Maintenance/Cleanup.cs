using System.Runtime.InteropServices;
using Frametide.Core.Infrastructure;
using Frametide.Core.Windows;

namespace Frametide.Core.Maintenance;

/// <param name="MinAgeDays">Only files not changed for this long: temp folders of installers that are running right now stay.</param>
public sealed record CleanupCategory(string Id, string Name, IReadOnlyList<string> Paths, IReadOnlyList<string>? StopServices = null,
    bool RecycleBin = false, int MinAgeDays = 0);

public sealed record CleanupItem(CleanupCategory Category, long Bytes);

/// <summary>Temporary files and caches that Windows and drivers leave behind. Files in use are skipped.</summary>
public static partial class Cleanup
{
    public static IReadOnlyList<CleanupCategory> Categories()
    {
        string Env(Environment.SpecialFolder f) => Environment.GetFolderPath(f);
        var local = Env(Environment.SpecialFolder.LocalApplicationData);
        var windows = Env(Environment.SpecialFolder.Windows);
        var programData = Env(Environment.SpecialFolder.CommonApplicationData);
        var systemDrive = windows.Length > 0 ? Path.GetPathRoot(windows) ?? "" : "";
        // Path.GetTempPath falls back to the user profile when TMP and TEMP are empty: only a real "Temp" folder counts.
        var userTemp = Path.TrimEndingDirectorySeparator(Path.GetTempPath());
        return
        [
            new("usertemp", "Temporary files (user)", [Path.GetFileName(userTemp).Equals("Temp", StringComparison.OrdinalIgnoreCase) ? userTemp : ""], MinAgeDays: 1),
            new("wintemp", "Temporary files (Windows)", [Under(windows, "Temp")], MinAgeDays: 1),
            new("wudl", "Windows Update downloads", [Under(windows, @"SoftwareDistribution\Download")], StopServices: ["wuauserv", "bits"]),
            new("dumps", "Crash dumps", [Under(windows, "Minidump"), Under(local, "CrashDumps"), Under(windows, "LiveKernelReports")]),
            new("wer", "Error reports (WER)", [Under(programData, @"Microsoft\Windows\WER\ReportArchive"), Under(programData, @"Microsoft\Windows\WER\ReportQueue"), Under(local, @"Microsoft\Windows\WER")]),
            new("nvinstall", "Extracted NVIDIA installers", [Under(systemDrive, "NVIDIA"), Under(programData, @"NVIDIA Corporation\Downloader")], MinAgeDays: 1),
            new("shader", "Shader cache (DirectX/NVIDIA) - causes brief stutter", [Under(local, "D3DSCache"), Under(local, @"NVIDIA\DXCache"), Under(local, @"NVIDIA\GLCache")]),
            new("recycle", "Recycle Bin", [], RecycleBin: true),
        ];
    }

    /// <summary>A sub folder of a known folder; "" (skipped) when the known folder is unavailable.</summary>
    private static string Under(string root, string sub) => Path.IsPathFullyQualified(root) ? Path.Combine(root, sub) : "";

    /// <summary>Last line of defence: never clean a drive root, a known top folder or anything relative.</summary>
    internal static bool IsSafeRoot(string dir)
    {
        if (dir.Length == 0 || !Path.IsPathFullyQualified(dir)) return false;
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir));
        if (Path.GetPathRoot(full) is { } root && full.Equals(Path.TrimEndingDirectorySeparator(root), StringComparison.OrdinalIgnoreCase)) return false;
        Environment.SpecialFolder[] top =
        [
            Environment.SpecialFolder.Windows, Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolder.ProgramFiles,
            Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.System, Environment.SpecialFolder.MyDocuments,
            Environment.SpecialFolder.DesktopDirectory,
        ];
        return !top.Select(Environment.GetFolderPath).Where(t => t.Length > 0)
            .Any(t => full.Equals(Path.TrimEndingDirectorySeparator(t), StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> Roots(CleanupCategory c) => c.Paths.Where(p => IsSafeRoot(p) && Directory.Exists(p));

    private static EnumerationOptions Options => new() { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };

    private static IEnumerable<FileInfo> Files(CleanupCategory c, string dir)
    {
        var before = DateTime.UtcNow.AddDays(-c.MinAgeDays);
        return new DirectoryInfo(dir).EnumerateFiles("*", Options).Where(f => c.MinAgeDays == 0 || f.LastWriteTimeUtc < before);
    }

    public static IReadOnlyList<CleanupItem> Scan() =>
        Categories().Select(c => new CleanupItem(c, c.RecycleBin ? RecycleBinSize() : Roots(c).Sum(d => FolderSize(c, d)))).ToList();

    private static long FolderSize(CleanupCategory c, string dir) => Files(c, dir).Sum(f =>
    {
        try { return f.Length; }
        catch (IOException) { return 0L; }
    });

    public static long Clean(IEnumerable<string> ids)
    {
        long freed = 0;
        foreach (var c in Categories().Where(c => ids.Contains(c.Id)))
        {
            if (c.RecycleBin)
            {
                var size = RecycleBinSize();
                var hr = SHEmptyRecycleBinW(IntPtr.Zero, null, 0x1 | 0x2 | 0x4);   // no confirmation, no progress, no sound
                if (hr == 0) freed += size; else Log.Warn($"Emptying the Recycle Bin failed (0x{hr:X8}).");
                continue;
            }
            // Only services that were running are started again afterwards.
            var stopped = (c.StopServices ?? []).Where(Services.IsRunning).ToList();
            foreach (var svc in stopped)
                if (!Services.StopAndWait(svc, TimeSpan.FromSeconds(30))) Log.Warn($"Cleanup: service {svc} did not stop in time.");
            try
            {
                foreach (var dir in Roots(c))
                {
                    // Several of these folders are writable for the user: never follow a junction out of them.
                    if (SafeDelete.RealRoot(dir) is not { } real)
                    {
                        Log.Warn($"Cleanup: skipped {dir}, it is a link to another folder.");
                        continue;
                    }
                    foreach (var f in Files(c, dir))
                        if (SafeDelete.Delete(f.FullName, real) is var len and >= 0) freed += len;   // -1: in use
                    // Empty folders, deepest first; the category folder itself stays.
                    foreach (var sub in new DirectoryInfo(dir).EnumerateDirectories("*", Options).OrderByDescending(d => d.FullName.Length))
                        SafeDelete.Delete(sub.FullName, real);
                }
            }
            finally
            {
                foreach (var svc in Enumerable.Reverse(stopped)) NativeProcess.Run("sc.exe", ["start", svc]);
            }
        }
        Log.Ok($"Cleanup: {freed / 1048576.0:N0} MB freed (files in use skipped).");
        return freed;
    }

    private static long RecycleBinSize()
    {
        var info = new RecycleBinInfo { Size = Marshal.SizeOf<RecycleBinInfo>() };
        return SHQueryRecycleBinW(null, ref info) == 0 ? info.Bytes : 0;
    }

    [StructLayout(LayoutKind.Sequential)]   // SHQUERYRBINFO: default (8-byte) packing on x64
    private struct RecycleBinInfo { public int Size; public long Bytes; public long Items; }

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHQueryRecycleBinW(string? rootPath, ref RecycleBinInfo info);

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHEmptyRecycleBinW(IntPtr window, string? rootPath, uint flags);
}
