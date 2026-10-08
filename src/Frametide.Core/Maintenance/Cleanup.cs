using System.Runtime.InteropServices;
using Frametide.Core.Infrastructure;
using Frametide.Core.Windows;

namespace Frametide.Core.Maintenance;

public sealed record CleanupCategory(string Id, string Name, IReadOnlyList<string> Paths, string? StopService = null, bool RecycleBin = false);

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
        var systemDrive = Path.GetPathRoot(windows)!;
        return
        [
            new("usertemp", "Temporary files (user)", [Path.GetTempPath()]),
            new("wintemp", "Temporary files (Windows)", [Path.Combine(windows, "Temp")]),
            new("wudl", "Windows Update downloads", [Path.Combine(windows, @"SoftwareDistribution\Download")], StopService: "wuauserv"),
            new("dumps", "Crash dumps", [Path.Combine(windows, "Minidump"), Path.Combine(local, "CrashDumps"), Path.Combine(windows, "LiveKernelReports")]),
            new("wer", "Error reports (WER)", [Path.Combine(programData, @"Microsoft\Windows\WER\ReportArchive"), Path.Combine(programData, @"Microsoft\Windows\WER\ReportQueue"), Path.Combine(local, @"Microsoft\Windows\WER")]),
            new("nvinstall", "Extracted NVIDIA installers", [Path.Combine(systemDrive, "NVIDIA"), Path.Combine(programData, @"NVIDIA Corporation\Downloader")]),
            new("shader", "Shader cache (DirectX/NVIDIA) - causes brief stutter", [Path.Combine(local, "D3DSCache"), Path.Combine(local, @"NVIDIA\DXCache"), Path.Combine(local, @"NVIDIA\GLCache")]),
            new("recycle", "Recycle Bin", [], RecycleBin: true),
        ];
    }

    private static EnumerationOptions Options => new() { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };

    public static IReadOnlyList<CleanupItem> Scan() =>
        Categories().Select(c => new CleanupItem(c, c.RecycleBin ? RecycleBinSize() : c.Paths.Where(Directory.Exists).Sum(FolderSize))).ToList();

    private static long FolderSize(string dir) => new DirectoryInfo(dir).EnumerateFiles("*", Options).Sum(f =>
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
                freed += RecycleBinSize();
                SHEmptyRecycleBinW(IntPtr.Zero, null, 0x1 | 0x2 | 0x4);   // no confirmation, no progress, no sound
                continue;
            }
            if (c.StopService is { } svc) NativeProcess.Run("sc.exe", ["stop", svc]);
            try
            {
                foreach (var dir in c.Paths.Where(Directory.Exists))
                {
                    foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", Options))
                    {
                        try { var len = f.Length; f.Delete(); freed += len; }
                        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }   // in use
                    }
                    // Empty folders, deepest first; the category folder itself stays.
                    foreach (var sub in new DirectoryInfo(dir).EnumerateDirectories("*", Options).OrderByDescending(d => d.FullName.Length))
                    {
                        try { sub.Delete(); }
                        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                    }
                }
            }
            finally { if (c.StopService is { } s) NativeProcess.Run("sc.exe", ["start", s]); }
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
