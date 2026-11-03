using System;
using System.Collections.Concurrent;
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
// values, and StartDue's wait for a slot's old client.
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

    // F1 stays visible with sessions off: whatever took the bot out, it is Lost, never logged in again, drops uncounted.
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
