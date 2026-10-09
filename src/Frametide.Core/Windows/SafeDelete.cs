using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Frametide.Core.Windows;

/// <summary>
/// Deletes files in folders a standard user can change (temp folders, caches in the profile) without following
/// links. The app runs elevated: a user could replace such a folder, or a folder in it, by a junction to System32,
/// and a plain delete would then remove system files. Each file is opened without following links, its real path
/// must still be inside the real path of the folder being cleaned, and it is deleted through that same handle.
/// </summary>
public static partial class SafeDelete
{
    /// <summary>
    /// The real path of a folder to clean, or null when the folder or one of its parents is a link (junction,
    /// symbolic link): then it is skipped.
    /// </summary>
    public static string? RealRoot(string dir)
    {
        using var handle = Open(dir, FileReadAttributes, followLinks: true);
        if (handle is null) return null;
        var real = FinalPath(handle);
        var expected = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir));
        return real is not null && real.Equals(expected, StringComparison.OrdinalIgnoreCase) ? real : null;
    }

    /// <summary>Deletes one file or empty folder below <paramref name="realRoot"/>; its size in bytes, or -1 if skipped.</summary>
    public static long Delete(string path, string realRoot)
    {
        using var handle = Open(path, DeleteAccess | FileReadAttributes, followLinks: false);
        if (handle is null) return -1;   // in use, no access or gone
        if (FinalPath(handle) is not { } real || !real.StartsWith(realRoot + "\\", StringComparison.OrdinalIgnoreCase)) return -1;
        if (!GetFileSizeEx(handle, out var size)) size = 0;
        var info = new FileDispositionInfo { DeleteFile = 1 };
        return SetFileInformationByHandle(handle, FileDispositionInfoClass, ref info, sizeof(byte)) ? size : -1;
    }

    private static SafeFileHandle? Open(string path, uint access, bool followLinks)
    {
        var flags = BackupSemantics | (followLinks ? 0 : OpenReparsePoint);   // BackupSemantics: folders too
        var handle = CreateFileW(path, access, ShareAll, IntPtr.Zero, OpenExisting, flags, IntPtr.Zero);
        if (!handle.IsInvalid) return handle;
        handle.Dispose();
        return null;
    }

    /// <summary>The path Windows resolved the handle to, without the "\\?\" prefix.</summary>
    private static string? FinalPath(SafeFileHandle handle)
    {
        var buffer = new char[1024];
        var length = GetFinalPathNameByHandleW(handle, ref buffer[0], (uint)buffer.Length, 0);
        if (length == 0 || length >= buffer.Length) return null;
        var path = new string(buffer, 0, (int)length);
        if (path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) return @"\\" + path[8..];
        return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
    }

    private const uint DeleteAccess = 0x00010000, FileReadAttributes = 0x80, ShareAll = 0x7, OpenExisting = 3;
    private const uint BackupSemantics = 0x02000000, OpenReparsePoint = 0x00200000;
    private const int FileDispositionInfoClass = 4;

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDispositionInfo { public byte DeleteFile; }

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetFinalPathNameByHandleW(SafeFileHandle file, ref char path, uint length, uint flags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileSizeEx(SafeFileHandle file, out long size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetFileInformationByHandle(SafeFileHandle file, int infoClass, ref FileDispositionInfo info, uint size);
}
