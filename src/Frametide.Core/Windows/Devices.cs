using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Frametide.Core.Windows;

/// <summary>Device status via cfgmgr32 (what Device Manager shows) and pnputil for enabling devices.</summary>
public static partial class Devices
{
    public const int ProblemDisabled = 22;   // CM_PROB_DISABLED

    /// <summary>Instance IDs of devices below HKLM\SYSTEM\CurrentControlSet\Enum\(enumerator)\(device id).</summary>
    public static IReadOnlyList<string> GetInstanceIds(string enumeratorAndDeviceId)
    {
        using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{enumeratorAndDeviceId}");
        return key?.GetSubKeyNames().Select(s => $@"{enumeratorAndDeviceId}\{s}").ToList() ?? [];
    }

    /// <summary>Problem code of a present device (0 = working), or null when it is not present.</summary>
    public static int? GetProblem(string instanceId)
    {
        if (CM_Locate_DevNodeW(out var node, instanceId, 0) != 0) return null;
        return CM_Get_DevNode_Status(out _, out var problem, node, 0) == 0 ? problem : null;
    }

    public static void Enable(string instanceId) => NativeProcess.Run("pnputil.exe", ["/enable-device", instanceId]);

    [LibraryImport("cfgmgr32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);

    [LibraryImport("cfgmgr32.dll")]
    private static partial int CM_Get_DevNode_Status(out uint status, out int problem, uint devInst, uint flags);
}
