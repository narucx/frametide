using Frametide.Core.Boost;
using Frametide.Core.Windows;
using Microsoft.Win32;

namespace Frametide.Tests;

/// <summary>Uses HKCU\Software\FrametideTests-(guid) instead of the Windows Update settings key.</summary>
[Collection("DataDir")]
public sealed class UpdatePauseTests : IDisposable
{
    private readonly string _sub = $@"Software\FrametideTests-{Guid.NewGuid():N}";
    private readonly string _previousRoot = UpdatePause.Root;
    private static readonly DateTime Now = new(2026, 10, 12, 19, 0, 0, DateTimeKind.Utc);

    public UpdatePauseTests() => UpdatePause.Root = $@"HKCU:\{_sub}";

    public void Dispose()
    {
        UpdatePause.Root = _previousRoot;
        Registry.CurrentUser.DeleteSubKeyTree(_sub, throwOnMissingSubKey: false);
    }

    private string? Value(string name) => Reg.Get(UpdatePause.Root, name).Value as string;

    [Fact]
    public void Pause_and_resume_put_back_what_was_there()
    {
        Reg.Set(UpdatePause.Root, "PauseQualityUpdatesEndTime", RegistryValueKind.String, "2026-01-01T00:00:00Z");
        UpdatePauseState? saved = null;
        var state = UpdatePause.Pause(Now, s => saved = s);

        Assert.NotNull(state);
        Assert.Same(state, saved);
        Assert.Equal("2026-10-12T23:00:00Z", Value("PauseUpdatesExpiryTime"));
        Assert.Equal("2026-10-12T19:00:00Z", Value("PauseUpdatesStartTime"));

        UpdatePause.Resume(state);
        Assert.False(Reg.Get(UpdatePause.Root, "PauseUpdatesExpiryTime").Exists);
        Assert.Equal("2026-01-01T00:00:00Z", Value("PauseQualityUpdatesEndTime"));
    }

    [Fact]
    public void A_pause_set_by_the_user_is_left_alone()
    {
        Reg.Set(UpdatePause.Root, "PauseUpdatesExpiryTime", RegistryValueKind.String, "2026-10-19T19:00:00Z");
        Assert.Null(UpdatePause.Pause(Now, _ => Assert.Fail("nothing may be written")));
        Assert.Equal("2026-10-19T19:00:00Z", Value("PauseUpdatesExpiryTime"));
    }

    [Fact]
    public void Extended_only_when_little_is_left()
    {
        var state = UpdatePause.Pause(Now, _ => { })!;
        Assert.False(UpdatePause.Extend(state, Now.AddHours(2)));
        Assert.True(UpdatePause.Extend(state, Now.AddHours(3.5)));
        Assert.Equal("2026-10-13T02:30:00Z", Value("PauseUpdatesExpiryTime"));
        Assert.Equal(state.Until, Value("PauseQualityUpdatesEndTime"));
    }

    [Fact]
    public void A_pause_changed_in_the_settings_meanwhile_wins()
    {
        var state = UpdatePause.Pause(Now, _ => { })!;
        Reg.Set(UpdatePause.Root, "PauseUpdatesExpiryTime", RegistryValueKind.String, "2026-10-19T19:00:00Z");
        Assert.False(UpdatePause.Extend(state, Now.AddHours(3.5)));
        UpdatePause.Resume(state);
        Assert.Equal("2026-10-19T19:00:00Z", Value("PauseUpdatesExpiryTime"));
    }
}
