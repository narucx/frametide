using System.Text.Json;
using System.Text.Json.Nodes;
using Frametide.Core.Infrastructure;

namespace Frametide.Core.Gpu;

/// <summary>Saved GPU tuning (gpu-profiles.json). The measurements are filled in by the smart undervolt.</summary>
public sealed class GpuProfile
{
    public string Name { get; set; } = "";
    public int MaxClock { get; set; }
    public int OffsetMHz { get; set; }
    public int PowerLimitW { get; set; }
    public string? Gpu { get; set; }
    public string? Driver { get; set; }
    public DateTime? Created { get; set; }
    public double? StockAvgPowerW { get; set; }
    public double? StockAvgClock { get; set; }
    public double? StockVoltage { get; set; }
    public double? AvgPowerW { get; set; }
    public double? AvgClock { get; set; }
    public double? Voltage { get; set; }
    public double? PowerSavedW { get; set; }
    public double? VoltageDropMv { get; set; }
    public double? PowerSavedPct { get; set; }
    public double? MaxTemp { get; set; }
}

public static class GpuProfiles
{
    public static event Action? Changed;

    public static IReadOnlyList<GpuProfile> All()
    {
        try
        {
            var node = JsonFile.ReadNode(AppPaths.GpuProfiles)?["Profiles"];
            // A single profile may have been written as an object instead of an array.
            var array = node as JsonArray ?? (node is JsonObject o ? [o.DeepClone()] : []);
            return array.Deserialize<List<GpuProfile>>(JsonFile.Options)?.Where(p => p.Name.Length > 0).ToList() ?? [];
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException) { Log.Warn($"GPU profiles could not be read: {e.Message}"); return []; }
    }

    public static GpuProfile? Find(string name) => All().FirstOrDefault(p => p.Name == name);

    /// <summary>Adds or replaces (same name) a profile.</summary>
    public static void Save(GpuProfile profile)
    {
        profile.Name = CleanName(profile.Name);
        var list = All().Where(p => p.Name != profile.Name).Append(profile).ToList();
        JsonFile.Write(AppPaths.GpuProfiles, new { Profiles = list });
        Changed?.Invoke();
    }

    public static void Delete(string name)
    {
        JsonFile.Write(AppPaths.GpuProfiles, new { Profiles = All().Where(p => p.Name != name).ToList() });
        Changed?.Invoke();
    }

    /// <summary>
    /// Profile names end up quoted in a scheduled task's command line: no quotes or control characters, and no
    /// backslash at the end (it would escape the closing quote).
    /// </summary>
    public static string CleanName(string name)
    {
        var clean = new string(name.Where(c => c != '"' && !char.IsControl(c)).ToArray()).Trim();
        while (clean.EndsWith('\\')) clean = clean[..^1].TrimEnd();
        return clean;
    }
}
