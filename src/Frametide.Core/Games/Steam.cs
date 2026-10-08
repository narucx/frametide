using System.Text.RegularExpressions;
using Frametide.Core.Windows;

namespace Frametide.Core.Games;

/// <summary>Steam install, library folders and accounts (userdata folders).</summary>
public static partial class Steam
{
    public static string? InstallPath()
    {
        if (Reg.Get(@"HKCU:\Software\Valve\Steam", "SteamPath").Value is string p && p.Length > 0) return p.Replace('/', '\\');
        return Reg.Get(@"HKLM:\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath").Value as string;
    }

    /// <summary>All library folders (the main install and the ones in libraryfolders.vdf).</summary>
    public static IReadOnlyList<string> Libraries()
    {
        if (InstallPath() is not { } steam) return [];
        var libs = new List<string> { steam };
        var vdf = Path.Combine(steam, @"steamapps\libraryfolders.vdf");
        if (File.Exists(vdf)) libs.AddRange(VdfPath().Matches(File.ReadAllText(vdf)).Select(m => m.Groups[1].Value.Replace(@"\\", @"\")));
        return libs.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The account that is logged in right now (= userdata folder name), or null.</summary>
    public static string? ActiveUser() =>
        Reg.Get(@"HKCU:\Software\Valve\Steam\ActiveProcess", "ActiveUser").Value is int id && id > 0 ? ((uint)id).ToString() : null;

    /// <summary>Launch options of an app from an account's localconfig.vdf (searches the app's block, brace-aware).</summary>
    public static string? LaunchOptions(string localConfigText, string appId)
    {
        foreach (Match m in Regex.Matches(localConfigText, $"\"{appId}\"\\s*\\{{"))
        {
            int i = m.Index + m.Length, depth = 1, start = i;
            while (i < localConfigText.Length && depth > 0)
            {
                if (localConfigText[i] == '{') depth++;
                else if (localConfigText[i] == '}') depth--;
                i++;
            }
            var lo = LaunchOptionsPattern().Match(localConfigText, start, i - start);
            if (lo.Success) return lo.Groups[1].Value.Replace("\\\"", "\"");
        }
        return null;
    }

    [GeneratedRegex(@"""path""\s+""([^""]+)""")]
    private static partial Regex VdfPath();

    [GeneratedRegex(@"""LaunchOptions""\s+""((?:[^""\\]|\\.)*)""")]
    private static partial Regex LaunchOptionsPattern();
}
