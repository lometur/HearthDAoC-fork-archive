using System;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading;
using DOL.Logging;

namespace DOL.GS.HearthDAoC.PlayerBots;

/// <summary>One report window: the counters since the last line, and a snapshot of the bots now.</summary>
public readonly record struct PlayerBotDriverWindow(
    int Seconds, int Bots, long Ticks, double TickMs, double MoverMs,
    int Waiting, int Dead, int Resting, int Hunting, int Fighting, int Casting, int Roaming, int Task,
    long Kills, long Deaths, long LevelUps, int MaxLevel, long Experience, long Blocked, long Stuck, long LinkDeathsCancelled,
    long Errors, double NearPlayerBots, double NearGamebots, int GamebotNearbyHuman, int GamebotStandard, int GamebotEfficient);

/// <summary>#188: one report window's changes of island by the mover's steps, over all bots (PLAYER_BOT_CROSSING win=):
/// taken (onto the camp's island or a larger one), the floor under the step taken in its place, and refused; then (the
/// final review of #188) the driver's ring points on the mesh dropped for their island before any walk (OnMesh).</summary>
public readonly record struct PlayerBotCrossingWindow(int Seconds, long Took, long Ground, long Refused, long Dropped);

/// <summary>One settled fight (levelmatch/DEATHS.md 2): the bot's health and power at its first think in reach and now,
/// the most attackers on a think, the swings seen and those the view gate refused, the casts, and this life's kills and
/// sits before it; then whether it had a living pet at that first think and its heals on itself since (DEATHS.md A5,
/// A6), and whether a cast in it went with Quickcast on; then (#167) its self-buffs up at that first think (F8), a
/// Theurgist's pets cast at the target (F1), and the thinks a cast due waited for its own swing's interrupt (F6); a
/// caster's control casts and steps back (F11), the holds on its target its own swing ended (C3, BrokeHold), and its
/// control casts at an add (C4; in Controls too); then (#167, the next leg's) the whole seconds into the fight a second
/// monster first hit the bot or came at it and its health then (FirstAdd), the fight dropped by a break-off (C2), the
/// distance at that first think, and the pick's crowding (CrowdingAlong, C5): -1 for none, a defence or Incoming's pick;
/// then (#167, st-deathsD's) the whole seconds its target was held (Hold), its control casts that held or not
/// (ControlLanded), its and its target's health at the first hit on it (-1: none), the health it lost a second over the
/// last 3 s (HealthRate; a gain is negative), its health when the projected break-off would have fired (ProjectedBreak,
/// logged only; -1: never), and the walk on the think before the fight's first (F12d's field); then (#167 D8) the step
/// backs that found no point, by why (KiteNone: range, clear, mesh, fit; none given, 0 each); then (#167 E C1) its
/// damage-over-time casts (Dots), (#167 E C2b) whether a cast went under Quickcast as the killing nuke
/// (KillingQuickcast), and (#167 E C4) its mezzes at the target (TargetMezzes); then (#167 E-L) the race's projection
/// of its target's health at its death, at its first think under 40% with a read (PlayerBotDriver.TargetLeftAtDeath;
/// -999: none); then (#167 E-R) its endurance at the fight's first think and at its end, and the style choices and the
/// thinks that held a style or a hybrid's damage cast back for a run's reserve (PlayerBotDriver.StyleWithin,
/// HoldsCast); then (#167 E M2) the sight checks its target hitting it in contact answered (Sight: the navmesh's ray
/// said no; PlayerBotDriver.SightInReach) and whether it dropped the target for want of sight (Blind); then (#167 E R1)
/// its steps in toward the target out of sight (BlindSteps; PlayerBotDriver.BlindStepStop); then (#167 F H1) its heal
/// requests that began a cast that lasted to the next think or landed (HealCasts; PlayerBotDriver.HealBegan) and the
/// heals it cast (HealsLanded; CastFinished); then (#167 F H2) the thinks it held its swing for a heal (HealHolds;
/// PlayerBotDriver.SelfHealStep's Hold) and its longest hold in thinks (HealHoldMax); then (#167 F N1b) a Necromancer's
/// power-tap casts (Taps); then (#167 F C1) the fight's add step in a word (AddWhy; PlayerBotDriver.FightAddWhy); then
/// (#167 F C2) of CtlFailed, the controls the server said were resisted (CtlResisted) or immune (CtlImmune), the rest
/// refused, interrupted or unsaid, and the casts under Quickcast at a control (QcControls) and of Engage's damage spell,
/// a Theurgist's pet too (QcNukes); then (#167 F-R) the highest endurance reserve the fight read for a run (Reserve;
/// PlayerBotDriver.ReserveNow; 0: none); then (#167 F P1) whether the rings' pick of a melee or archer bot was made
/// with its crowd limits lifted (Lifted; PlayerBotDriver.CrowdLifted); then (#167 F P1b) of the rings' pick's crowd,
/// the aggressive adds over the bot's cap (Over; CrowdingAlong's; -1: another pick); then (#167 F P2) the bot's own
/// kills' spots coming due by the target or its stand point (Due; PlayerBotDriver.DueAfterKill, CrowdingAt; -1: unread,
/// a healer's or pet class's pick, or another pick); then (#167 F P4, log only) for a target not the rings' pick,
/// whether that pick counted it, its search scanned it, or never saw it (Seen; PlayerBotDriver.SeenAtPick; "-": a
/// rings' pick), and the potential adds along the corridor of the pick's walk in (PathCrowd;
/// PlayerBotDriver.CrowdingAlongPath; -1: unchecked); then (#167 F F5) the rings' pick a target that is no rings' pick
/// (a defence, a monster coming at the bot) replaced as the bot walked to it, its crowding and name (HuntCrowd,
/// HuntTarget; PlayerBotDriver.HuntReplaced): -1 and none for a rings' pick, or with none replaced; then (#167 F R2)
/// whether the fight began on the rest its kill skipped (RestSkip: the first fight after the skip, with no sit between;
/// PlayerBotDriver.SkipsRestAfterKill); then (#167 F-WD) the seconds the capped clock had run when the fight began
/// (CapHold; PlayerBotDriver.CapHoldAtFight; -1: none) and the steps out of reach its hold took (HoldSteps;
/// PlayerBotDriver.HoldStepsClear; -1: no capped clock).</summary>
public readonly record struct PlayerBotFight(
    string Bot, int Level, string Target, int TargetLevel, ConColor Con, bool FromDefence, bool Killed,
    byte StartHp, byte EndHp, byte StartMana, byte EndMana, long Seconds, int Adds, int Swings, int NotVisible, int Casts,
    int LifeKills, int Rests, bool Pet = false, int Heals = 0, bool Quickcast = false, int Buffs = 0, int PetCasts = 0,
    int Waits = 0, int Controls = 0, int Kites = 0, int CtlBroken = 0, int AddControls = 0, int AddAt = -1, int AddHp = -1,
    bool BreakOff = false, int EngageDistance = -1, int Crowd = -1, long HeldSeconds = 0, int CtlLanded = 0, int CtlFailed = 0,
    int HpAtReach = -1, int TargetHpAtReach = -1, double Rate = 0, int WouldBreak = -1, PlayerBotWalk Walk = PlayerBotWalk.None,
    (int Range, int Clear, int Mesh, int Fit)? KiteNone = null, int Dots = 0, bool KillingQuickcast = false,
    int TargetMezzes = 0, int RaceLeft = -999, int EndStart = -1, int End = -1, int StylesHeld = 0, int CastsHeld = 0,
    int Sight = 0, bool Blind = false, int BlindSteps = 0, int HealCasts = 0, int HealsLanded = 0,
    int HealHolds = 0, int HealHoldMax = 0, int Taps = 0, PlayerBotAddWhy AddWhy = PlayerBotAddWhy.None, int CtlResisted = 0,
    int CtlImmune = 0, int QcControls = 0, int QcNukes = 0, int Reserve = 0, bool Lifted = false, int Over = -1,
    int Due = -1, string Seen = "-", int PathCrowd = -1, int HuntCrowd = -1, string HuntTarget = null,
    bool RestSkip = false, int CapHold = -1, int HoldSteps = -1);

/// <summary>What a PLAYER_BOT_DEATH line adds after its first fields (levelmatch/DEATHS.md 2); -1 for none: the fight's
/// fields (startHp, fightSec, adds, swings, notVisible) out of a fight, targetHp with no target, sinceKillSec with no kill
/// this life, fromBind with the bind in another region. Avoided: the names the bot avoided as it died, before this
/// death's (DEATHS.md A3). Again (#167 F4): no kill since its last release (DiedAgain); SinceRelease: the seconds since
/// it, -1 with none this login; Ill: the resurrection illness on at the fatal fight's start (false out of a fight). Then
/// (#167 C3, C4) the fatal fight's caster fields as PLAYER_BOT_FIGHT gives them, Quickcast 1 or 0: -1 out of a fight; and
/// FIGHT's AddAt, AddHp and EngageDistance (-1 out of a fight too), and BreakOff: the bot died on a break-off's run (C2;
/// out of a fight then, as the break-off dropped it). Then (#167, st-deathsD's) FIGHT's HeldSeconds, CtlLanded,
/// CtlFailed, HpAtReach, TargetHpAtReach and WouldBreak (-1 out of a fight), Rate at the death, Walk: the fight's, or out
/// of a fight the last think's alive, and Endurance; then (#167 D8) FIGHT's KiteNone (none: out of a fight, -1 each);
/// then (#167 E C1, C2b, C4) FIGHT's Dots, KillingQuickcast (1 or 0) and TargetMezzes (-1 each out of a
/// fight); then (#167 E-L) FIGHT's RaceLeft (-999 out of a fight), and (#167 E-R) FIGHT's StylesHeld and CastsHeld (-1
/// each out of a fight); then (#167 F H1) FIGHT's heals=, healCast= and healLanded=, (#167 F H2) healHold= and
/// healHoldMax=, and (#167 F N1b) taps= (-1 each out of a fight); then (#167 F C1) FIGHT's addWhy= (none out of a fight:
/// -1); then (#167 F C2) FIGHT's CtlResisted, CtlImmune, QcControls and QcNukes (-1 each out of a fight), and at any
/// death Quickcast's remaining disable in seconds (QcLeft, -1 without it); then (#167 F P1) FIGHT's Crowd, the rings'
/// pick's crowding of the target (-1: no target, or another pick), and (#167 F P1b) FIGHT's Over and (#167 F P2)
/// Due, and (#167 F P4) FIGHT's Seen and PathCrowd; then (#167 F F5) FIGHT's HuntCrowd and HuntTarget (with a target;
/// -1 and none without) and the fatal fight's BlindSteps (-1 out of a fight); then (#167 F R2) FIGHT's RestSkip, 1 or
/// 0 (-1 out of a fight); then (#167 F-WD) FIGHT's CapHold and HoldSteps (-1 each out of a fight).</summary>
public readonly record struct PlayerBotDeathDetail(
    PlayerBotActivity Activity, int StartHp, long FightSeconds, int TargetHp, bool FromDefence, int Adds, int LifeKills,
    int Rests, long SinceKillSeconds, byte Mana, int Swings, int NotVisible, string Killer, int KillerLevel,
    bool KillerIsTarget, int FromBind, int Avoided = 0, bool Again = false, long SinceRelease = -1, bool Ill = false,
    int Controls = -1, int Kites = -1, int Casts = -1, int Quickcast = -1, int Waits = -1, int CtlBroken = -1,
    int AddControls = -1, int AddAt = -1, int AddHp = -1, bool BreakOff = false, int EngageDistance = -1, long HeldSeconds = -1,
    int CtlLanded = -1, int CtlFailed = -1, int HpAtReach = -1, int TargetHpAtReach = -1, double Rate = 0, int WouldBreak = -1,
    PlayerBotWalk Walk = PlayerBotWalk.None, int Endurance = -1, (int Range, int Clear, int Mesh, int Fit)? KiteNone = null,
    int Dots = -1, int KillingQuickcast = -1, int TargetMezzes = -1, int RaceLeft = -999, int StylesHeld = -1,
    int CastsHeld = -1, int Heals = -1, int HealCasts = -1, int HealsLanded = -1,
    int HealHolds = -1, int HealHoldMax = -1, int Taps = -1, PlayerBotAddWhy? AddWhy = null, int CtlResisted = -1,
    int CtlImmune = -1, int QcControls = -1, int QcNukes = -1, int QcLeft = -1, int Crowd = -1, int Over = -1,
    int Due = -1, string Seen = "-", int PathCrowd = -1, int HuntCrowd = -1, string HuntTarget = null,
    int BlindSteps = -1, int RestSkip = -1, int CapHold = -1, int HoldSteps = -1);

/// <summary>#167 F: what the rings' pick read for the target it took (PlayerBotDriver.FindWithin): FIGHT crowd= (the live
/// potential adds CrowdingAlong counted), over= (those over the bot's cap, P1b), due= (its kills' respawns due, P2) and
/// pathCrowd= (the corridor's count, P4; -1 unchecked), and lifted= (P1: a melee or archer bot's pick made with its
/// crowd limits lifted). None (all -1): a pick that was not the rings' (self-defence, a monster coming at it,
/// TurnOn).</summary>
public readonly record struct PlayerBotPickReading(int Crowd, int Over, int Due, int PathCrowd, bool Lifted = false)
{
    public static readonly PlayerBotPickReading None = new(-1, -1, -1, -1);
}

// HearthDAoC player bots (#137): one PLAYER_BOT_DRIVER line a minute, then the lever's PLAYER_BOT_WAKE line and (#188) the
// PLAYER_BOT_CROSSING win= line, from a thread-pool timer off the game loop, and every TallyEveryWindows windows a
// PLAYER_BOT_TALLY line per bot (the level-matched run). At Stop, a last CROSSING line for the part of a window since.
// The driver times itself (think, mover) because its TimerService row also holds the bot's other timers. The driver's
// own lines are formatted here too: PLAYER_BOT_FIGHT per settled fight and the deaths' fields (levelmatch/DEATHS.md 2).
public static class PlayerBotDriverReport
{
    private static readonly Logger Log = LoggerManager.Create(typeof(PlayerBotDriverReport));
    public const int WindowSeconds = 60;
    public const int TallyEveryWindows = 5;   // PLAYER_BOT_TALLY at windows 5, 10, ... 60 (levelmatch/DESIGN.md 2.7)

    private static long _ticks, _tickStopwatch, _moverStopwatch, _kills, _deaths, _levelUps, _experience, _blocked, _stuck,
        _ldCancelled, _errors;
    private static long _crossingTook, _crossingGround, _crossingRefused, _crossingDropped;    // #188
    private static long _windowStarted;                                                        // #188: its start (Stopwatch)
    private static int _maxLevel, _windows;
    private static Timer _timer;
    private static readonly ConditionalWeakTable<GameLiving, StrongBox<long>> _countedLives = new();

    public static void AddTick(long stopwatchTicks) { Interlocked.Increment(ref _ticks); Interlocked.Add(ref _tickStopwatch, stopwatchTicks); }
    public static void AddMover(long stopwatchTicks) => Interlocked.Add(ref _moverStopwatch, stopwatchTicks);
    // One kill per monster life (its SpawnTick, set at each AddToWorld, GameObject.cs:614), however many player bots
    // fought it: each bot whose engaged target dies reports it, and a start row's bots share their monsters (runs 2-3).
    // False when another bot already counted this life.
    public static bool Kill(GameLiving monster)
    {
        StrongBox<long> counted = _countedLives.GetValue(monster, _ => new StrongBox<long>(long.MinValue));
        long life = monster.SpawnTick;
        if (Interlocked.Exchange(ref counted.Value, life) == life) return false;
        Interlocked.Increment(ref _kills);
        return true;
    }
    public static void Death() => Interlocked.Increment(ref _deaths);
    public static void Experience(long amount) => Interlocked.Add(ref _experience, amount);
    public static void Blocked() => Interlocked.Increment(ref _blocked);
    public static void StuckRecovered() => Interlocked.Increment(ref _stuck);
    public static void LinkDeathCancelled() => Interlocked.Increment(ref _ldCancelled);
    public static void Error() => Interlocked.Increment(ref _errors);
    // #188: each change of island a step would make (PlayerBotPathMover.ReportCrossing), for PLAYER_BOT_CROSSING win=.
    public static void CrossingTook() => Interlocked.Increment(ref _crossingTook);
    public static void CrossingGround() => Interlocked.Increment(ref _crossingGround);
    public static void CrossingRefused() => Interlocked.Increment(ref _crossingRefused);
    // The final review of #188: each ring point on the mesh the driver drops for its island (PlayerBotDriver.OnMesh), the
    // guard's common case live, which no step sees.
    public static void CrossingDropped() => Interlocked.Increment(ref _crossingDropped);

    public static void LevelUp(int levels, int newLevel)
    {
        Interlocked.Add(ref _levelUps, levels);
        int seen;
        while (newLevel > (seen = Volatile.Read(ref _maxLevel)) && Interlocked.CompareExchange(ref _maxLevel, newLevel, seen) != seen) { }
    }

    public static void EnsureStarted()
    {
        if (Volatile.Read(ref _timer) != null) return;
        Timer timer = new(_ => Write(), null, Timeout.Infinite, Timeout.Infinite);
        if (Interlocked.CompareExchange(ref _timer, timer, null) != null) { timer.Dispose(); return; }
        Volatile.Write(ref _windowStarted, Stopwatch.GetTimestamp());
        timer.Change(WindowSeconds * 1000, WindowSeconds * 1000);
    }

    /// <summary>The server's stop (PlayerBotDriver.OnServerStopped). #188: once the report ran, a last PLAYER_BOT_CROSSING
    /// line, for the part of a window since the last line, so that a leg's lines sum to its every change of island and every dropped ring point.</summary>
    public static void Stop()
    {
        Timer timer = Interlocked.Exchange(ref _timer, null);
        Interlocked.Exchange(ref _windows, 0);
        if (timer == null) return;
        timer.Dispose();
        long since = Stopwatch.GetTimestamp() - Volatile.Read(ref _windowStarted);
        PlayerBotCrossingWindow last = TakeCrossings((int)Math.Round((double)since / Stopwatch.Frequency));
        if (Log.IsInfoEnabled) Log.Info(FormatCrossing(last));
    }

    /// <summary>#188: the changes of island (and ring points dropped) since the last call, for a PLAYER_BOT_CROSSING win=
    /// line of the given seconds; the counts start from zero again.</summary>
    public static PlayerBotCrossingWindow TakeCrossings(int seconds) => new(seconds, Interlocked.Exchange(ref _crossingTook, 0),
        Interlocked.Exchange(ref _crossingGround, 0), Interlocked.Exchange(ref _crossingRefused, 0),
        Interlocked.Exchange(ref _crossingDropped, 0));

    private static void Write()
    {
        try
        {
            int bots = 0, waiting = 0, dead = 0, resting = 0, hunting = 0, fighting = 0, casting = 0, roaming = 0, task = 0, maxLevel = 0;
            long nearPlayerBots = 0, nearGamebots = 0;
            foreach (PlayerBotState s in PlayerBotDriver.States)
            {
                bots++;
                switch (s.Activity)
                {
                    case PlayerBotActivity.Waiting: waiting++; break;
                    case PlayerBotActivity.Dead: dead++; break;
                    case PlayerBotActivity.Resting: resting++; break;
                    case PlayerBotActivity.Hunting: hunting++; break;
                    case PlayerBotActivity.Fighting: fighting++; break;
                    case PlayerBotActivity.Casting: casting++; break;
                    case PlayerBotActivity.Roaming: roaming++; break;
                    case PlayerBotActivity.Task: task++; break;
                }
                nearPlayerBots += s.NearPlayerBots;
                nearGamebots += s.NearGamebots;
                maxLevel = Math.Max(maxLevel, s.LastLevel);
            }
            int human = 0, standard = 0, efficient = 0;
            foreach (GameBot bot in AutonomousBotRegistry.Snapshot())           // AutonomousBotRegistry.cs:75-85
            {
                switch (bot.AutonomousFidelity)                                  // GameBot.cs:82, set each think (BotBrain.cs:1330-1332)
                {
                    case eAutonomousFidelity.NearbyHuman: human++; break;
                    case eAutonomousFidelity.Standard: standard++; break;
                    default: efficient++; break;
                }
            }
            double toMs = 1000.0 / Stopwatch.Frequency;
            PlayerBotDriverWindow w = new(WindowSeconds, bots, Interlocked.Exchange(ref _ticks, 0),
                Interlocked.Exchange(ref _tickStopwatch, 0) * toMs, Interlocked.Exchange(ref _moverStopwatch, 0) * toMs,
                waiting, dead, resting, hunting, fighting, casting, roaming, task,
                Interlocked.Exchange(ref _kills, 0), Interlocked.Exchange(ref _deaths, 0), Interlocked.Exchange(ref _levelUps, 0),
                Math.Max(maxLevel, Interlocked.Exchange(ref _maxLevel, 0)), Interlocked.Exchange(ref _experience, 0),
                Interlocked.Exchange(ref _blocked, 0), Interlocked.Exchange(ref _stuck, 0), Interlocked.Exchange(ref _ldCancelled, 0),
                Interlocked.Exchange(ref _errors, 0),
                bots == 0 ? 0 : (double)nearPlayerBots / bots, bots == 0 ? 0 : (double)nearGamebots / bots, human, standard, efficient);
            if (Log.IsInfoEnabled) Log.Info(Format(w));
            PlayerBotWakeWindow wake = PlayerBotWake.TakeWindow(WindowSeconds);   // its own line: the DRIVER format is pinned
            if (Log.IsInfoEnabled) Log.Info(PlayerBotWake.Format(wake));
            Volatile.Write(ref _windowStarted, Stopwatch.GetTimestamp());          // #188: its own line too, every window
            PlayerBotCrossingWindow crossing = TakeCrossings(WindowSeconds);
            if (Log.IsInfoEnabled) Log.Info(FormatCrossing(crossing));
            int window = Interlocked.Increment(ref _windows);
            if (window % TallyEveryWindows == 0 && Log.IsInfoEnabled)
                foreach (PlayerBotState s in PlayerBotDriver.States)                   // fields written on the loop: whole-value reads
                    Log.Info(FormatTally(window, s.Player.Name, s.LastLevel, Volatile.Read(ref s.Kills), Volatile.Read(ref s.Deaths),
                        Interlocked.Read(ref s.Gained), s.NetExperience, Volatile.Read(ref s.CastFailures),
                        Volatile.Read(ref s.Retrains), s.Activity));
        }
        catch (Exception e)
        {
            if (Log.IsWarnEnabled) Log.Warn($"PLAYER_BOT_DRIVER report failed: {e.Message}");
        }
    }

    /// <summary>One bot's own counts since its login. Window N's lines follow the N-th DRIVER line: the counts at the end
    /// of window N (the start of the [N, ...) range summarize.py and legcheck.py use). xp is the gains the DRIVER line sums;
    /// netXp is its experience now minus at login (REVIEW-FIXES 9: a death's loss shows there).</summary>
    public static string FormatTally(int window, string bot, int level, int kills, int deaths, long xp, long netXp,
        int castFailed, int retrained, PlayerBotActivity activity) => string.Create(CultureInfo.InvariantCulture,
        $"PLAYER_BOT_TALLY window={window} bot={bot} level={level} kills={kills} deaths={deaths} xp={xp} netXp={netXp} " +
        $"castFailed={castFailed} retrained={retrained} activity={activity}");

    /// <summary>PLAYER_BOT_FIGHT, from the driver when a fight it was in settles: the monster died (kill) or is no longer
    /// fair game (dropped). lifeKills and rests are this life's before the fight; tools/deathdiag.py reads the line.</summary>
    public static string FormatFight(PlayerBotFight f) => string.Create(CultureInfo.InvariantCulture,
        $"PLAYER_BOT_FIGHT bot={f.Bot} level={f.Level} target=\"{f.Target}\" targetLevel={f.TargetLevel} con={f.Con} " +
        $"from={(f.FromDefence ? "defence" : "pull")} result={(f.Killed ? "kill" : "dropped")} startHp={f.StartHp} endHp={f.EndHp} " +
        $"startMana={f.StartMana} endMana={f.EndMana} sec={f.Seconds} adds={f.Adds} swings={f.Swings} notVisible={f.NotVisible} " +
        $"casts={f.Casts} lifeKills={f.LifeKills} rests={f.Rests} pet={(f.Pet ? "alive" : "none")} heals={f.Heals} " +
        $"qc={(f.Quickcast ? 1 : 0)} buffs={f.Buffs} petCasts={f.PetCasts} waits={f.Waits} ctl={f.Controls} kites={f.Kites} " +
        $"ctlBroken={f.CtlBroken} addCtl={f.AddControls} addAt={f.AddAt} addHp={f.AddHp} breakOff={(f.BreakOff ? 1 : 0)} " +
        $"engageDist={f.EngageDistance} crowd={f.Crowd} heldSec={f.HeldSeconds} ctlLanded={f.CtlLanded} ctlFailed={f.CtlFailed} " +
        $"hpAtReach={f.HpAtReach} tHpAtReach={f.TargetHpAtReach} rate={FormatRate(f.Rate)} wouldBreak={f.WouldBreak} " +
        $"walk={FormatWalk(f.Walk)} kiteNone={FormatKiteNone(f.KiteNone ?? default)} dots={f.Dots} " +
        $"qcKill={(f.KillingQuickcast ? 1 : 0)} ctlMez={f.TargetMezzes} raceLeft={f.RaceLeft} endStart={f.EndStart} " +
        $"end={f.End} styleHeld={f.StylesHeld} castHeld={f.CastsHeld} sight={f.Sight} blind={(f.Blind ? 1 : 0)} " +
        $"blindSteps={f.BlindSteps} healCast={f.HealCasts} healLanded={f.HealsLanded} healHold={f.HealHolds} " +
        $"healHoldMax={f.HealHoldMax} taps={f.Taps} addWhy={FormatAddWhy(f.AddWhy)} ctlResisted={f.CtlResisted} " +
        $"ctlImmune={f.CtlImmune} qcCtl={f.QcControls} qcNuke={f.QcNukes} reserve={f.Reserve} lifted={(f.Lifted ? 1 : 0)} " +
        $"over={f.Over} due={f.Due} seen={f.Seen ?? "-"} pathCrowd={f.PathCrowd} huntCrowd={f.HuntCrowd} " +
        $"huntTarget=\"{f.HuntTarget}\" restSkip={(f.RestSkip ? 1 : 0)} capHold={f.CapHold} holdStep={f.HoldSteps}");

    /// <summary>#167 F C1 addWhy=: none, ctl, pets, noControl, tries, noneTakes, rootContact, targetFirst, hitNoQc,
    /// closeNoQc, los or noAdd.</summary>
    public static string FormatAddWhy(PlayerBotAddWhy why) => why switch
    {
        PlayerBotAddWhy.Control => "ctl", PlayerBotAddWhy.Pets => "pets", PlayerBotAddWhy.NoControl => "noControl",
        PlayerBotAddWhy.Tries => "tries", PlayerBotAddWhy.NoneTakes => "noneTakes",
        PlayerBotAddWhy.RootInContact => "rootContact", PlayerBotAddWhy.TargetFirst => "targetFirst",
        PlayerBotAddWhy.HitNoQc => "hitNoQc", PlayerBotAddWhy.CloseNoQc => "closeNoQc", PlayerBotAddWhy.Los => "los",
        PlayerBotAddWhy.NoAdd => "noAdd", _ => "none",
    };

    /// <summary>#167 rate=: one decimal, a loss under 0.05 a second (either way) as 0.0, not -0.0.</summary>
    private static string FormatRate(double rate) =>
        (Math.Round(rate, 1) is var rounded && rounded != 0 ? rounded : 0).ToString("0.0", CultureInfo.InvariantCulture);

    /// <summary>#167 walk=: none, hunt, anchor, roam, clear, rest, kite or breakoff.</summary>
    private static string FormatWalk(PlayerBotWalk walk) => walk.ToString().ToLowerInvariant();

    /// <summary>#167 D8 kiteNone=range:clear:mesh:fit, the step backs that found no point by why (PlayerBotDriver.KiteWalk).</summary>
    private static string FormatKiteNone((int Range, int Clear, int Mesh, int Fit) k) => $"{k.Range}:{k.Clear}:{k.Mesh}:{k.Fit}";

    /// <summary>PLAYER_BOT_REST (#167), when a rest ends: the seconds the bot sat and why it stood (PlayerBotDriver.EndRest);
    /// then its health and power when it sat, and the killed monsters' spawn spots its rest spot was read against (C7);
    /// then (#167 D10) its rest walks blocked before it sat (PlayerBotDriver.RestGoalAt's tries), and inReach=1 when it
    /// sat within a potential add's reach as its rest check read them; then (#167 E-R) end=, its endurance when it sat
    /// (-1: none given).</summary>
    public static string FormatRest(string bot, int level, long seconds, string why, int hp, int mp, int respawn,
        int blocked, bool inReach, int end = -1) =>
        string.Create(CultureInfo.InvariantCulture,
            $"PLAYER_BOT_REST bot={bot} level={level} sec={seconds} why={why} hp={hp} mp={mp} respawn={respawn} " +
            $"blocked={blocked} inReach={(inReach ? 1 : 0)} end={end}");

    /// <summary>PLAYER_BOT_BACK (#167 C1), at the first fight after a release (PlayerBotDriver.ReportBack): the seconds
    /// since the release, those stepping out of reach, held (the back-off, the illness, the replan's hold) and walking
    /// (Roam; #167 D4, the walk out toward the camp planned under the release's immunity), the replan
    /// (PlayerBotDriver.BackReplan), and (#167 D4) the seconds it walked while immune: the step out and the walk out.</summary>
    public static string FormatBack(string bot, int level, long seconds, long stepOut, long hold, long walk, string replan,
        long immune) =>
        string.Create(CultureInfo.InvariantCulture,
            $"PLAYER_BOT_BACK bot={bot} level={level} sec={seconds} stepOut={stepOut} hold={hold} walk={walk} replan={replan} " +
            $"immune={immune}");

    /// <summary>PLAYER_BOT_BREAKOFF (#167 C2), when a break-off ends (PlayerBotDriver.EndBreakOff): the bot's health,
    /// endurance and attackers when it broke off, the seconds it ran, and how it ended (shook: none still after it;
    /// fought: still chased, or nowhere to run, it turned on them; died); then (#167, st-deathsD's) the health it lost a
    /// second over the 3 s before it broke off (PlayerBotDriver.HealthRate), and (#167 D1) why=, the rule that fired
    /// (PlayerBotDriver.BreakOffWhy). #167 D2: one that would fire but doesn't, once a fight (PlayerBotDriver.ReportRefusal):
    /// outcome=refused, sec=0, the bot's health, endurance and monsters on it then, and why= why not
    /// (PlayerBotDriver.BreakOffRefusal). Then (#167 E-S) sprintOff=, the seconds into the run its sprint first went off
    /// (PlayerBotDriver.BreakOffSprint; -1: never, or none given), and endLeft=, its endurance at the run's end (a refused
    /// one's: then). Then (#167 E T1) turn=, why a fought one turned (caught, nopoint, blocked or cap; - for none),
    /// holds=, its one-second tries that found no point while it held (PlayerBotDriver.NoPointTurns), and speed= and
    /// chaserSpeed=, its run speed and the fastest chaser's when it began (191 unencumbered, less when encumbered; a
    /// refusal's too; -1 before they are read). Then (#167 F-O) sprint=, the seconds its sprint lasts from its endurance
    /// at the start (PlayerBotDriver.SprintSeconds; 999: no end; -1: not read), a refusal's too. Then (#167 F-W) joined=,
    /// the monsters that joined its chasers on the run (one that came back counts again), and wayAdds=, 1 when its
    /// point's straight way passed another monster's reach (no clear one), 0 when clear, -1 with no point
    /// (PlayerBotDriver.WayInAddReach). Then (#167 F-C) qcLeft=, a caster's Quickcast disable left at the start, whole
    /// seconds rounded up (PlayerBotDriver.QuickcastLeftSeconds, as DEATH qcLeft=; -1: no caster, or no Quickcast).
    /// why= is (#167 F-C) cantfinish too: a caster hit with its Quickcast spent that would die before it came back and
    /// a nuke was cast. Then (#167 F-CS) cfSkip=, 1 when a why=cantfinish run marked its target's name for the pick and
    /// the plan (PlayerBotDriver.MarkCantFinish: yellow or harder, no kill of that name at yellow or harder in 15
    /// minutes), 0 when it didn't, -1 for every other why and on a refusal.</summary>
    public static string FormatBreakOff(string bot, int level, int hp, int end, int attackers, long seconds, string outcome,
        double rate, string why, long sprintOff = -1, int endLeft = -1, string turn = "-", int holds = 0, int speed = -1,
        int chaserSpeed = -1, int sprint = -1, int joined = 0, int wayAdds = -1, int qcLeft = -1, int cfSkip = -1) =>
        string.Create(CultureInfo.InvariantCulture,
            $"PLAYER_BOT_BREAKOFF bot={bot} level={level} hp={hp} end={end} attackers={attackers} sec={seconds} outcome={outcome} " +
            $"rate={FormatRate(rate)} why={why} sprintOff={sprintOff} endLeft={endLeft} turn={turn} holds={holds} " +
            $"speed={speed} chaserSpeed={chaserSpeed} sprint={Math.Min(sprint, 999)} joined={joined} wayAdds={wayAdds} " +
            $"qcLeft={qcLeft} cfSkip={cfSkip}");

    /// <summary>PLAYER_BOT_RELEASE (#167 D7), each release the driver asks for (PlayerBotDriver.ReleaseDue): its try in
    /// this death (a release that didn't revive the bot goes again 10 s on) and the milliseconds since the death (the
    /// later of the server's DeathTick and the bot's first dead think).</summary>
    public static string FormatRelease(string bot, int level, int attempt, long sinceDeath) =>
        string.Create(CultureInfo.InvariantCulture,
            $"PLAYER_BOT_RELEASE bot={bot} level={level} try={attempt} sinceDeath={sinceDeath}");

    /// <summary>The fields PLAYER_BOT_DEATH appends after task= (the ones before stay as they were: legcheck.py reads them).</summary>
    public static string FormatDeathDetail(PlayerBotDeathDetail d) => string.Create(CultureInfo.InvariantCulture,
        $"activity={d.Activity} startHp={d.StartHp} fightSec={d.FightSeconds} targetHp={d.TargetHp} " +
        $"from={(d.FromDefence ? "defence" : "pull")} adds={d.Adds} lifeKills={d.LifeKills} rests={d.Rests} " +
        $"sinceKillSec={d.SinceKillSeconds} mana={d.Mana} swings={d.Swings} notVisible={d.NotVisible} killer=\"{d.Killer}\" " +
        $"killerLevel={d.KillerLevel} killerIsTarget={d.KillerIsTarget} fromBind={d.FromBind} avoided={d.Avoided} " +
        $"again={d.Again} sinceRelease={d.SinceRelease} ill={d.Ill} ctl={d.Controls} kites={d.Kites} casts={d.Casts} " +
        $"qc={d.Quickcast} waits={d.Waits} ctlBroken={d.CtlBroken} addCtl={d.AddControls} addAt={d.AddAt} addHp={d.AddHp} " +
        $"breakOff={(d.BreakOff ? 1 : 0)} engageDist={d.EngageDistance} heldSec={d.HeldSeconds} ctlLanded={d.CtlLanded} " +
        $"ctlFailed={d.CtlFailed} hpAtReach={d.HpAtReach} tHpAtReach={d.TargetHpAtReach} rate={FormatRate(d.Rate)} " +
        $"wouldBreak={d.WouldBreak} walk={FormatWalk(d.Walk)} end={d.Endurance} kiteNone={FormatKiteNone(d.KiteNone ?? (-1, -1, -1, -1))} " +
        $"dots={d.Dots} qcKill={d.KillingQuickcast} ctlMez={d.TargetMezzes} raceLeft={d.RaceLeft} " +
        $"styleHeld={d.StylesHeld} castHeld={d.CastsHeld} heals={d.Heals} healCast={d.HealCasts} " +
        $"healLanded={d.HealsLanded} healHold={d.HealHolds} healHoldMax={d.HealHoldMax} taps={d.Taps} " +
        $"addWhy={(d.AddWhy is { } why ? FormatAddWhy(why) : "-1")} ctlResisted={d.CtlResisted} ctlImmune={d.CtlImmune} " +
        $"qcCtl={d.QcControls} qcNuke={d.QcNukes} qcLeft={d.QcLeft} crowd={d.Crowd} over={d.Over} due={d.Due} " +
        $"seen={d.Seen ?? "-"} pathCrowd={d.PathCrowd} huntCrowd={d.HuntCrowd} huntTarget=\"{d.HuntTarget}\" " +
        $"blindSteps={d.BlindSteps} restSkip={d.RestSkip} capHold={d.CapHold} holdStep={d.HoldSteps}");

    /// <summary>#188: PLAYER_BOT_CROSSING win=, the window's changes of island over all bots, then its ring points
    /// dropped: the totals (a bot's own CROSSING bot= lines are samples, at most one a minute;
    /// PlayerBotPathMover.ReportCrossing).</summary>
    public static string FormatCrossing(PlayerBotCrossingWindow w) => string.Create(CultureInfo.InvariantCulture,
        $"PLAYER_BOT_CROSSING win={w.Seconds} took={w.Took} ground={w.Ground} refused={w.Refused} dropped={w.Dropped}");

    public static string Format(PlayerBotDriverWindow w)
    {
        CultureInfo c = CultureInfo.InvariantCulture;
        double botSeconds = (double)w.Bots * w.Seconds;
        string PerBot(double value, string format) => w.Bots == 0 ? "-" : (value / botSeconds).ToString(format, c);
        string Mean(double value) => w.Bots == 0 ? "-" : value.ToString("0.0", c);
        return string.Create(c,
            $"PLAYER_BOT_DRIVER win={w.Seconds} bots={w.Bots} ticksPerBotSec={PerBot(w.Ticks, "0.0")} " +
            $"thinkMsPerBotSec={PerBot(w.TickMs - w.MoverMs, "0.000")} moverMsPerBotSec={PerBot(w.MoverMs, "0.000")} " +
            $"waiting={w.Waiting} dead={w.Dead} resting={w.Resting} hunting={w.Hunting} fighting={w.Fighting} casting={w.Casting} " +
            $"roaming={w.Roaming} task={w.Task} kills={w.Kills} deaths={w.Deaths} levelUps={w.LevelUps} maxLevel={w.MaxLevel} " +
            $"xp={w.Experience} blocked={w.Blocked} stuck={w.Stuck} ldCancelled={w.LinkDeathsCancelled} errors={w.Errors} " +
            $"nearPlayerBots={Mean(w.NearPlayerBots)} nearGamebots={Mean(w.NearGamebots)} " +
            $"gamebotFidelity={w.GamebotNearbyHuman}/{w.GamebotStandard}/{w.GamebotEfficient}");
    }
}
