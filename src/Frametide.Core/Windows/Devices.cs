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

    public const uint PropertyDeviceDesc = 0x01, PropertyClass = 0x08, PropertyFriendlyName = 0x0D;   // CM_DRP_*

    /// <summary>A string property of a device, also of one that is not connected (phantom), or null.</summary>
    public static string? GetProperty(string instanceId, uint property)
    {
        if (CM_Locate_DevNodeW(out var node, instanceId, 1) != 0) return null;   // CM_LOCATE_DEVNODE_PHANTOM
        var buffer = new byte[2048];
        var length = (uint)buffer.Length;
        if (CM_Get_DevNode_Registry_PropertyW(node, property, out _, buffer, ref length, 0) != 0 || length < 2) return null;
        return System.Text.Encoding.Unicode.GetString(buffer, 0, (int)length).TrimEnd('\0');
    }

    [LibraryImport("cfgmgr32.dll")]
    private static partial int CM_Get_DevNode_Registry_PropertyW(uint devInst, uint property, out uint type, [Out] byte[] buffer, ref uint length, uint flags);

    [LibraryImport("cfgmgr32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);

    [LibraryImport("cfgmgr32.dll")]
    private static partial int CM_Get_DevNode_Status(out uint status, out int problem, uint devInst, uint flags);
}
