using System.Text.RegularExpressions;

namespace Frametide.Core.Games;

public sealed record Cs2User(string Id, string CfgDir, string? LaunchOptions, DateTime LastUsed, bool Active)
{
    public string VideoFile => Path.Combine(CfgDir, "cs2_video.txt");
    public string MachineConvars => Path.Combine(CfgDir, "cs2_machine_convars.vcfg");
}

public enum HintLevel { Ok, Info, Warn, Bad }

public sealed record LaunchOptionHint(string Option, HintLevel Level, string Text);

public sealed record VideoSetting(string Name, string Value, bool Translate);

/// <summary>Counter-Strike 2: install folder, per-account configuration (Steam userdata), launch options and video settings.</summary>
public static partial class Cs2
{
    public const string AppId = "730";

    public static string? GameDir()
    {
        foreach (var lib in Steam.Libraries())
        {
            var acf = Path.Combine(lib, @"steamapps\appmanifest_730.acf");
            if (!File.Exists(acf)) continue;
            var dir = InstallDir().Match(File.ReadAllText(acf)).Groups[1].Value;
            return Path.Combine(lib, @"steamapps\common", dir);
        }
        return null;
    }

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
    public static Cs2User? CurrentUser() => CurrentUser(Users());

    public static Cs2User? CurrentUser(IReadOnlyList<Cs2User> users) => users.FirstOrDefault(u => u.Active) ?? users.MaxBy(u => u.LastUsed);

    private static readonly (string Pattern, HintLevel Level, string Text)[] Hints =
    [
        ("^-threads", HintLevel.Bad, "Limits worker threads. CS2 picks better on its own, can cost FPS. Remove it."),
        ("^-high$", HintLevel.Warn, "Sets high priority. Game Boost handles this; otherwise it can disturb audio/Discord."),
        ("^-noreflex$", HintLevel.Bad, "Turns off NVIDIA Reflex = more input lag. Remove it."),
        ("^-mainthreadpriority", HintLevel.Warn, "Changes the main thread priority. No proven benefit, can cause stutter on some systems. Leave it out and compare with a benchmark."),
        ("^-tickrate", HintLevel.Info, "Only affects your own local servers, not matchmaking."),
        ("^-novid$|^-nojoy$", HintLevel.Info, "No effect in CS2, harmless."),
        (@"^\+fps_max", HintLevel.Info, "Better set in game (Video > Max FPS)."),
        ("^-vulkan$", HintLevel.Warn, "Vulkan renderer: usually worse frametimes than DX11 on NVIDIA."),
        ("^-allow_third_party_software$", HintLevel.Info, "Only needed for overlays/capture that hook into CS2 (e.g. OBS Game Capture)."),
        (@"^\+exec|^-console$|^-language|^-fullscreen$|^-refresh|^-freq|^\+cl_|^-w$|^-h$", HintLevel.Ok, "OK."),
    ];

    /// <summary>One hint per launch option ("-flag" or "+cvar value").</summary>
    public static IReadOnlyList<LaunchOptionHint> AnalyzeLaunchOptions(string? options)
    {
        if (string.IsNullOrWhiteSpace(options)) return [];
        var result = new List<LaunchOptionHint>();
        foreach (Match m in LaunchToken().Matches(options))
        {
            var option = m.Value.Trim();
            var flag = option.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
            var hint = Hints.FirstOrDefault(h => Regex.IsMatch(flag, h.Pattern, RegexOptions.IgnoreCase));
            result.Add(hint.Text is null
                ? new LaunchOptionHint(option, HintLevel.Info, "Unknown, when in doubt leave it out.")
                : new LaunchOptionHint(option, hint.Level, hint.Text));
        }
        return result;
    }

    private static readonly (string Key, string Name)[] VideoNames =
    [
        ("setting.defaultres", "Resolution (width)"), ("setting.defaultresheight", "Resolution (height)"), ("setting.fullscreen", "Display mode"),
        ("setting.refreshrate_numerator", "Refresh rate"), ("setting.mat_vsync", "V-Sync"), ("setting.r_low_latency", "NVIDIA Reflex"),
        ("setting.msaa_samples", "MSAA"), ("setting.r_csgo_cmaa_enable", "CMAA2"), ("setting.r_csgo_fsr_upsample", "FSR"),
        ("setting.shaderquality", "Shader quality"), ("setting.videocfg_shadow_quality", "Shadows"), ("setting.videocfg_dynamic_shadows", "Dynamic shadows"),
        ("setting.videocfg_texture_detail", "Texture detail"), ("setting.r_texturefilteringquality", "Texture filtering"), ("setting.videocfg_particle_detail", "Particles"),
        ("setting.videocfg_ao_detail", "Ambient occlusion"), ("setting.videocfg_hdr_detail", "HDR"), ("setting.videocfg_fsr_detail", "FSR detail"),
        ("setting.gpu_mem_level", "Texture memory level"), ("setting.cpu_level", "Model detail (CPU)"), ("setting.gpu_level", "Effect detail (GPU)"),
    ];

    private static Dictionary<string, string> OffOn() => new() { ["0"] = "Off", ["1"] = "On" };

    private static Dictionary<string, string> Quality4() => new() { ["0"] = "Low", ["1"] = "Medium", ["2"] = "High", ["3"] = "Very high" };

    private static readonly Dictionary<string, Dictionary<string, string>> VideoValues = new()
    {
        ["setting.fullscreen"] = new() { ["0"] = "Windowed/borderless", ["1"] = "Fullscreen" },
        ["setting.mat_vsync"] = OffOn(),
        ["setting.r_low_latency"] = new() { ["0"] = "Off", ["1"] = "Enabled", ["2"] = "Enabled + Boost" },
        ["setting.msaa_samples"] = new() { ["0"] = "Off", ["2"] = "2x", ["4"] = "4x", ["8"] = "8x" },
        ["setting.r_csgo_cmaa_enable"] = OffOn(),
        ["setting.r_csgo_fsr_upsample"] = OffOn(),
        ["setting.videocfg_dynamic_shadows"] = new() { ["0"] = "Sun only", ["1"] = "All" },
        ["setting.shaderquality"] = new() { ["0"] = "Low", ["1"] = "High" },
        ["setting.videocfg_shadow_quality"] = Quality4(),
        ["setting.videocfg_texture_detail"] = Quality4(),
        ["setting.videocfg_particle_detail"] = Quality4(),
        ["setting.videocfg_ao_detail"] = new() { ["0"] = "Off", ["1"] = "Medium", ["2"] = "High" },
        ["setting.videocfg_hdr_detail"] = new() { ["-1"] = "Performance", ["3"] = "Quality" },
        ["setting.refreshrate_numerator"] = new() { ["0"] = "Automatic" },
    };

    /// <summary>The known settings from cs2_video.txt. Named values (Off, High, ...) are marked for translation.</summary>
    public static IReadOnlyList<VideoSetting> VideoSettings(string file)
    {
        if (!File.Exists(file)) return [];
        var text = File.ReadAllText(file);
        var result = new List<VideoSetting>();
        foreach (var (key, name) in VideoNames)
        {
            var m = Regex.Match(text, "\"" + Regex.Escape(key) + "\"\\s+\"([^\"]*)\"");
            if (!m.Success) continue;
            var raw = m.Groups[1].Value;
            result.Add(VideoValues.TryGetValue(key, out var map) && map.TryGetValue(raw, out var named)
                ? new VideoSetting(name, named, true)
                : new VideoSetting(name, raw, false));
        }
        return result;
    }

    [GeneratedRegex(@"""installdir""\s+""([^""]+)""")]
    private static partial Regex InstallDir();

    [GeneratedRegex(@"[-+][^\s]+(\s+(?![-+])[^\s]+)?")]
    private static partial Regex LaunchToken();
}
