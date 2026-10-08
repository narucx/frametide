namespace Frametide.Core.Boost;

public enum AutoBoostAction { None, Start, Stop, SkipFinished }

/// <summary>
/// Auto Game Boost: start when a listed game runs, stop once no listed game has been running for <see cref="StopDelay"/>.
/// Only a boost started automatically is stopped automatically.
/// </summary>
public sealed class AutoBoost
{
    public static readonly TimeSpan StopDelay = TimeSpan.FromSeconds(20);

    private DateTime? _noGameSince;
    private bool _skipSawGame;

    /// <param name="skipSession">"Next session without Game Boost": one game session is left out; <see cref="AutoBoostAction.SkipFinished"/> once it ended.</param>
    public AutoBoostAction Decide(bool gameRunning, BoostState? state, DateTime now, bool skipSession = false)
    {
        if (gameRunning) _noGameSince = null;
        if (state is null)
        {
            if (skipSession)
            {
                if (gameRunning) { _skipSawGame = true; return AutoBoostAction.None; }
                if (!_skipSawGame) return AutoBoostAction.None;
                _skipSawGame = false;
                return AutoBoostAction.SkipFinished;
            }
            return gameRunning ? AutoBoostAction.Start : AutoBoostAction.None;
        }
        if (!state.Auto || gameRunning) return AutoBoostAction.None;
        _noGameSince ??= now;
        if (now - _noGameSince < StopDelay) return AutoBoostAction.None;
        _noGameSince = null;
        return AutoBoostAction.Stop;
    }
}
