using Frametide.Core.Infrastructure;
using Frametide.Core.Tweaks;
using Frametide.Core.Windows;
using Microsoft.Win32;

namespace Frametide.Tests;

/// <summary>Engine behavior with test tweaks below HKCU\Software\FrametideTests\(guid) (deleted afterwards).</summary>
public sealed class TweakEngineTests : IDisposable
{
    private readonly string _sub = $@"Software\FrametideTests\{Guid.NewGuid():N}";
    private readonly string _dir = Directory.CreateTempSubdirectory("ft-engine-").FullName;
    private string KeyPath => $@"HKCU:\{_sub}";

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(_sub, throwOnMissingSubKey: false);
        using (var parent = Registry.CurrentUser.OpenSubKey(@"Software\FrametideTests"))
            if (parent is { SubKeyCount: 0, ValueCount: 0 }) Registry.CurrentUser.DeleteSubKey(@"Software\FrametideTests", throwOnMissingSubKey: false);
        Directory.Delete(_dir, recursive: true);
    }

    private RegistryTweak TestTweak() => new()
    {
        Id = "test.reg", Category = "Test", Name = "Test tweak", Description = "",
        Registry = [new RegSetting(KeyPath, "A", RegistryValueKind.DWord, 1L), new RegSetting(KeyPath, "B", RegistryValueKind.String, "on")],
    };

    private (TweakEngine Engine, Journal Journal) Engine(params Tweak[] tweaks)
    {
        var journal = new Journal(Path.Combine(_dir, "journal.json"));
        return (new TweakEngine(tweaks, [], journal), journal);
    }

    [Fact]
    public void Apply_then_revert_restores_exactly_the_previous_state()
    {
        Reg.Set(KeyPath, "A", RegistryValueKind.DWord, 7L);      // existed before with another value; B did not exist
        var t = TestTweak();
        var (engine, journal) = Engine(t);

        Assert.Equal(TweakStatus.NotApplied, engine.GetStatus(t));
        var result = engine.Apply(t);
        Assert.True(result.Success);
        Assert.Equal(TweakStatus.Applied, engine.GetStatus(t));

        Assert.True(engine.Revert(t));
        Assert.Equal(7L, Convert.ToInt64(Reg.Get(KeyPath, "A").Value));
        Assert.False(Reg.Get(KeyPath, "B").Exists);
        Assert.Empty(journal.TweakIds);
    }

    [Fact]
    public void Applying_twice_keeps_the_real_original()
    {
        Reg.Set(KeyPath, "A", RegistryValueKind.DWord, 7L);
        var t = TestTweak();
        var (engine, _) = Engine(t);
        engine.Apply(t);
        engine.Apply(t);
        engine.Revert(t);
        Assert.Equal(7L, Convert.ToInt64(Reg.Get(KeyPath, "A").Value));
    }

    [Fact]
    public void A_tweak_without_backup_is_not_reverted()
    {
        // Already set before: the profile skips the tweak, so nothing is backed up and Revert must leave it alone.
        Reg.Set(KeyPath, "A", RegistryValueKind.DWord, 1L);
        Reg.Set(KeyPath, "B", RegistryValueKind.String, "on");
        var t = TestTweak();
        var (engine, journal) = Engine(t);
        engine.ApplyProfile(new TweakProfile("Test", "", ["test.reg"]));
        Assert.Empty(journal.TweakIds);

        Assert.False(engine.CanRevert(t));
        Assert.Empty(engine.RevertableIds());
        Assert.False(engine.Revert(t));
        Assert.Equal(1L, Convert.ToInt64(Reg.Get(KeyPath, "A").Value));
        Assert.Equal("on", Reg.Get(KeyPath, "B").Value);
    }

    [Fact]
    public void Revert_restores_only_settings_with_a_backup()
    {
        Reg.Set(KeyPath, "A", RegistryValueKind.DWord, 1L);
        Reg.Set(KeyPath, "B", RegistryValueKind.String, "on");
        var t = TestTweak();
        var (engine, journal) = Engine(t);
        journal.SaveOriginal(t.Id, $@"reg|{KeyPath}|A", new RegValue(true, RegistryValueKind.DWord, 7).ToJson());

        Assert.True(engine.Revert(t));
        Assert.Equal(7L, Convert.ToInt64(Reg.Get(KeyPath, "A").Value));
        Assert.Equal("on", Reg.Get(KeyPath, "B").Value);     // no backup: not deleted
        Assert.Empty(journal.TweakIds);
    }

    [Fact]
    public void Revert_removes_a_key_that_only_the_tweak_created()
    {
        var t = TestTweak();
        var (engine, _) = Engine(t);
        Assert.False(Reg.KeyExists(KeyPath));
        Assert.True(engine.Apply(t).Success);
        Assert.True(engine.Revert(t));
        Assert.False(Reg.KeyExists(KeyPath));
    }

    [Fact]
    public void Revert_keeps_a_key_that_existed_before()
    {
        Reg.Set(KeyPath, "Other", RegistryValueKind.DWord, 1L);
        Reg.Remove(KeyPath, "Other");                          // key exists, but empty
        var t = TestTweak();
        var (engine, _) = Engine(t);
        engine.Apply(t);
        engine.Revert(t);
        Assert.True(Reg.KeyExists(KeyPath));
    }

    [Fact]
    public void A_failed_or_incomplete_revert_keeps_the_backup()
    {
        Tweak Make(string id, Exception error) => new CustomTweak
        {
            Id = id, Category = "Test", Name = id, Description = "",
            Status = () => TweakStatus.Applied, ApplyAction = j => j.SaveOriginal(id, "k", "v"), RevertAction = _ => throw error,
        };
        var failing = Make("failing", new InvalidOperationException("powercfg failed"));
        var incomplete = Make("incomplete", new RevertIncompleteException("adapter not found"));
        var (engine, journal) = Engine(failing, incomplete);
        engine.Apply(failing);
        engine.Apply(incomplete);

        Assert.False(engine.Revert(failing));
        Assert.False(engine.Revert(incomplete));
        Assert.Equal(["failing", "incomplete"], journal.TweakIds);
    }

    [Fact]
    public void A_tweak_that_needs_no_backup_can_always_be_reverted()
    {
        var reverted = false;
        var t = new CustomTweak
        {
            Id = "test.nobackup", Category = "Test", Name = "No backup needed", Description = "", RevertNeedsBackup = false,
            Status = () => TweakStatus.Applied, ApplyAction = _ => { }, RevertAction = _ => reverted = true,
        };
        var (engine, _) = Engine(t);
        Assert.True(engine.CanRevert(t));
        Assert.True(engine.Revert(t));
        Assert.True(reverted);
    }

    [Fact]
    public void Status_is_partial_when_only_some_values_match()
    {
        Reg.Set(KeyPath, "A", RegistryValueKind.DWord, 1L);
        var t = TestTweak();
        Assert.Equal(TweakStatus.Partial, Engine(t).Engine.GetStatus(t));
    }

    [Fact]
    public void A_failing_apply_is_reported_as_failure_not_success()
    {
        var t = new CustomTweak
        {
            Id = "test.fail", Category = "Test", Name = "Failing tweak", Description = "", BlockedHint = "hint",
            Status = () => TweakStatus.NotApplied,
            ApplyAction = _ => throw new UnauthorizedAccessException("denied"),
            RevertAction = _ => { },
        };
        var result = Engine(t).Engine.Apply(t);
        Assert.False(result.Success);
        Assert.Equal("denied", result.Error);
        Assert.Equal("hint", result.Hint);
    }

    [Fact]
    public void Apply_without_error_but_without_effect_is_not_a_success()
    {
        var t = new CustomTweak
        {
            Id = "test.noop", Category = "Test", Name = "No effect", Description = "",
            Status = () => TweakStatus.NotApplied, ApplyAction = _ => { }, RevertAction = _ => { },
        };
        Assert.False(Engine(t).Engine.Apply(t).Success);
    }

    [Fact]
    public void Revert_all_only_touches_tweaks_with_a_journal_entry()
    {
        var reverted = new List<string>();
        Tweak Make(string id) => new CustomTweak
        {
            Id = id, Category = "Test", Name = id, Description = "",
            Status = () => TweakStatus.Applied, ApplyAction = j => j.SaveOriginal(id, "k", "v"), RevertAction = _ => reverted.Add(id),
        };
        var a = Make("a"); var b = Make("b");
        var (engine, journal) = Engine(a, b);
        engine.Apply(a);
        engine.RevertAll();
        Assert.Equal(["a"], reverted);
        Assert.Empty(journal.TweakIds);
    }
}

public sealed class TweakCatalogTests
{
    [Fact]
    public void Catalog_is_complete_and_ids_are_unique()
    {
        var ids = TweakCatalog.Tweaks.Select(t => t.Id).ToList();
        Assert.Equal(31, ids.Count);
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.Equal(12, TweakCatalog.Repairs.Count);
    }

    [Fact]
    public void Every_profile_tweak_exists()
    {
        var ids = TweakCatalog.Tweaks.Select(t => t.Id).ToHashSet();
        foreach (var p in TweakProfile.All)
            Assert.All(p.TweakIds, id => Assert.Contains(id, ids));
        Assert.Equal(24, TweakProfile.Recommended.TweakIds.Count);
    }

    /// <summary>Read-only: every status check and every repair detection runs on this PC without throwing.</summary>
    [Fact]
    public void Status_and_detection_never_fail_on_a_real_system()
    {
        var engine = new TweakEngine(TweakCatalog.Tweaks, TweakCatalog.Repairs, new Journal(Path.Combine(Path.GetTempPath(), $"ft-unused-{Guid.NewGuid():N}.json")));
        Assert.All(engine.Tweaks, t => Assert.NotEqual(TweakStatus.Error, engine.GetStatus(t)));
        foreach (var r in engine.Repairs) r.Detect();
    }
}
