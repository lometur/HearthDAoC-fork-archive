using System;
using System.Collections.Concurrent;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using DOL.Database;
using DOL.GS.HearthDAoC.PlayerBots;
using DOL.GS.ServerRules;
using DOL.Logging;
using NUnit.Framework;

namespace DOL.GS.Tests;

// HearthDAoC player bots (#160 step 1, its review): PlayerBotSpawner's session rules on its own slots, by reflection
// (they are private and static), with no server and no game loop: what Leave does with each reason under both switch
// values, StartDue's wait for a slot's old client, and what a session carries to the next login (Resume at the login,
// Carry at the driver's stop: the death cap, and the camp unless a death came after it); BeginLogOff's waits and late
// asks, and Observe's asks again.
[TestFixture, NonParallelizable]
public sealed class UT_PlayerBotSpawnerSessions
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private const BindingFlags HiddenStatic = BindingFlags.Static | BindingFlags.NonPublic;
    private static readonly PropertyInfo SessionsProperty =
        typeof(PlayerBotSpawner).GetProperty(nameof(PlayerBotSpawner.Sessions))!;
    private static readonly FieldInfo SlotsField = typeof(PlayerBotSpawner).GetField("_slots", HiddenStatic)!;
    private static readonly FieldInfo StartedField = typeof(PlayerBotSpawner).GetField("_startedUtc", HiddenStatic)!;
    private static readonly PlayerBotSessionSettings On = PlayerBotSessionSettings.Default;

    // As UT_HeadlessClient: GameObject's type initializer reads the database.
    private sealed class EmptyServer : GameServer
    {
        protected override IServerRules ServerRulesImpl => new NormalServerRules();
        protected override IObjectDatabase DataBaseImpl => DispatchProxy.Create<IObjectDatabase, UT_HeadlessClient.EmptyReads>();
    }

    private GameServer _savedServer;

    [OneTimeSetUp]
    public void InitializeLogging() => LoggerManager.InitializeWithExplicitLibrary(null, LogLibrary.None);

    [SetUp]
    public void SaveServer()
    {
        _savedServer = GameServer.Instance;
        GameServer.LoadTestDouble((EmptyServer)RuntimeHelpers.GetUninitializedObject(typeof(EmptyServer)));
    }

    [TearDown]
    public void Restore()
    {
        SessionsProperty.SetValue(null, PlayerBotSessionSettings.Off);
        SlotsField.SetValue(null, Array.Empty<PlayerBotSpawner.SlotState>());
        DropPosted(NpcService.Instance);                     // the StartOnGameLoop posts: no game loop runs them here
        GameServer.LoadTestDouble(_savedServer);
    }

    private static void DropPosted(GameServiceBase service)
    {
        object actions = typeof(GameServiceBase).GetField("_actions", Hidden)!.GetValue(service)!;
        actions.GetType().GetMethod("Clear")!.Invoke(actions, null);
    }

    private static int Posted(GameServiceBase service)
    {
        object actions = typeof(GameServiceBase).GetField("_actions", Hidden)!.GetValue(service)!;
        return (int)actions.GetType().GetProperty("Count")!.GetValue(actions)!;
    }

    private static PlayerBotSpawner.SlotState Slot(PlayerBotSpawner.SlotPhase phase) =>
        new(new PlayerBotSlot("hdbot-alb-02", eRealm.Albion, 2), false) { Phase = phase };

    private static HeadlessClient Client(GameClient.eClientState state) =>
        new(new DbAccount { Name = "hdbot-alb-02", Language = "EN", PrivLevel = (int)ePrivLevel.Player }) { ClientState = state };

    private static void Call(string method, params object[] args) =>
        typeof(PlayerBotSpawner).GetMethod(method, HiddenStatic)!.Invoke(null, args);

    private static PlayerBotSpawner.SlotState Leave(PlayerBotSessionSettings sessions, string reason, DateTime now,
        int dropsBefore = 0)
    {
        SessionsProperty.SetValue(null, sessions);
        PlayerBotSpawner.SlotState s = Slot(PlayerBotSpawner.SlotPhase.Playing);
        s.Drops = dropsBefore;
        Call("Leave", s, now, reason);
        return s;
    }

    // F1 stays visible with sessions off: whatever took the bot out, it is Lost, never logged in again, drops
    // uncounted.
    [TestCase("error")] [TestCase("reaped")] [TestCase("none")] [TestCase("kicked")] [TestCase("quit")]
    [TestCase("logoff-forced")]
    public void Sessions_off_a_bot_that_leaves_for_any_reason_is_lost(string reason)
    {
        PlayerBotSpawner.SlotState s = Leave(PlayerBotSessionSettings.Off, reason, DateTime.UtcNow);
        Assert.That((s.Phase, s.Drops), Is.EqualTo((PlayerBotSpawner.SlotPhase.Lost, 0)));
    }

    [TestCase("error")] [TestCase("reaped")] [TestCase("none")] [TestCase("kicked")]
    public void Sessions_on_a_drop_logs_in_again_after_relog_seconds(string reason)
    {
        DateTime now = DateTime.UtcNow;
        PlayerBotSpawner.SlotState s = Leave(On, reason, now);
        Assert.That((s.Phase, s.RetryAtUtc, s.Drops, s.LeftHow),
            Is.EqualTo((PlayerBotSpawner.SlotPhase.Pending, now.AddSeconds(On.RelogSeconds), 1, reason)));
    }

    [Test]
    public void Sessions_on_the_sixth_drop_is_lost()
    {
        PlayerBotSpawner.SlotState s = Leave(On, "error", DateTime.UtcNow, dropsBefore: PlayerBotSessions.MaxRelogs);
        Assert.That((s.Phase, s.Drops), Is.EqualTo((PlayerBotSpawner.SlotPhase.Lost, PlayerBotSessions.MaxRelogs + 1)));
    }

    [TestCase("quit")] [TestCase("logoff-forced")]
    public void Sessions_on_its_own_log_off_goes_offline_for_its_gap(string reason)
    {
        DateTime now = DateTime.UtcNow;
        PlayerBotSpawner.SlotState s = Leave(On, reason, now, dropsBefore: PlayerBotSessions.MaxRelogs);
        Assert.That((s.Phase, s.Drops), Is.EqualTo((PlayerBotSpawner.SlotPhase.Offline, PlayerBotSessions.MaxRelogs)));
        Assert.That(s.RetryAtUtc, Is.InRange(now.AddMinutes(On.OfflineMinMinutes), now.AddMinutes(On.OfflineMaxMinutes)));
    }

    [Test]
    public void Sessions_on_a_gap_of_zero_still_waits_five_seconds() =>
        Assert.That(Leave(On with { OfflineMinMinutes = 0, OfflineMaxMinutes = 0 }, "quit", new DateTime(2026, 11, 3))
            .RetryAtUtc, Is.EqualTo(new DateTime(2026, 11, 3).AddSeconds(5)));

    // A slot's next login waits until its old client is Disconnected (its quit has run); then it starts, and Loading
    // reads the new client (s.Client null until StartOnGameLoop sets it).
    [TestCase(PlayerBotSpawner.SlotPhase.Pending)] [TestCase(PlayerBotSpawner.SlotPhase.Offline)]
    public void A_slot_logs_in_again_only_once_its_old_client_is_disconnected(PlayerBotSpawner.SlotPhase phase)
    {
        DateTime now = DateTime.UtcNow;
        SessionsProperty.SetValue(null, On);
        StartedField.SetValue(null, now.AddHours(-1));
        PlayerBotSpawner.SlotState s = Slot(phase);
        (s.Started, s.RetryAtUtc, s.Client) = (true, now.AddSeconds(-1), Client(GameClient.eClientState.CharScreen));
        SlotsField.SetValue(null, new[] { s });
        int posted = Posted(NpcService.Instance);

        Call("StartDue", now);
        Assert.That((s.Phase, Posted(NpcService.Instance)), Is.EqualTo((phase, posted)), "its old client still quits");

        s.Client.ClientState = GameClient.eClientState.Disconnected;
        Call("StartDue", now);
        Assert.That((s.Phase, s.Client, Posted(NpcService.Instance)),
            Is.EqualTo((PlayerBotSpawner.SlotPhase.Loading, (HeadlessClient)null, posted + 1)));
    }

    private static readonly ConcurrentDictionary<GamePlayer, PlayerBotState> States =
        (ConcurrentDictionary<GamePlayer, PlayerBotState>)typeof(PlayerBotDriver).GetField("_states", HiddenStatic)!.GetValue(null)!;

    // A session's driver, as Drive starts it (Resume) for a player made without its constructor (region 0), registered
    // as PlayerBotDriver.Start registers it; End stops it as its logout does (PlayerBotDriver.Stop, which runs Carry).
    private static PlayerBotState Session(PlayerBotSpawner.SlotState s)
    {
        GamePlayer player = (GamePlayer)RuntimeHelpers.GetUninitializedObject(typeof(GamePlayer));
        PlayerBotState state = new(player);
        Call("Resume", s, state, player);
        States[player] = state;
        return state;
    }

    private static void End(PlayerBotState state)
    {
        state.Player.ObjectState = GameObject.eObjectState.Deleted;          // the quit took it out of the world
        PlayerBotDriver.Stop(state.Player);
    }

    private static readonly Vector3 CampA = new(1_000, 2_000, 30), CampB = new(5_000, 6_000, 70);

    // The death cap never goes down in a run (a GameBot's resets only when its controller is made, once a run): each
    // session's driver starts at the cap the last one stopped at.
    [Test]
    public void The_death_cap_carries_over_to_the_next_session()
    {
        SessionsProperty.SetValue(null, On);
        PlayerBotSpawner.SlotState s = Slot(PlayerBotSpawner.SlotPhase.Playing);
        PlayerBotState first = Session(s);
        first.DeathSafetySteps = PlayerBotDriver.MaxDeathSteps;
        End(first);
        PlayerBotState second = Session(s);
        Assert.That((s.DeathSteps, second.DeathSafetySteps), Is.EqualTo((PlayerBotDriver.MaxDeathSteps, PlayerBotDriver.MaxDeathSteps)));
        End(second);
        Assert.That(s.DeathSteps, Is.EqualTo(PlayerBotDriver.MaxDeathSteps));
    }

    // The camp: the anchor as the driver stopped, used once, at a login in its region; not one the bot died at (a
    // replan owed), nor after a death on its log-off (deaths past DeathsAtLogOff with the log-off task running).
    [Test]
    public void A_session_keeps_its_camp_for_the_next_login_in_its_region_once()
    {
        SessionsProperty.SetValue(null, On);
        PlayerBotSpawner.SlotState s = Slot(PlayerBotSpawner.SlotPhase.Playing);
        PlayerBotState first = Session(s);
        (first.AnchorRegion, first.Anchor) = (0, CampA);
        End(first);
        Assert.That(s.Camp, Is.EqualTo(new PlayerBotCamp(0, CampA)));
        PlayerBotState second = Session(s);
        Assert.That((second.AnchorRegion, second.Anchor, s.Camp), Is.EqualTo(((ushort)0, CampA, (PlayerBotCamp)null)));

        s.Camp = new PlayerBotCamp(5, CampB);                                // another region's: not used, and gone
        PlayerBotState third = Session(s);
        Assert.That((third.Anchor == CampB, s.Camp), Is.EqualTo((false, (PlayerBotCamp)null)));
    }

    [Test]
    public void A_camp_the_bot_died_at_or_died_after_logging_off_from_is_not_kept()
    {
        SessionsProperty.SetValue(null, On);
        PlayerBotSpawner.SlotState s = Slot(PlayerBotSpawner.SlotPhase.Playing);
        PlayerBotState owes = Session(s);
        (owes.Anchor, owes.ReplanOwed) = (CampA, PlayerBotDriver.Died);
        End(owes);
        Assert.That(s.Camp, Is.Null, "died at it, no replan yet");

        PlayerBotState logOff = Session(s);
        (logOff.Anchor, logOff.Deaths, s.DeathsAtLogOff) = (CampB, 2, 2);
        logOff.Task = new PlayerBotLogOffTask(null, 0);
        logOff.Deaths++;                                                       // killed on the way out, released, quit
        End(logOff);
        Assert.That(s.Camp, Is.Null, "a death on its log-off");

        PlayerBotState clean = Session(s);
        (clean.Anchor, clean.Deaths, s.DeathsAtLogOff) = (CampB, 2, 2);
        clean.Task = new PlayerBotLogOffTask(null, 0);
        End(clean);
        Assert.That(s.Camp, Is.EqualTo(new PlayerBotCamp(0, CampB)));
    }

    // A bot in the world, driven: its client Playing, its player Active, its driver state registered (no timer).
    private static (PlayerBotSpawner.SlotState Slot, PlayerBotState State) Driven()
    {
        HeadlessClient client = Client(GameClient.eClientState.Playing);
        GamePlayer player = (GamePlayer)RuntimeHelpers.GetUninitializedObject(typeof(GamePlayer));
        typeof(GamePlayer).GetField("m_client", Hidden)!.SetValue(player, client);
        client.Player = player;
        PlayerBotState state = new(player);
        States[player] = state;
        PlayerBotSpawner.SlotState s = Slot(PlayerBotSpawner.SlotPhase.LoggingOff);
        s.Client = client;
        return (s, state);
    }

    private sealed class Errands : IPlayerBotTask
    {
        public bool Finished { get; set; }
        public bool Tick(PlayerBotState state, long now) => true;
    }

    [Test]
    public void The_log_off_is_given_to_a_free_driver_with_its_deaths_so_far()
    {
        SessionsProperty.SetValue(null, On);
        (PlayerBotSpawner.SlotState s, PlayerBotState state) = Driven();
        state.Deaths = 3;
        Call("BeginLogOff", s);
        Assert.That((state.Task is PlayerBotLogOffTask, s.LogOffStarted, s.DeathsAtLogOff, s.LogOffWaits), Is.EqualTo((true, true, 3, 0)));
        States.TryRemove(state.Player, out _);
    }

    // The review of step 1: a late ask (posted again before the first ran) finds its own task: started, nothing
    // replaced.
    [Test]
    public void A_late_ask_finds_its_own_log_off_and_marks_it_started()
    {
        SessionsProperty.SetValue(null, On);
        (PlayerBotSpawner.SlotState s, PlayerBotState state) = Driven();
        PlayerBotLogOffTask given = new(s.Client, 0);
        state.Task = given;
        Call("BeginLogOff", s);
        Assert.That((state.Task, s.LogOffStarted, s.LogOffWaits), Is.EqualTo(((IPlayerBotTask)given, true, 0)));
        States.TryRemove(state.Player, out _);
    }

    // It waits, counted (a waiting line once a minute), for the errands to finish and then for their re-login to go
    // first.
    [Test]
    public void The_log_off_waits_for_the_errands_and_their_relog()
    {
        SessionsProperty.SetValue(null, On);
        (PlayerBotSpawner.SlotState s, PlayerBotState state) = Driven();
        Errands errands = new();
        state.Task = errands;
        Call("BeginLogOff", s);
        errands.Finished = true;
        s.RelogRequested = true;
        Call("BeginLogOff", s);
        Assert.That((state.Task, s.LogOffStarted, s.LogOffWaits), Is.EqualTo(((IPlayerBotTask)errands, false, 2)));
        States.TryRemove(state.Player, out _);
    }

    [Test]
    public void Without_a_driver_the_log_off_waits()
    {
        SessionsProperty.SetValue(null, On);
        PlayerBotSpawner.SlotState s = Slot(PlayerBotSpawner.SlotPhase.LoggingOff);
        s.Client = Client(GameClient.eClientState.Playing);
        Call("BeginLogOff", s);
        Assert.That((s.LogOffStarted, s.LogOffWaits), Is.EqualTo((false, 1)));
    }

    // A log-off the driver couldn't take is asked again every LogOffRetrySeconds; askedS counts from the first ask.
    [Test]
    public void A_log_off_asked_again_keeps_its_first_ask()
    {
        DateTime now = DateTime.UtcNow;
        SessionsProperty.SetValue(null, On);
        PlayerBotSpawner.SlotState s = Slot(PlayerBotSpawner.SlotPhase.Playing);
        (s.Client, s.SessionStartUtc, s.SessionEndsUtc) = (Client(GameClient.eClientState.Playing), now.AddMinutes(-5), now);
        SlotsField.SetValue(null, new[] { s });
        Call("Observe", now);
        Assert.That((s.Phase, s.LogOffAskedUtc), Is.EqualTo((PlayerBotSpawner.SlotPhase.LoggingOff, now)));
        Call("Observe", now.AddSeconds(PlayerBotSpawner.LogOffRetrySeconds));
        Assert.That(s.Phase, Is.EqualTo(PlayerBotSpawner.SlotPhase.Playing), "not taken: asked again");
        Call("Observe", now.AddSeconds(PlayerBotSpawner.LogOffRetrySeconds + 1));
        Assert.That((s.Phase, s.LogOffAskedUtc, s.LogOffPostedUtc), Is.EqualTo((PlayerBotSpawner.SlotPhase.LoggingOff, now,
            now.AddSeconds(PlayerBotSpawner.LogOffRetrySeconds + 1))));
    }

    // The review of step 1: a slot out of the world keeps no client and no player (no offline progress, no memory
    // held): once its old client is Disconnected, Observe drops it.
    [TestCase(PlayerBotSpawner.SlotPhase.Offline)] [TestCase(PlayerBotSpawner.SlotPhase.Pending)]
    public void A_slot_out_of_the_world_drops_its_old_client_once_it_is_disconnected(PlayerBotSpawner.SlotPhase phase)
    {
        SessionsProperty.SetValue(null, On);
        PlayerBotSpawner.SlotState s = Slot(phase);
        s.Client = Client(GameClient.eClientState.CharScreen);
        s.RetryAtUtc = DateTime.MaxValue;
        SlotsField.SetValue(null, new[] { s });
        Call("Observe", DateTime.UtcNow);
        Assert.That(s.Client, Is.Not.Null, "still quitting");
        s.Client.ClientState = GameClient.eClientState.Disconnected;
        Call("Observe", DateTime.UtcNow);
        Assert.That((s.Phase, s.Client), Is.EqualTo((phase, (HeadlessClient)null)));
    }

    // The ramp holds first logins only: a slot that started before logs in again whatever the room.
    [Test]
    public void The_ramp_holds_first_logins_only()
    {
        DateTime now = DateTime.UtcNow;
        SessionsProperty.SetValue(null, On);
        StartedField.SetValue(null, now);                    // no room yet: DesiredActiveCount is 0 at the start
        PlayerBotSpawner.SlotState first = Slot(PlayerBotSpawner.SlotPhase.Pending), again = Slot(PlayerBotSpawner.SlotPhase.Offline);
        again.Started = true;
        SlotsField.SetValue(null, new[] { first, again });
        Call("StartDue", now);
        Assert.That((first.Phase, again.Phase), Is.EqualTo((PlayerBotSpawner.SlotPhase.Pending, PlayerBotSpawner.SlotPhase.Loading)));
    }
}
