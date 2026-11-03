using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;

namespace DOL.GS.HearthDAoC.PlayerBots;

// HearthDAoC player bots (#160, #167 "Logging in and out", step 1): the HEARTHDAOC_PLAYER_BOT_SESSIONS switch, parsed,
// and the spawner's session rules. Pure, so UT_PlayerBotSessions covers them all; PlayerBotSpawner reads the variable
// once at start, with HEARTHDAOC_PLAYER_BOTS. off, the default, changes nothing: every bot logs in at start as fast as
// PlayerBotPacing allows and stays, and a bot that drops out stays out (F1). on: the GameBots' startup ramp
// (AutonomousPopulationRamp.DesiredActiveCount, as AutonomousPopulationController.cs:106 uses it), play sessions that
// end in a player's log-off (PlayerBotLogOffTask), a login after the offline gap where the bot logged off, and a login
// again RelogSeconds after a drop, at most MaxRelogs in a run.

public sealed record PlayerBotSessionSettings(bool On, int RampMinutes, int SessionMinMinutes, int SessionMaxMinutes,
    int OfflineMinMinutes, int OfflineMaxMinutes, int RelogSeconds)
{
    public const string EnvironmentVariable = "HEARTHDAOC_PLAYER_BOT_SESSIONS";
    public const int MaxRampMinutes = 60, MaxSessionMinutes = 1_440, MaxOfflineMinutes = 10_080;
    public const int MinRelogSeconds = 5, MaxRelogSeconds = 3_600;

    public static readonly PlayerBotSessionSettings Off = new(false, 0, 0, 0, 0, 0, 0);

    /// <summary>on alone: the GameBots' 15-minute ramp (startup_ramp_minutes' default,
    /// AutonomousPopulationProperties.cs:26), #160's sessions of 1-4 hours, 30-120 minutes offline, a dropped bot back
    /// after a minute. Step 2's weekly schedule replaces the sessions and the offline gaps.</summary>
    public static readonly PlayerBotSessionSettings Default = new(true, 15, 60, 240, 30, 120, 60);

    public static PlayerBotSessionSettings Parse(string text)
    {
        string value = text?.Trim() ?? "";
        if (value.Length == 0 || value.Equals("off", StringComparison.OrdinalIgnoreCase))
            return Off;
        string[] parts = value.Split(',');
        if (!parts[0].Trim().Equals("on", StringComparison.OrdinalIgnoreCase))
            throw Bad(text);
        PlayerBotSessionSettings settings = Default;
        var seen = new HashSet<string>();
        for (int i = 1; i < parts.Length; i++)
        {
            string[] pair = parts[i].Split('=');
            if (pair.Length != 2)
                throw Bad(text);
            string key = pair[0].Trim().ToLowerInvariant(), number = pair[1].Trim();
            if (!seen.Add(key))
                throw Bad(text);
            switch (key)
            {
                case "ramp":
                    settings = settings with { RampMinutes = Number(number, 0, MaxRampMinutes, text) };
                    break;
                case "session":
                    (int sessionMin, int sessionMax) = Range(number, 1, MaxSessionMinutes, text);
                    settings = settings with { SessionMinMinutes = sessionMin, SessionMaxMinutes = sessionMax };
                    break;
                case "offline":
                    (int offlineMin, int offlineMax) = Range(number, 0, MaxOfflineMinutes, text);
                    settings = settings with { OfflineMinMinutes = offlineMin, OfflineMaxMinutes = offlineMax };
                    break;
                case "relog":
                    settings = settings with { RelogSeconds = Number(number, MinRelogSeconds, MaxRelogSeconds, text) };
                    break;
                default:
                    throw Bad(text);
            }
        }
        return settings;
    }

    public string Describe() => On
        ? FormattableString.Invariant($"on ramp={RampMinutes} session={SessionMinMinutes}-{SessionMaxMinutes} offline={OfflineMinMinutes}-{OfflineMaxMinutes} relog={RelogSeconds}")
        : "off";

    private static int Number(string text, int min, int max, string whole) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n >= min && n <= max ? n : throw Bad(whole);

    // "A-B" (A <= B) or "A" for A-A.
    private static (int Min, int Max) Range(string text, int min, int max, string whole)
    {
        string[] ends = text.Split('-');
        if (ends.Length > 2)
            throw Bad(whole);
        int low = Number(ends[0].Trim(), min, max, whole);
        int high = ends.Length == 2 ? Number(ends[1].Trim(), min, max, whole) : low;
        return low <= high ? (low, high) : throw Bad(whole);
    }

    private static FormatException Bad(string text) => new(
        $"{EnvironmentVariable} must be off, on, or on with any of ramp=MINUTES (0-{MaxRampMinutes}), " +
        $"session=MIN-MAX (1-{MaxSessionMinutes} minutes), offline=MIN-MAX (0-{MaxOfflineMinutes} minutes) and " +
        $"relog=SECONDS ({MinRelogSeconds}-{MaxRelogSeconds}), like on,ramp=2,session=3-6,offline=1-2, got '{text}'");
}

/// <summary>A bot's camp as it left the world (the driver's anchor), for its next login (PlayerBotSpawner).</summary>
public sealed record PlayerBotCamp(ushort Region, Vector3 At);

/// <summary>What the spawner does with a slot whose bot left the world (PlayerBotSessions.AfterLeave).</summary>
public enum PlayerBotLeave { Lost, Offline, Relog }

public static class PlayerBotSessions
{
    public const string QuitReason = "quit";             // HeadlessClient: the quit timer ended (GamePlayer.cs:822-832)
    public const string ForcedReason = "logoff-forced";  // PlayerBotLogOffTask's last resort, out of every human's sight
    public const int MaxRelogs = 5;                      // drops logged in again in a run; the next one is Lost (F1)
    public const int LogOffsPerSecond = 2;               // log-offs asked a second: each ends in a save on the game loop

    /// <summary>How many slots may make their first login of the run now: with sessions off, any (the pacing alone,
    /// PlayerBotPacing); on, the GameBots' ramp target (AutonomousPopulationRamp.DesiredActiveCount: a third of the
    /// roster over the first 5 minutes, all of it by RampMinutes; 0 = all at once) less the slots already started.
    /// Logins after a session or a drop are not counted.</summary>
    public static int FirstLoginRoom(PlayerBotSessionSettings settings, int slots, int started, TimeSpan sinceStart) =>
        !settings.On ? int.MaxValue
        : Math.Max(0, AutonomousPopulationRamp.DesiredActiveCount(true, slots, settings.RampMinutes, sinceStart) - started);

    /// <summary>A bot that left the world: with sessions off, Lost (never logged in again, F1); on, Offline after its
    /// own log-off (the quit timer's end, or the task's forced one), else Relog while drops (this one counted) is at
    /// most MaxRelogs, then Lost.</summary>
    public static PlayerBotLeave AfterLeave(bool sessionsOn, string reason, int drops) =>
        !sessionsOn ? PlayerBotLeave.Lost
        : reason is QuitReason or ForcedReason ? PlayerBotLeave.Offline
        : drops <= MaxRelogs ? PlayerBotLeave.Relog
        : PlayerBotLeave.Lost;

    /// <summary>Whether this login places the slot's start spot (HEARTHDAOC_PLAYER_BOT_SPOTS). Sessions off, as at
    /// 350d60c: every login but the errand's re-login (errandRelogged). On: the run's first world entry only (entries:
    /// the worlds entered so far), so a relogin is where the bot left, with no visible teleport.</summary>
    public static bool PlacesSpot(bool sessionsOn, int entries, bool errandRelogged) =>
        sessionsOn ? entries == 0 : !errandRelogged;

    /// <summary>Whether the self-test slot runs the self-test at this world entry (entries counts it). Sessions off, as
    /// at 350d60c: every time. On: the run's first entry only, not each session's login.</summary>
    public static bool RunsSelfTest(bool sessionsOn, bool selfTestSlot, int entries) =>
        selfTestSlot && (!sessionsOn || entries == 1);

    /// <summary>Whether a small plan's quest fighter gets its errands at this world entry (entries counts it), after
    /// the errand's own re-login is handled. Sessions off, as at 350d60c: every time. On: the run's first entry only,
    /// not each session's login.</summary>
    public static bool GivesErrands(bool sessionsOn, int entries) => !sessionsOn || entries == 1;

    /// <summary>Whether a bot's logout releases it first (HeadlessClient.QuitOnGameLoop). Sessions on: a dead bot, as a
    /// link death's end does (GamePlayer.cs:1131-1137), so a drop's relogin is at its bind with the release's costs,
    /// not alive where it died (WorldInitRequestHandler.cs:108-112). Off: never, as at 350d60c.</summary>
    public static bool ReleasesOnLogout(bool sessionsOn, bool alive) => sessionsOn && !alive;

    /// <summary>Whether a bot keeps its camp (the driver's anchor as it stopped) for its next login: not when it owes a
    /// replan (it died at that camp and hasn't planned away, PlayerBotDriver.OwesReplan), nor when it died since its
    /// log-off began (a death on the log-off owes none: the task holds the think). It then plans afresh where it logs
    /// in, as a GameBot that died replans (AWBC:3780-3816).</summary>
    public static bool KeepsCamp(bool replanOwed, int deathsSinceLogOff) => !replanOwed && deathsSinceLogOff <= 0;

    /// <summary>Why the spawner's BeginLogOff can't give the log-off to the driver yet, or null: no driver (not in the
    /// world, or its self-test still runs), a task it runs (the errands), or the errands' re-login due (that goes
    /// first).</summary>
    public static string LogOffWait(bool driven, bool taskRunning, bool errandRelogDue) =>
        !driven ? "no-driver" : taskRunning ? "task" : errandRelogDue ? "errand-relog" : null;

    /// <summary>Whether a think that finds its bot not alive found it out of the world instead (PlayerBotDriver step
    /// 6): a quit timer's end on another TimerService worker (GamePlayer.cs:827-828) took it out during the think
    /// (Active, then Inactive, then Deleted; a death keeps it Active). Sessions on: no death, the think ends. Off: as
    /// at 350d60c.</summary>
    public static bool LeftDuringThink(bool sessionsOn, bool active) => sessionsOn && !active;

    /// <summary>A session's or an offline gap's length, uniform between min and max minutes, to the second.</summary>
    public static TimeSpan Roll(int minMinutes, int maxMinutes, Random dice) =>
        TimeSpan.FromSeconds(minMinutes * 60 + dice.Next((maxMinutes - minMinutes) * 60 + 1));

    /// <summary>Each slot's own dice, seeded from the slot (PlayerBotKit.Seed, times a prime: a stream apart from the
    /// kit's), so a leg repeats its sessions bot for bot.</summary>
    public static Random Dice(PlayerBotSlot slot) => new(unchecked((int)PlayerBotKit.Seed(slot) * 7_919));
}
