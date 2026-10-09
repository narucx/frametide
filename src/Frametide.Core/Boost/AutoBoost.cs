namespace Frametide.Core.Boost;

public enum AutoBoostAction { None, Start, Stop, SkipFinished }

/// <summary>
/// Auto Game Boost: start when a listed game runs, stop once no listed game has been running for <see cref="StopDelay"/>.
/// Only a boost started automatically is stopped automatically. A boost stopped by hand while the game runs is not
/// started again until the game closed.
/// </summary>
public sealed class AutoBoost
{
    public static readonly TimeSpan StopDelay = TimeSpan.FromSeconds(20);

    private DateTime? _noGameSince;
    private bool _skipSawGame;
    private bool _hadState;
    private bool _stoppedManually;

    /// <param name="skipSession">"Next session without Game Boost": one game session is left out; <see cref="AutoBoostAction.SkipFinished"/> once it ended.</param>
    public AutoBoostAction Decide(bool gameRunning, BoostState? state, DateTime now, bool skipSession = false)
    {
        if (gameRunning) _noGameSince = null;
        // The boost is gone without a Stop from here: STOP was pressed (page, tray). Respected until the game closed.
        if (state is null && _hadState && gameRunning) _stoppedManually = true;
        _hadState = state is not null;
        if (!gameRunning) _stoppedManually = false;
        if (state is null)
        {
            if (skipSession)
            {
                if (gameRunning) { _skipSawGame = true; return AutoBoostAction.None; }
                if (!_skipSawGame) return AutoBoostAction.None;
                _skipSawGame = false;
                return AutoBoostAction.SkipFinished;
            }
            return gameRunning && !_stoppedManually ? AutoBoostAction.Start : AutoBoostAction.None;
        }
        if (!state.Auto || gameRunning) return AutoBoostAction.None;
        _noGameSince ??= now;
        if (now - _noGameSince < StopDelay) return AutoBoostAction.None;
        _noGameSince = null;
        _hadState = false;   // this STOP is ours
        return AutoBoostAction.Stop;
    }
}
