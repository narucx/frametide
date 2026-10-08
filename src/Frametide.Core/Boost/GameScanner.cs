using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Frametide.Core.Boost;

public sealed record InstalledGame(string Source, string Name, string Exe, string Dir);

/// <summary>
/// Finds installed games (Steam, Epic, GOG, Riot, EA, Ubisoft, Battle.net) and the executable that actually runs, e.g. the
/// large *-Win64-Shipping.exe of Unreal Engine games instead of the small launcher next to it.
/// </summary>
public static partial class GameScanner
{
    // Steam app IDs of tools and runtimes, not games.
    private static readonly HashSet<string> SteamIgnore = ["228980", "1070560", "1391110", "1628350", "250820", "431960", "1493710", "2180100", "1826330"];

    // Games where the file heuristic picks the wrong executable.
    private static readonly Dictionary<string, string> KnownSteamExes = new()
    {
        ["730"] = "cs2.exe", ["252950"] = "RocketLeague.exe", ["2225070"] = "Trackmania.exe", ["578080"] = "TslGame.exe",
        ["1172470"] = "r5apex.exe", ["570"] = "dota2.exe", ["440"] = "tf_win64.exe", ["271590"] = "GTA5.exe",
        ["1086940"] = "bg3.exe", ["359550"] = "RainbowSix.exe", ["1938090"] = "cod.exe", ["2357570"] = "Overwatch.exe",
    };

    public static IReadOnlyList<InstalledGame> Scan() =>
        Safe(SteamGames).Concat(Safe(Epic)).Concat(Safe(Gog)).Concat(Safe(OtherLaunchers))
            .GroupBy(g => g.Exe, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
            .OrderBy(g => g.Source).ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    private static List<InstalledGame> Safe(Func<IEnumerable<InstalledGame>> source)
    {
        try { return source().ToList(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or System.Security.SecurityException) { return []; }
    }

    /// <summary>The executable of the game in an install folder, or null.</summary>
    internal static string? FindGameExe(string? dir)
    {
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return null;
        var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 5, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        var exes = new DirectoryInfo(dir).EnumerateFiles("*.exe", options)
            .Where(f => !ExeIgnore().IsMatch(Path.GetFileNameWithoutExtension(f.Name)) && !DirIgnore().IsMatch(f.FullName))
            .ToList();
        // Unreal Engine: the *-Shipping.exe is the real game process.
        var pick = exes.Where(f => f.Name.EndsWith("-Shipping.exe", StringComparison.OrdinalIgnoreCase)).MaxBy(f => f.Length)
                   ?? exes.MaxBy(f => f.Length);
        return pick?.Name;
    }

    private static IEnumerable<InstalledGame> SteamGames()
    {
        foreach (var lib in Games.Steam.Libraries())
        {
            var apps = Path.Combine(lib, "steamapps");
            if (!Directory.Exists(apps)) continue;
            foreach (var acf in Directory.EnumerateFiles(apps, "appmanifest_*.acf"))
            {
                var text = File.ReadAllText(acf);
                var id = AcfField(text, "appid");
                var name = AcfField(text, "name");
                if (id is null || name is null || SteamIgnore.Contains(id) || NotAGame().IsMatch(name)) continue;
                var dir = Path.Combine(apps, "common", AcfField(text, "installdir") ?? "");
                var exe = KnownSteamExes.GetValueOrDefault(id) ?? FindGameExe(dir);
                if (exe is not null) yield return new InstalledGame("Steam", name, exe, dir);
            }
        }
    }

    private static IEnumerable<InstalledGame> Epic()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Epic\EpicGamesLauncher\Data\Manifests");
        if (!Directory.Exists(dir)) yield break;
        foreach (var file in Directory.EnumerateFiles(dir, "*.item"))
        {
            JsonElement j;
            try { j = JsonDocument.Parse(File.ReadAllText(file)).RootElement; }
            catch (JsonException) { continue; }
            if (j.TryGetProperty("bIsApplication", out var app) && app.ValueKind == JsonValueKind.False) continue;
            var loc = Str(j, "InstallLocation");
            if (string.IsNullOrEmpty(loc)) continue;
            var exe = FindGameExe(loc) ?? (Str(j, "LaunchExecutable") is { Length: > 0 } le ? Path.GetFileName(le) : null);
            if (exe is not null) yield return new InstalledGame("Epic", Str(j, "DisplayName") ?? exe, exe, loc);
        }
    }

    private static IEnumerable<InstalledGame> Gog()
    {
        using var root = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\GOG.com\Games");
        if (root is null) yield break;
        foreach (var sub in root.GetSubKeyNames())
        {
            using var k = root.OpenSubKey(sub);
            if (k?.GetValue("exe") is string exe && k.GetValue("gameName") is string name)
                yield return new InstalledGame("GOG", name, Path.GetFileName(exe), k.GetValue("path") as string ?? "");
        }
    }

    // Riot, EA, Ubisoft and Battle.net games: Windows uninstall entries plus a scan of the install folder.
    private static IEnumerable<InstalledGame> OtherLaunchers()
    {
        var roots = new (RegistryKey Hive, string Path)[]
        {
            (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Uninstall"),
        };
        foreach (var (hive, path) in roots)
        {
            using var root = hive.OpenSubKey(path);
            if (root is null) continue;
            foreach (var sub in root.GetSubKeyNames())
            {
                using var k = root.OpenSubKey(sub);
                var pub = k?.GetValue("Publisher") as string ?? "";
                var name = k?.GetValue("DisplayName") as string ?? "";
                var loc = k?.GetValue("InstallLocation") as string ?? "";
                string? src = null;
                if (Has(pub, "Riot Games") && RiotGame().IsMatch(name)) src = "Riot";
                else if (Has(pub, "Electronic Arts") && !EaLauncher().IsMatch(name)) src = "EA";
                else if (Has(pub, "Ubisoft") && !UbiLauncher().IsMatch(name)) src = "Ubisoft";
                else if (Has(pub, "Blizzard") && !Has(name, "Battle.net")) src = "Battle.net";
                if (src is null) continue;
                var exe = Has(name, "VALORANT") ? "VALORANT-Win64-Shipping.exe"
                    : Has(name, "League of Legends") ? "League of Legends.exe"
                    : FindGameExe(loc);
                if (exe is not null) yield return new InstalledGame(src, name, exe, loc);
            }
        }
    }

    private static string? AcfField(string text, string key)
    {
        var m = Regex.Match(text, $"\"{key}\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static bool Has(string s, string part) => s.Contains(part, StringComparison.OrdinalIgnoreCase);

    private static string? Str(JsonElement j, string name) => j.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    [GeneratedRegex(@"unins|setup|install|crash|report|redist|vcredist|dxsetup|directx|dotnet|ndp\d|prereq|launcher|updater|update|helper|cefprocess|cef_|webhelper|easyanticheat|eac_|beservice|battleye|uninstall|7z|python|java|node|ue4prereq|ueprereq|dxwebsetup|oalinst|physx|benchmark|editor|server|dedicated|config|settings|touchup|cleanup|repair|bugreport|unitycrashhandler|zfgamebrowser|overlay", RegexOptions.IgnoreCase)]
    private static partial Regex ExeIgnore();

    [GeneratedRegex(@"\\(_CommonRedist|Redist|Support|DirectX|__Installer|Engine\\Binaries\\ThirdParty|EasyAntiCheat|BattlEye)\\", RegexOptions.IgnoreCase)]
    private static partial Regex DirIgnore();

    [GeneratedRegex(@"Redistributable|Proton|Steam Linux Runtime|SteamVR|Soundtrack|Dedicated Server|SDK", RegexOptions.IgnoreCase)]
    private static partial Regex NotAGame();

    [GeneratedRegex("VALORANT|League of Legends", RegexOptions.IgnoreCase)]
    private static partial Regex RiotGame();

    [GeneratedRegex(@"EA app|Origin", RegexOptions.IgnoreCase)]
    private static partial Regex EaLauncher();

    [GeneratedRegex("Ubisoft Connect|Uplay", RegexOptions.IgnoreCase)]
    private static partial Regex UbiLauncher();
}
