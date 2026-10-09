using System.Text.RegularExpressions;

namespace Frametide.Core.Windows;

public sealed record PowerScheme(string Guid, string Name, bool Active);

/// <summary>Power plans via powercfg.exe. Output parsing is language independent (GUIDs and hex values only).</summary>
public static partial class PowerCfg
{
    public const string UsbSubgroup = "2a737441-1930-4402-8d77-b2bebba308a3";
    public const string UsbSelectiveSuspend = "48e6b7a6-50f5-4782-a5d4-53bb8f07e226";
    public const string HighPerformance = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";

    public static IReadOnlyList<PowerScheme> GetSchemes()
    {
        var r = NativeProcess.Run("powercfg.exe", ["/list"]);
        return SchemePattern().Matches(r.Output)
            .Select(m => new PowerScheme(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value.Trim().Length > 0))
            .ToList();
    }

    /// <summary>AC and DC index of a setting; the last two hex values of "powercfg /query" are AC and DC.</summary>
    public static (int Ac, int Dc)? GetSetting(string scheme, string subgroup, string setting)
    {
        var r = NativeProcess.Run("powercfg.exe", ["/query", scheme, subgroup, setting]);
        var hex = HexPattern().Matches(r.Output).Select(m => Convert.ToInt32(m.Value, 16)).ToList();
        return hex.Count < 2 ? null : (hex[^2], hex[^1]);
    }

    /// <summary>Throws when powercfg fails (e.g. the plan no longer exists), so a revert keeps its backup.</summary>
    public static void SetSetting(string scheme, string subgroup, string setting, int ac, int dc)
    {
        foreach (var (verb, value) in new[] { ("/setacvalueindex", ac), ("/setdcvalueindex", dc) })
        {
            var r = NativeProcess.Run("powercfg.exe", [verb, scheme, subgroup, setting, value.ToString()]);
            if (r.ExitCode != 0) throw new InvalidOperationException($"powercfg {verb} {scheme} failed: {(r.Output + r.Error).Trim()}");
        }
    }

    /// <summary>Re-activates the current plan so changed values take effect.</summary>
    public static void Refresh() => NativeProcess.Run("powercfg.exe", ["/setactive", "SCHEME_CURRENT"]);

    public static bool SetActive(string scheme) => NativeProcess.Run("powercfg.exe", ["/setactive", scheme]).ExitCode == 0;

    [GeneratedRegex(@"([0-9a-fA-F-]{36})\s+\((.+?)\)(\s*\*)?")]
    private static partial Regex SchemePattern();

    [GeneratedRegex("0x[0-9a-fA-F]{8}")]
    private static partial Regex HexPattern();
}
