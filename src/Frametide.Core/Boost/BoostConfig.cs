using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Frametide.Core.Infrastructure;
using Frametide.Core.Windows;

namespace Frametide.Core.Boost;

[JsonConverter(typeof(JsonStringEnumConverter<GamePriority>))]
public enum GamePriority { Normal, AboveNormal, High }

[JsonConverter(typeof(JsonStringEnumConverter<CoreAffinity>))]
public enum CoreAffinity { All, NoCore0, PCores }

public sealed class GameEntry
{
    public string Name { get; set; } = "";
    public string Exe { get; set; } = "";
    public GamePriority Priority { get; set; } = GamePriority.AboveNormal;
    public CoreAffinity Affinity { get; set; } = CoreAffinity.All;

    /// <summary>Process name as Windows reports it: file name without ".exe", lower case.</summary>
    [JsonIgnore]
    public string ProcessName => BoostConfig.ProcessName(Exe);
}

/// <summary>Game Boost settings, stored in config.json.</summary>
public sealed partial class BoostConfig
{
    /// <summary>Value of <see cref="PowerPlan"/> that leaves the power plan alone.</summary>
    public const string KeepPowerPlan = "keep";

    public List<GameEntry> Games { get; set; } = [];
    public List<string> KillList { get; set; } = [];
    public List<string> SuspendList { get; set; } = [];
    public string PowerPlan { get; set; } = PowerCfg.HighPerformance;
    public bool FlushDns { get; set; }
    public bool ClearShaderCache { get; set; }

    /// <summary>GPU profile applied on START ("" = none).</summary>
    public string GpuProfile { get; set; } = "";

    public bool GpuKeepAfterStop { get; set; }

    /// <summary>Start Game Boost when a listed game runs, stop it after the game closed.</summary>
    public bool AutoBoost { get; set; }

    private static readonly string[] Keys =
        [nameof(Games), nameof(KillList), nameof(SuspendList), nameof(PowerPlan), nameof(FlushDns), nameof(ClearShaderCache), nameof(GpuProfile), nameof(GpuKeepAfterStop), nameof(AutoBoost)];

    public static BoostConfig Load()
    {
        var cfg = new BoostConfig();
        var obj = new JsonObject();
        foreach (var k in Keys)
            if (Settings.GetNode(k) is { } node) obj[k] = node;
        try { cfg = obj.Deserialize<BoostConfig>(JsonFile.Options) ?? cfg; }
        catch (JsonException e) { Log.Warn($"Game Boost settings could not be read, using defaults: {e.Message}"); }
        cfg.Games = cfg.Games.Where(g => IsValidExe(g.Exe)).ToList();
        cfg.KillList = cfg.KillList.Where(IsValidExe).ToList();
        cfg.SuspendList = cfg.SuspendList.Where(IsValidExe).ToList();
        return cfg;
    }

    public void Save()
    {
        var obj = JsonSerializer.SerializeToNode(this, JsonFile.Options)!.AsObject();
        Settings.Update(root => { foreach (var k in Keys) root[k] = obj[k]?.DeepClone(); });
    }

    public static string ProcessName(string exe) => Path.GetFileNameWithoutExtension(exe.Trim()).ToLowerInvariant();

    /// <summary>A plain file name ending in .exe. Game names become registry key names (launch priority), so no paths.</summary>
    public static bool IsValidExe(string? exe) => exe is not null && ExePattern().IsMatch(exe);

    /// <summary>Text box content (one entry per line, also "," or ";") to exe names; ".exe" is added when missing.</summary>
    public static List<string> ParseExeList(string text) => text
        .Split(['\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(s => s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? s : s + ".exe")
        .Where(IsValidExe)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    [GeneratedRegex(@"^[^\\/:*?""<>|]+\.exe$", RegexOptions.IgnoreCase)]
    private static partial Regex ExePattern();
}
