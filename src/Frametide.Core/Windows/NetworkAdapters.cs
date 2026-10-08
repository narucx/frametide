using System.Net.NetworkInformation;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Frametide.Core.Windows;

/// <summary>An advanced driver property ("Energy Efficient Ethernet" etc.) and its allowed values.</summary>
public sealed record AdapterProperty(string Keyword, string DisplayName, string? Value, IReadOnlyDictionary<string, string> Options);

/// <summary>A physical, connected network adapter and its driver key in the registry.</summary>
public sealed record NetworkAdapter(string Name, string Description, string ClassKey, string? InstanceId)
{
    public string RegPath => $@"HKLM:\{ClassKey}";
}

/// <summary>
/// Network adapters via the registry, which is what Device Manager's "Advanced" tab and the NetAdapter cmdlets use:
/// Class\{4d36e972-...}\NNNN holds the current values, Ndi\Params\(keyword) the display name and allowed values.
/// Changes take effect after restarting the adapter.
/// </summary>
public static class NetworkAdapters
{
    private const string ClassRoot = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}";
    private const int NcfPhysical = 0x4;

    public static IReadOnlyList<NetworkAdapter> GetConnectedPhysical()
    {
        var up = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up)
            .ToDictionary(n => n.Id, n => n, StringComparer.OrdinalIgnoreCase);
        var list = new List<NetworkAdapter>();
        using var root = Registry.LocalMachine.OpenSubKey(ClassRoot);
        if (root is null) return list;
        foreach (var sub in root.GetSubKeyNames().Where(n => n.Length == 4 && n.All(char.IsDigit)))
        {
            using var key = root.OpenSubKey(sub);
            if (key is null || (Convert.ToInt32(key.GetValue("Characteristics") ?? 0) & NcfPhysical) == 0) continue;
            if (key.GetValue("NetCfgInstanceId") is not string id || !up.TryGetValue(id, out var nic)) continue;
            list.Add(new NetworkAdapter(nic.Name, nic.Description, $@"{ClassRoot}\{sub}", key.GetValue("DeviceInstanceID") as string));
        }
        return list;
    }

    public static IReadOnlyList<AdapterProperty> GetProperties(NetworkAdapter adapter)
    {
        var list = new List<AdapterProperty>();
        using var key = Registry.LocalMachine.OpenSubKey(adapter.ClassKey);
        using var parms = key?.OpenSubKey(@"Ndi\Params");
        if (key is null || parms is null) return list;
        foreach (var kw in parms.GetSubKeyNames())
        {
            using var p = parms.OpenSubKey(kw);
            using var e = p?.OpenSubKey("enum");
            if (p?.GetValue("ParamDesc") is not string desc) continue;
            var options = e?.GetValueNames().ToDictionary(v => v, v => e.GetValue(v)?.ToString() ?? v) ?? [];
            list.Add(new AdapterProperty(kw, desc, key.GetValue(kw)?.ToString() ?? p.GetValue("default")?.ToString(), options));
        }
        return list;
    }

    public static void SetProperty(NetworkAdapter adapter, string keyword, string value)
    {
        using var key = Registry.LocalMachine.OpenSubKey(adapter.ClassKey, writable: true)
            ?? throw new InvalidOperationException($"Driver key of {adapter.Name} not found");
        key.SetValue(keyword, value, RegistryValueKind.String);
    }

    /// <summary>Restarts the adapter so driver settings take effect (connection drops for 10-15 s).</summary>
    public static void Restart(NetworkAdapter adapter)
    {
        if (adapter.InstanceId is null) return;
        NativeProcess.Run("pnputil.exe", ["/restart-device", adapter.InstanceId], TimeSpan.FromSeconds(60));
    }

    /// <summary>Properties whose display name matches, with the option whose text matches the wanted value.</summary>
    public static IEnumerable<(NetworkAdapter Adapter, AdapterProperty Property, string TargetValue)> FindTargets(Regex name, Regex value, Regex? exclude = null)
    {
        foreach (var a in GetConnectedPhysical())
            foreach (var p in GetProperties(a))
            {
                if (!name.IsMatch(p.DisplayName) || (exclude?.IsMatch(p.DisplayName) ?? false)) continue;
                var target = p.Options.FirstOrDefault(o => value.IsMatch(o.Value)).Key;
                if (target is not null) yield return (a, p, target);
            }
    }
}
