using Frametide.Core.Games;

namespace Frametide.Tests;

public sealed class Cs2Tests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ft-cs2-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Launch_options_are_split_and_rated()
    {
        var hints = Cs2.AnalyzeLaunchOptions("-novid -threads 8 +fps_max 0 -high +exec autoexec.cfg -foo");
        Assert.Equal(["-novid", "-threads 8", "+fps_max 0", "-high", "+exec autoexec.cfg", "-foo"], hints.Select(h => h.Option));
        Assert.Equal([HintLevel.Info, HintLevel.Bad, HintLevel.Info, HintLevel.Warn, HintLevel.Ok, HintLevel.Info], hints.Select(h => h.Level));
        Assert.Empty(Cs2.AnalyzeLaunchOptions("  "));
    }

    [Fact]
    public void Launch_options_are_read_from_the_app_block_only()
    {
        const string vdf = """
            "UserLocalConfigStore"
            {
                "Software" { "Valve" { "Steam" { "apps" {
                    "570" { "LaunchOptions" "-dota" }
                    "730"
                    {
                        "cloud" { "last_sync_state" "synchronized" }
                        "LaunchOptions" "-novid +exec \"my cfg\""
                    }
                } } } }
            }
            """;
        Assert.Equal("-novid +exec \"my cfg\"", Steam.LaunchOptions(vdf, "730"));
        Assert.Equal("-dota", Steam.LaunchOptions(vdf, "570"));
        Assert.Null(Steam.LaunchOptions(vdf, "440"));
    }

    [Fact]
    public void Video_settings_are_named()
    {
        var file = Path.Combine(_dir, "cs2_video.txt");
        File.WriteAllText(file, """
            "video.cfg"
            {
                "setting.defaultres"        "1920"
                "setting.mat_vsync"         "0"
                "setting.r_low_latency"     "2"
                "setting.videocfg_shadow_quality"   "3"
                "setting.unknown"           "1"
            }
            """);
        var s = Cs2.VideoSettings(file);
        Assert.Equal([("Resolution (width)", "1920", false), ("V-Sync", "Off", true), ("NVIDIA Reflex", "Enabled + Boost", true), ("Shadows", "Very high", true)],
            s.Select(v => (v.Name, v.Value, v.Translate)));
    }

    [Theory]
    [InlineData("155.133.226.75", "155.133.226.75")]
    [InlineData(" 162.254.193.6 ", "162.254.193.6")]
    [InlineData("10.0.0.1", null)]
    [InlineData("192.168.1.1", null)]
    [InlineData("172.20.0.1", null)]
    [InlineData("127.0.0.1", null)]
    [InlineData("0.0.0.0", null)]
    [InlineData("255.255.255.255", null)]
    [InlineData("224.0.0.1", null)]
    [InlineData("169.254.1.1", null)]
    [InlineData("100.64.0.1", null)]
    [InlineData("1", null)]
    [InlineData("0x9b.133.226.75", null)]
    [InlineData("::1", null)]
    [InlineData("2001:db8::1", null)]
    public void Only_public_IPv4_relay_addresses_are_blocked(string text, string? expected) =>
        Assert.Equal(expected, ServerBlocker.PublicIPv4(text));
}
