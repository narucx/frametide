namespace Frametide.Core.Games;

public sealed record Cs2User(string Id, string CfgDir, string? LaunchOptions, DateTime LastUsed, bool Active)
{
    public string VideoFile => Path.Combine(CfgDir, "cs2_video.txt");
    public string MachineConvars => Path.Combine(CfgDir, "cs2_machine_convars.vcfg");
}

/// <summary>Counter-Strike 2 per-account configuration (Steam userdata).</summary>
public static class Cs2
{
    public const string AppId = "730";

    public static IReadOnlyList<Cs2User> Users()
    {
        if (Steam.InstallPath() is not { } steam) return [];
        var root = Path.Combine(steam, "userdata");
        if (!Directory.Exists(root)) return [];
        var active = Steam.ActiveUser();
        var users = new List<Cs2User>();
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            var cfg = Path.Combine(dir, AppId, @"local\cfg");
            var local = Path.Combine(dir, @"config\localconfig.vdf");
            var launch = File.Exists(local) ? Steam.LaunchOptions(File.ReadAllText(local), AppId) : null;
            if (!Directory.Exists(cfg) && launch is null) continue;
            var id = Path.GetFileName(dir);
            users.Add(new Cs2User(id, cfg, launch, File.Exists(local) ? File.GetLastWriteTime(local) : DateTime.MinValue, id == active));
        }
        return users;
    }

    /// <summary>The logged-in account, otherwise the most recently used one.</summary>
    public static Cs2User? CurrentUser()
    {
        var users = Users();
        return users.FirstOrDefault(u => u.Active) ?? users.MaxBy(u => u.LastUsed);
    }
}
