using System.Runtime.InteropServices;

namespace Frametide.Core.Windows;

public enum StartMode { Boot, System, Automatic, AutomaticDelayed, Manual, Disabled }

/// <summary>Service start modes, read from the registry and set with sc.exe.</summary>
public static partial class Services
{
    public static StartMode? GetStartMode(string name)
    {
        var path = $@"HKLM:\SYSTEM\CurrentControlSet\Services\{name}";
        var start = Reg.Get(path, "Start");
        if (!start.Exists) return null;
        var mode = Convert.ToInt32(start.Value) switch
        {
            0 => StartMode.Boot, 1 => StartMode.System, 2 => StartMode.Automatic, 3 => StartMode.Manual, _ => StartMode.Disabled,
        };
        if (mode == StartMode.Automatic && Reg.Get(path, "DelayedAutostart").Value is int d && d == 1) mode = StartMode.AutomaticDelayed;
        return mode;
    }

    public static void SetStartMode(string name, StartMode mode)
    {
        var arg = mode switch
        {
            StartMode.Automatic => "auto", StartMode.AutomaticDelayed => "delayed-auto", StartMode.Manual => "demand",
            StartMode.Disabled => "disabled",
            _ => throw new ArgumentException($"Start mode {mode} cannot be set"),
        };
        var r = NativeProcess.Run("sc.exe", ["config", name, "start=", arg]);
        if (r.ExitCode != 0) throw new InvalidOperationException($"sc config {name} failed: {r.Output.Trim()}");
        // A disabled service is also stopped right away (sc.exe instead of ServiceController: no extra package).
        if (mode == StartMode.Disabled) NativeProcess.Run("sc.exe", ["stop", name]);
    }

    /// <summary>Whether the service is running (or starting); false when it does not exist.</summary>
    public static bool IsRunning(string name) => State(name) is 2 or 4 or 5 or 6;

    /// <summary>Stops the service and waits until it has stopped (sc.exe stop only asks and returns at once).</summary>
    public static bool StopAndWait(string name, TimeSpan timeout)
    {
        NativeProcess.Run("sc.exe", ["stop", name]);
        var until = DateTime.UtcNow + timeout;
        while (State(name) is not (null or 1))   // 1 = SERVICE_STOPPED
        {
            if (DateTime.UtcNow > until) return false;
            Thread.Sleep(250);
        }
        return true;
    }

    /// <summary>SERVICE_STATUS.dwCurrentState, or null when the service does not exist.</summary>
    private static int? State(string name)
    {
        var scm = OpenSCManagerW(null, null, 0x1);   // SC_MANAGER_CONNECT
        if (scm == IntPtr.Zero) return null;
        try
        {
            var service = OpenServiceW(scm, name, 0x4);   // SERVICE_QUERY_STATUS
            if (service == IntPtr.Zero) return null;
            try { return QueryServiceStatus(service, out var status) ? status.CurrentState : null; }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(scm); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public int ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint;
    }

    [LibraryImport("advapi32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial IntPtr OpenSCManagerW(string? machine, string? database, uint access);

    [LibraryImport("advapi32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial IntPtr OpenServiceW(IntPtr scm, string name, uint access);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryServiceStatus(IntPtr service, out ServiceStatus status);

    [LibraryImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseServiceHandle(IntPtr handle);
}
