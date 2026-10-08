using System.Text.Json.Nodes;
using Frametide.Core.Boost;
using Frametide.Core.Infrastructure;
using Frametide.Core.Windows;
using Microsoft.Win32;

namespace Frametide.Tests;

/// <summary>Uses a temporary data folder and HKCU\Software\FrametideTests\(guid) instead of the real IFEO key.</summary>
public sealed class GameBoostTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ft-boost-").FullName;
    private readonly string _sub = $@"Software\FrametideTests\{Guid.NewGuid():N}";
    private readonly string _previousRoot = LaunchPriority.Root;

    public GameBoostTests()
    {
        AppPaths.UseDataDir(_dir);
        LaunchPriority.Root = $@"HKCU:\{_sub}";
    }

    public void Dispose()
    {
        LaunchPriority.Root = _previousRoot;
        Registry.CurrentUser.DeleteSubKeyTree(_sub, throwOnMissingSubKey: false);
        using (var parent = Registry.CurrentUser.OpenSubKey(@"Software\FrametideTests"))
            if (parent is { SubKeyCount: 0, ValueCount: 0 }) Registry.CurrentUser.DeleteSubKey(@"Software\FrametideTests", throwOnMissingSubKey: false);
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Exe_lists_accept_names_only()
    {
        Assert.Equal(["chrome.exe", "Discord.exe", "steam.exe"], BoostConfig.ParseExeList("chrome\r\nDiscord.exe, steam;  ;C:\\x\\y.exe\nCHROME.EXE"));
        Assert.True(BoostConfig.IsValidExe("League of Legends.exe"));
        Assert.False(BoostConfig.IsValidExe(@"..\evil.exe"));
        Assert.False(BoostConfig.IsValidExe("a/b.exe"));
        Assert.False(BoostConfig.IsValidExe("game"));
    }

    [Fact]
    public void Settings_roundtrip_keeps_other_keys()
    {
        Settings.Set("Language", "de");
        var cfg = new BoostConfig { Games = [new GameEntry { Name = "Game", Exe = "game.exe", Priority = GamePriority.High, Affinity = CoreAffinity.PCores }], PowerPlan = BoostConfig.KeepPowerPlan };
        cfg.Save();

        var node = Settings.GetNode("Games")!.AsArray()[0]!;
        Assert.Equal("High", node["Priority"]!.GetValue<string>());
        Assert.Null(node["ProcessName"]);
        var back = BoostConfig.Load();
        Assert.Equal(CoreAffinity.PCores, back.Games.Single().Affinity);
        Assert.Equal(BoostConfig.KeepPowerPlan, back.PowerPlan);
        Assert.Equal("de", Settings.Language);
    }

    [Fact]
    public void Invalid_game_entries_are_dropped_on_load()
    {
        Settings.Set("Games", JsonNode.Parse("""[{ "Name": "x", "Exe": "..\\x.exe" }, { "Name": "ok", "Exe": "ok.exe" }]"""));
        Assert.Equal(["ok.exe"], BoostConfig.Load().Games.Select(g => g.Exe));
    }

    [Fact]
    public void Protected_processes_and_games_are_never_closed()
    {
        var cfg = new BoostConfig { Games = [new GameEntry { Name = "Game", Exe = "Game.exe" }] };
        Assert.True(GameBoost.IsProtected("explorer.exe", cfg));
        Assert.True(GameBoost.IsProtected("game.EXE", cfg));
        Assert.False(GameBoost.IsProtected("chrome.exe", cfg));
    }

    [Fact]
    public void Launch_priority_is_restored_exactly()
    {
        var root = LaunchPriority.Root;
        // One game already has its own IFEO key with another value and an existing priority.
        Reg.Set($@"{root}\old.exe", "UseLargePages", RegistryValueKind.DWord, 1);
        Reg.Set($@"{root}\old.exe\PerfOptions", "CpuPriorityClass", RegistryValueKind.DWord, 2);

        var entries = LaunchPriority.Set(
        [
            new GameEntry { Exe = "new.exe", Priority = GamePriority.AboveNormal },
            new GameEntry { Exe = "old.exe", Priority = GamePriority.High },
            new GameEntry { Exe = "normal.exe", Priority = GamePriority.Normal },
        ]);
        Assert.Equal(["new.exe", "old.exe"], entries.Select(e => e.Exe));
        Assert.Equal(6, Reg.Get($@"{root}\new.exe\PerfOptions", "CpuPriorityClass").Value);
        Assert.Equal(3, Reg.Get($@"{root}\old.exe\PerfOptions", "CpuPriorityClass").Value);
        Assert.False(Reg.KeyExists($@"{root}\normal.exe"));

        LaunchPriority.Restore(entries);
        Assert.False(Reg.KeyExists($@"{root}\new.exe"));
        Assert.Equal(2, Reg.Get($@"{root}\old.exe\PerfOptions", "CpuPriorityClass").Value);
        Assert.Equal(1, Reg.Get($@"{root}\old.exe", "UseLargePages").Value);
    }

    [Fact]
    public void Persistent_launch_priority_is_tracked_in_the_data_folder()
    {
        var cfg = new BoostConfig { Games = [new GameEntry { Exe = "game.exe" }] };
        LaunchPriority.EnablePersistent(cfg);
        LaunchPriority.EnablePersistent(cfg);   // again: must not back up its own value as "original"
        Assert.Single(LaunchPriority.Persistent);
        Assert.False(LaunchPriority.Persistent[0].PrevExists);

        LaunchPriority.DisablePersistent();
        Assert.False(File.Exists(AppPaths.IfeoState));
        Assert.False(Reg.KeyExists($@"{LaunchPriority.Root}\game.exe"));
    }

    [Fact]
    public void Auto_boost_starts_with_a_game_and_stops_after_the_delay()
    {
        var auto = new AutoBoost();
        var t0 = new DateTime(2026, 1, 1, 20, 0, 0);
        Assert.Equal(AutoBoostAction.None, auto.Decide(false, null, t0));
        Assert.Equal(AutoBoostAction.Start, auto.Decide(true, null, t0));

        var state = new BoostState { Auto = true };
        Assert.Equal(AutoBoostAction.None, auto.Decide(true, state, t0.AddSeconds(3)));
        Assert.Equal(AutoBoostAction.None, auto.Decide(false, state, t0.AddSeconds(6)));
        Assert.Equal(AutoBoostAction.None, auto.Decide(true, state, t0.AddSeconds(20)));    // game back (e.g. restart): timer resets
        Assert.Equal(AutoBoostAction.None, auto.Decide(false, state, t0.AddSeconds(30)));
        Assert.Equal(AutoBoostAction.None, auto.Decide(false, state, t0.AddSeconds(45)));
        Assert.Equal(AutoBoostAction.Stop, auto.Decide(false, state, t0.AddSeconds(51)));
    }

    [Fact]
    public void Auto_boost_never_stops_a_manual_boost()
    {
        var auto = new AutoBoost();
        var manual = new BoostState { Auto = false };
        var t0 = DateTime.Now;
        for (var s = 0; s < 120; s += 3) Assert.Equal(AutoBoostAction.None, auto.Decide(false, manual, t0.AddSeconds(s)));
    }

    [Fact]
    public void Lasso_lists_are_split_into_rules()
    {
        Assert.Equal([["a.exe", "x"], ["b.exe", "y"]], ProcessLasso.Split("a.exe,x,b.exe,y,c.exe", 2).ToList());
        Assert.Empty(ProcessLasso.Split("", 3));
    }
}
