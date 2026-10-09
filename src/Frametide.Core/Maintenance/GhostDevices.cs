using Frametide.Core.Infrastructure;
using Frametide.Core.Windows;
using Microsoft.Win32;

namespace Frametide.Core.Maintenance;

/// <param name="LastSeen">Last time the device was connected or removed (UTC), null when unknown.</param>
public sealed record GhostDevice(string Class, string Name, string InstanceId, DateTime? LastSeen)
{
    /// <summary>
    /// Suggested for removal: not seen for a month and not a kind of device that is often just unplugged or switched
    /// off for a while (dock, headset dongle, controller, Bluetooth, monitor), whose settings would be lost.
    /// </summary>
    public bool Suggested => LastSeen is { } seen && DateTime.UtcNow - seen > TimeSpan.FromDays(30)
        && Class is not ("AudioEndpoint" or "SoftwareComponent" or "PrintQueue" or "Bluetooth" or "Monitor" or "MEDIA" or "USB" or "XboxComposite");
}

/// <summary>
/// Devices Windows remembers but that are not connected anymore ("show hidden devices" in Device Manager): old USB
/// devices, monitors, audio endpoints and so on. Only classes where removing is harmless are listed; a device that is
/// plugged in again is simply installed again.
/// </summary>
public static class GhostDevices
{
    private static readonly HashSet<string> Classes = new(StringComparer.OrdinalIgnoreCase)
    {
        "USB", "HIDClass", "Mouse", "Keyboard", "AudioEndpoint", "MEDIA", "Monitor", "Image", "Camera", "Bluetooth", "WPD",
        "DiskDrive", "Ports", "PrintQueue", "SoftwareComponent", "Biometric", "XboxComposite",
    };

    public static IReadOnlyList<GhostDevice> Find()
    {
        var result = new List<GhostDevice>();
        using var root = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum");
        if (root is null) return result;
        foreach (var enumerator in root.GetSubKeyNames())
        {
            using var e = root.OpenSubKey(enumerator);
            if (e is null) continue;
            foreach (var device in e.GetSubKeyNames())
            {
                using var d = e.OpenSubKey(device);
                if (d is null) continue;
                foreach (var instance in d.GetSubKeyNames())
                {
                    var id = $@"{enumerator}\{device}\{instance}";
                    if (Devices.GetProblem(id) is not null) continue;   // present
                    // The registry values of device keys are not readable for everyone; the device API is.
                    if (Devices.GetProperty(id, Devices.PropertyClass) is not { } cls || !Classes.Contains(cls)) continue;
                    var name = Devices.GetProperty(id, Devices.PropertyFriendlyName) ?? Devices.GetProperty(id, Devices.PropertyDeviceDesc) ?? device;
                    result.Add(new GhostDevice(cls, name, id, Devices.LastSeen(id)));
                }
            }
        }
        return result.OrderBy(g => g.Class).ThenBy(g => g.Name).ToList();
    }

    public static int Remove(IEnumerable<string> instanceIds)
    {
        int ok = 0, total = 0;
        foreach (var id in instanceIds)
        {
            total++;
            try
            {
                var r = NativeProcess.Run("pnputil.exe", ["/remove-device", id]);
                if (r.ExitCode == 0) ok++;
                else Log.Warn($"Removing {id} failed: {r.Output.Trim()}");
            }
            catch (Exception e) { Log.Warn($"Removing {id} failed: {e.Message}"); }
        }
        Log.Ok($"Ghost devices removed: {ok} of {total}.");
        return ok;
    }
}
