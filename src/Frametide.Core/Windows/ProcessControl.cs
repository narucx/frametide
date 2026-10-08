using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Frametide.Core.Windows;

/// <summary>Suspends and resumes whole processes (all threads), like Resource Monitor's "Suspend process".</summary>
public static partial class ProcessControl
{
    private const uint ProcessSuspendResume = 0x0800;

    /// <summary>NTSTATUS of the call; 0 = success.</summary>
    public static int Suspend(int pid) => Call(pid, suspend: true);

    public static int Resume(int pid) => Call(pid, suspend: false);

    /// <summary>Start time as round-trip text, used to recognise a process again (PIDs are reused). Null when access is denied.</summary>
    public static string? StartTime(Process p)
    {
        try { return p.StartTime.ToString("o"); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { return null; }
    }

    private static int Call(int pid, bool suspend)
    {
        var h = OpenProcess(ProcessSuspendResume, false, pid);
        if (h == IntPtr.Zero) return Marshal.GetLastPInvokeError();
        try { return suspend ? NtSuspendProcess(h) : NtResumeProcess(h); }
        finally { CloseHandle(h); }
    }

    [LibraryImport("ntdll.dll")]
    private static partial int NtSuspendProcess(IntPtr handle);

    [LibraryImport("ntdll.dll")]
    private static partial int NtResumeProcess(IntPtr handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);
}
