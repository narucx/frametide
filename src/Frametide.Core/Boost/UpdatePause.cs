using System.Globalization;
using Frametide.Core.Infrastructure;
using Frametide.Core.Windows;
using Microsoft.Win32;

namespace Frametide.Core.Boost;

/// <summary>What Game Boost wrote to pause Windows Update: the values before (null = missing) and the end it set.</summary>
public sealed class UpdatePauseState
{
    public Dictionary<string, string?> Prev { get; set; } = [];
    public string Until { get; set; } = "";
}

/// <summary>
/// Pauses Windows Update while playing, the same way "Pause updates" in the Windows settings does, so no update scan,
/// download or install starts in the middle of a game. The pause always has an end a few hours ahead: if Frametide
/// or the PC crashes, Windows resumes updates by itself.
/// </summary>
public static class UpdatePause
{
    /// <summary>Settings key of Windows Update; tests use a key under HKCU.</summary>
    public static string Root { get; set; } = @"HKLM:\SOFTWARE\Microsoft\WindowsUpdate\UX\Settings";

    public static readonly TimeSpan Length = TimeSpan.FromHours(4);

    /// <summary>Extended while the game still runs once less than this is left.</summary>
    public static readonly TimeSpan ExtendBelow = TimeSpan.FromHours(1);

    private static readonly string[] StartNames = ["PauseUpdatesStartTime", "PauseFeatureUpdatesStartTime", "PauseQualityUpdatesStartTime"];
    private static readonly string[] EndNames = ["PauseUpdatesExpiryTime", "PauseFeatureUpdatesEndTime", "PauseQualityUpdatesEndTime"];
    private const string Expiry = "PauseUpdatesExpiryTime";

    public static string Format(DateTime utc) => utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    public static DateTime? Parse(string? value) =>
        DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t) ? t : null;

    /// <summary>The end of a pause the user set in the Windows settings, if one is running.</summary>
    public static DateTime? UserPauseEnd(DateTime nowUtc) => Parse(Read(Expiry)) is { } end && end > nowUtc ? end : null;

    /// <summary>Pauses updates until now + <see cref="Length"/>. Returns what to put back, or null when updates are paused already.</summary>
    public static UpdatePauseState? Pause(DateTime nowUtc, Action<UpdatePauseState> beforeWrite)
    {
        if (UserPauseEnd(nowUtc) is not null) return null;
        var state = new UpdatePauseState { Until = Format(nowUtc + Length) };
        foreach (var name in StartNames.Concat(EndNames)) state.Prev[name] = Read(name);
        beforeWrite(state);
        foreach (var name in StartNames) Write(name, Format(nowUtc));
        foreach (var name in EndNames) Write(name, state.Until);
        return state;
    }

    /// <summary>Moves the end further when little is left (long sessions). False when nothing had to be done.</summary>
    public static bool Extend(UpdatePauseState state, DateTime nowUtc)
    {
        if (Parse(state.Until) is { } until && until - nowUtc > ExtendBelow) return false;
        // Changed in the Windows settings meanwhile (resumed or paused longer): that wins.
        if (Read(Expiry) != state.Until) return false;
        state.Until = Format(nowUtc + Length);
        foreach (var name in EndNames) Write(name, state.Until);
        return true;
    }

    /// <summary>Puts the values back. Left alone when the pause was changed in the Windows settings meanwhile.</summary>
    public static void Resume(UpdatePauseState state)
    {
        if (Read(Expiry) != state.Until)
        {
            Log.Info("Windows Update: the pause was changed in the Windows settings meanwhile, it is left as it is.");
            return;
        }
        foreach (var (name, value) in state.Prev)
        {
            if (value is null) Reg.Remove(Root, name);
            else Write(name, value);
        }
        Log.Ok("Windows Update resumed.");
    }

    private static string? Read(string name) => Reg.Get(Root, name) is { Exists: true, Value: { } v } ? Convert.ToString(v, CultureInfo.InvariantCulture) : null;

    private static void Write(string name, string value) => Reg.Set(Root, name, RegistryValueKind.String, value);
}
