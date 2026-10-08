namespace Frametide.Core.Boost;

public enum AutoBoostAction { None, Start, Stop }

/// <summary>
/// Auto Game Boost: start when a listed game runs, stop once no listed game has been running for <see cref="StopDelay"/>.
/// Only a boost started automatically is stopped automatically.
/// </summary>
public sealed class AutoBoost
{
    public static readonly TimeSpan StopDelay = TimeSpan.FromSeconds(20);

    private DateTime? _noGameSince;

    public AutoBoostAction Decide(bool gameRunning, BoostState? state, DateTime now)
    {
        if (gameRunning) _noGameSince = null;
        if (state is null) return gameRunning ? AutoBoostAction.Start : AutoBoostAction.None;
        if (!state.Auto || gameRunning) return AutoBoostAction.None;
        _noGameSince ??= now;
        if (now - _noGameSince < StopDelay) return AutoBoostAction.None;
        _noGameSince = null;
        return AutoBoostAction.Stop;
    }
}
