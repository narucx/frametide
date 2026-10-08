using System.Runtime.InteropServices;
using Frametide.Core.Infrastructure;
using Microsoft.Win32;

namespace Frametide.Core.Windows;

/// <summary>System restore points via SRSetRestorePointW (what Checkpoint-Computer uses).</summary>
public static partial class RestorePoint
{
    private const string SrKey = @"HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore";

    /// <summary>Creates a restore point. Returns null on success, otherwise the reason.</summary>
    public static string? Create(string description)
    {
        // Windows allows only one restore point per 24 h by default; lift that for this call.
        var freq = Reg.Get(SrKey, "SystemRestorePointCreationFrequency");
        try
        {
            Reg.Set(SrKey, "SystemRestorePointCreationFrequency", RegistryValueKind.DWord, 0);
            var info = new RestorePointInfo { EventType = BeginSystemChange, RestorePointType = ModifySettings, Description = Truncate(description, 64) };
            if (!SRSetRestorePointW(ref info, out var status))
            {
                var reason = status.Status == ErrorServiceDisabled
                    ? "System Restore is turned off. Turn it on: System Properties > System Protection > drive C: > Configure."
                    : $"Error {status.Status}";
                Log.Error($"Restore point failed: {reason}");
                return reason;
            }
            var end = new RestorePointInfo { EventType = EndSystemChange, RestorePointType = ModifySettings, SequenceNumber = status.SequenceNumber };
            SRSetRestorePointW(ref end, out _);
            Log.Ok($"Restore point '{description}' created.");
            return null;
        }
        finally
        {
            Reg.Restore(SrKey, "SystemRestorePointCreationFrequency", freq);
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    private const int BeginSystemChange = 100, EndSystemChange = 101, ModifySettings = 12, ErrorServiceDisabled = 1058;

    [StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Unicode)]
    private struct RestorePointInfo
    {
        public int EventType;
        public int RestorePointType;
        public long SequenceNumber;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Description;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct StateMgrStatus
    {
        public int Status;
        public long SequenceNumber;
    }

    [DllImport("srclient.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SRSetRestorePointW(ref RestorePointInfo info, out StateMgrStatus status);
}
