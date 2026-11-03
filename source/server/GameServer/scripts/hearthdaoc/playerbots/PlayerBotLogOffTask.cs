using System.Numerics;
using DOL.Logging;
using static System.FormattableString;

namespace DOL.GS.HearthDAoC.PlayerBots;

// HearthDAoC player bots (#160, #167 "Logging in and out", step 1): a session's end as a player logs off. The spawner
// gives it to the driver as its task (PlayerBotSpawner.BeginLogOff); it runs at the driver's step 10, so no new pull,
// rest, stand or self-cast while it holds the think. First the fight: while a monster hits the bot, or the target it
// engaged is still in the fight, the driver fights it (PlayerBotDriver.UnderAttack, the errand task's rule). Then, once
// per try, a spot out of the reach of every monster round it that would aggro (PlayerBotDriver.LogOffSpot), the walk
// there (standing first if it sat to rest: a walk doesn't stand it), a stop, and the player's /quit
// (GamePlayer.Quit(false), GamePlayer.cs:1057-1098): the sit, the session statistics, the quit timer of 20 s or up to
// 60 s after combat (GamePlayer.cs:718-833), sitting still. A hit stands it and the driver fights (the quit is
// cancelled, GamePlayer.cs:8793-8802); then it tries again. A refused /quit (moving, mounted, crafting, an instance:
// GamePlayer.cs:1059-1084) lets the bot play on for a minute, then it tries again. At the timer's end the server saves
// the bot and takes it out of the world, and HeadlessClient logs out with reason quit
// (HeadlessClient.OnPlayerQuitSent). Only after 10 minutes or 5 refusals, and only with no human in sight
// (PlayerBotDriver.SeenByHuman), does it log out without the timer (reason logoff-forced). It never quits dead: a dead
// bot's think ends at step 6, before any task.
public sealed class PlayerBotLogOffTask : IPlayerBotTask
{
    private static readonly Logger Log = LoggerManager.Create(typeof(PlayerBotLogOffTask));

    private readonly HeadlessClient _client;
    private readonly long _since;
    private bool _quitAsked, _spotChecked, _forced;
    private Vector3? _spot;
    private long _quitAt, _playUntil, _spotAt;
    private int _refusals, _cancels, _walks;

    public PlayerBotLogOffTask(HeadlessClient client, long now)
    {
        _client = client;
        _since = now;
    }

    public bool Finished => false;   // it ends with the bot's logout: the driver stops (PlayerBotDriver.Stop)

    public bool Tick(PlayerBotState state, long now)
    {
        GamePlayer player = state.Player;
        bool sitting = player.IsSitting;
        if (_quitAsked && !sitting)                                  // stood up (a fight, a cast): the quit is off
        {
            (_quitAsked, _spotChecked, _spot) = (false, false, null);
            _cancels++;
        }
        bool givesUp = PlayerBotLogOffPlan.GivesUp(now - _since, _refusals);
        bool walking = _spot != null && now - _spotAt < PlayerBotLogOffPlan.WalkMilliseconds;   // a long walk: quit there
        PlayerBotLogOffStep step = PlayerBotLogOffPlan.Next(PlayerBotDriver.UnderAttack(state, now), sitting, _quitAsked,
            now - _quitAt, now < _playUntil, _spotChecked, walking, player.IsMoving, givesUp,
            givesUp && PlayerBotDriver.SeenByHuman(player, null, default));
        switch (step)
        {
            case PlayerBotLogOffStep.Fight:
            case PlayerBotLogOffStep.Play:
                return false;                                        // the driver fights, or plays on after a refusal
            case PlayerBotLogOffStep.Hold:
                return true;                                         // seated: the mover's idle broadcast keeps X/Y
            case PlayerBotLogOffStep.Force:
                if (!_forced)
                {
                    _forced = true;
                    Report(player, "forced", now);
                    _client.Logout(PlayerBotSessions.ForcedReason);   // saves, then quits (HeadlessClient.QuitOnGameLoop)
                }
                return true;
            case PlayerBotLogOffStep.Find:
                (_spotChecked, _spotAt) = (true, now);
                _spot = PlayerBotDriver.LogOffSpot(player);
                return true;
            case PlayerBotLogOffStep.Stand:
                player.Sit(false);                                   // seated from a rest: up before the walk
                return true;
            case PlayerBotLogOffStep.Walk:
                PlayerBotStepResult walked = state.Mover.StepToward(_spot.Value, 20, now);
                if (walked is PlayerBotStepResult.Arrived or PlayerBotStepResult.Blocked)
                {
                    _spot = null;                                    // there, or as near as it gets: it quits here
                    _walks++;
                }
                return true;
            case PlayerBotLogOffStep.Stop:
                state.Mover.Stop(now);
                return true;
            default:                                                 // Quit
                state.Mover.Stop(now);
                if (_quitAsked) _refusals++;                         // 90 s seated and still here: asked again
                if (player.Quit(false))
                {
                    (_quitAsked, _quitAt) = (true, now);
                    Report(player, "quit", now);
                }
                else
                {
                    _refusals++;
                    (_spotChecked, _spot, _playUntil) = (false, null, now + PlayerBotLogOffPlan.RefusedPlayMilliseconds);
                }
                return true;
        }
    }

    // PLAYER_BOT_LOGOFF: each /quit asked (what=quit) and a forced logout (what=forced), where the bot sits (the next
    // login's position, unless it is hit and moves), for the leg's checks.
    private void Report(GamePlayer player, string what, long now)
    {
        if (!Log.IsInfoEnabled) return;
        Log.Info(Invariant($"PLAYER_BOT_LOGOFF {what} bot={player.Name} level={player.Level} sec={(now - _since) / 1000} refusals={_refusals} cancels={_cancels} walks={_walks} inCombat={player.InCombat} region={player.CurrentRegionID} x={player.X} y={player.Y} z={player.Z}"));
    }
}

/// <summary>The log-off's step this think (PlayerBotLogOffPlan.Next).</summary>
public enum PlayerBotLogOffStep { Fight, Play, Find, Walk, Stop, Quit, Hold, Force, Stand }

/// <summary>The log-off's decisions, from what the bot sees, with no server (UT_PlayerBotSessions).</summary>
public static class PlayerBotLogOffPlan
{
    public const int QuitWaitMilliseconds = 90_000;      // the quit timer's longest, 60 s (GamePlayer.cs:720), and slack
    public const int RefusedPlayMilliseconds = 60_000;   // a refused /quit: the bot plays on this long, then tries again
    public const int GiveUpMilliseconds = 600_000;       // then (or after MaxRefusals) the logout without the timer
    public const int WalkMilliseconds = 60_000;          // the walk to the spot (2,500 out at most): then it quits there
    public const int MaxRefusals = 5;

    public static bool GivesUp(long msInTask, int refusals) => msInTask >= GiveUpMilliseconds || refusals >= MaxRefusals;

    /// <summary>In order: the fight first (underAttack: the driver fights, as the errands let it); a quit timer running
    /// (asked, still seated, under QuitWaitMilliseconds) holds; given up and out of every human's sight, the forced
    /// logout; after a refusal it plays on until its time (playing); then the spot (once per try), the walk to it (a
    /// bot seated from a rest stands first: the mover's steps don't stand it, GamePlayer.cs:7973), a stop, and
    /// /quit.</summary>
    public static PlayerBotLogOffStep Next(bool underAttack, bool sitting, bool quitAsked, long msSinceQuit, bool playing,
        bool spotChecked, bool walking, bool moving, bool givesUp, bool seenByHuman)
    {
        if (underAttack) return PlayerBotLogOffStep.Fight;
        if (quitAsked && sitting && msSinceQuit < QuitWaitMilliseconds) return PlayerBotLogOffStep.Hold;
        if (givesUp && !seenByHuman) return PlayerBotLogOffStep.Force;
        if (playing) return PlayerBotLogOffStep.Play;
        if (!spotChecked) return PlayerBotLogOffStep.Find;
        if (walking) return sitting && !quitAsked ? PlayerBotLogOffStep.Stand : PlayerBotLogOffStep.Walk;
        if (moving) return PlayerBotLogOffStep.Stop;
        return PlayerBotLogOffStep.Quit;
    }
}
