using System.Globalization;
using System.Text.RegularExpressions;
using Frametide.Core.Games;

namespace Frametide.Core.Bench;

/// <summary>An FPS limit found in game or driver settings. <see cref="Fps"/>: 0 = unlimited, null = limited to the refresh rate.</summary>
public sealed record FpsLimit(string Source, int? Fps, string? File = null);

/// <summary>
/// Reads FPS limits from game settings when a recording starts, so a capped average is not mistaken for a result.
/// </summary>
public static partial class FpsLimits
{
    /// <summary>Additional sources (e.g. driver settings) registered by other modules.</summary>
    public static readonly List<Func<string, IEnumerable<FpsLimit>>> Providers = [];

    /// <summary>Games that keep the limit where it cannot be read; the cap is detected from the recording instead.</summary>
    public static readonly IReadOnlyDictionary<string, string> Unreadable = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["RocketLeague.exe"] = "Rocket League keeps its FPS limit in the encrypted profile, Frametide cannot read it. The limit is detected from the recording instead.",
    };

    public static IReadOnlyList<FpsLimit> Read(string exe)
    {
        var found = new List<FpsLimit>();
        foreach (var source in Providers.Append(Cs2Limits).Append(UnrealLimits))
        {
            try { found.AddRange(source(exe)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException or InvalidOperationException) { }
        }
        return found;
    }

    private static IEnumerable<FpsLimit> Cs2Limits(string exe)
    {
        if (!exe.Equals("cs2.exe", StringComparison.OrdinalIgnoreCase) || Cs2.CurrentUser() is not { } user) yield break;
        if (File.Exists(user.MachineConvars) && Cs2FpsMax().Match(File.ReadAllText(user.MachineConvars)) is { Success: true } m)
            yield return new FpsLimit("In game (fps_max)", (int)double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), user.MachineConvars);
        if (user.LaunchOptions is { } lo && LaunchFpsMax().Match(lo) is { Success: true } l)
            yield return new FpsLimit("Launch option +fps_max", int.Parse(l.Groups[1].Value, CultureInfo.InvariantCulture));
    }

    private static IEnumerable<FpsLimit> UnrealLimits(string exe)
    {
        if (UnrealConfig(exe) is not { } file) yield break;
        var text = File.ReadAllText(file);
        if (UnrealFrameRateLimit().Match(text) is { Success: true } m)
            yield return new FpsLimit("In game (FrameRateLimit)", (int)double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), file);
        if (UnrealVsync().IsMatch(text)) yield return new FpsLimit("In-game V-Sync", null, file);
    }

    /// <summary>GameUserSettings.ini of an Unreal Engine game (project name from "Project-Win64-Shipping.exe").</summary>
    private static string? UnrealConfig(string exe)
    {
        if (UnrealExe().Match(exe) is not { Success: true } m) return null;
        var project = m.Groups[1].Value switch { "FortniteClient" => "FortniteGame", var p => p };
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), project, @"Saved\Config");
        if (!Directory.Exists(dir)) return null;
        return new DirectoryInfo(dir).EnumerateFiles("GameUserSettings.ini", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
            .MaxBy(f => f.LastWriteTime)?.FullName;
    }

    [GeneratedRegex(@"""fps_max""\s+""([\d.]+)""")]
    private static partial Regex Cs2FpsMax();

    [GeneratedRegex(@"\+fps_max\s+(\d+)")]
    private static partial Regex LaunchFpsMax();

    [GeneratedRegex(@"(?m)^FrameRateLimit=([\d.]+)")]
    private static partial Regex UnrealFrameRateLimit();

    [GeneratedRegex(@"(?m)^bUseVSync=True")]
    private static partial Regex UnrealVsync();

    [GeneratedRegex(@"^(.+?)-Win(64|GDK)-Shipping\.exe$", RegexOptions.IgnoreCase)]
    private static partial Regex UnrealExe();
}
