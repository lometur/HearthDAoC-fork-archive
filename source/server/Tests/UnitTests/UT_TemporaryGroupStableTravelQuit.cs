using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using DOL.Database;
using DOL.Events;
using DOL.GS.HearthDAoC.PlayerBots;
using DOL.GS.ServerRules;
using NUnit.Framework;

namespace DOL.GS.Tests;

// HearthDAoC (#160 step 1's review; the owner approved this change outside the player-bot code, 2026-11-03): every
// GamePlayer.MoveTo marks the player in TemporaryGroupStableTravel.PendingPlayerTransfers (GamePlayer.cs:8000-8001),
// and for a player with no group nothing took the marker away: each player who teleported once (a release, a stuck
// bot's step back, a player bot's session) stayed reachable until the server stopped. A quit now drops the player's
// marker and route (GamePlayerEvent.Quit, which every quit raises: GamePlayer.Quit(true), GamePlayer.cs:1102).
[TestFixture, NonParallelizable]
public sealed class UT_TemporaryGroupStableTravelQuit
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private const BindingFlags HiddenStatic = BindingFlags.Static | BindingFlags.NonPublic;

    // As UT_HeadlessClient: GameObject's type initializer reads the database.
    private sealed class EmptyServer : GameServer
    {
        protected override IServerRules ServerRulesImpl => new NormalServerRules();
        protected override IObjectDatabase DataBaseImpl => DispatchProxy.Create<IObjectDatabase, UT_HeadlessClient.EmptyReads>();
    }

    private GameServer _savedServer;

    [SetUp]
    public void SaveServer()
    {
        _savedServer = GameServer.Instance;
        GameServer.LoadTestDouble((EmptyServer)RuntimeHelpers.GetUninitializedObject(typeof(EmptyServer)));
        Pending().Clear();
    }

    [TearDown]
    public void RestoreServer()
    {
        Pending().Clear();
        GameServer.LoadTestDouble(_savedServer);
    }

    private static ConcurrentDictionary<GamePlayer, DateTime> Pending() =>
        (ConcurrentDictionary<GamePlayer, DateTime>)typeof(TemporaryGroupStableTravel)
            .GetField("PendingPlayerTransfers", HiddenStatic)!.GetValue(null)!;

    // A live player with no group (as every player bot is), as its MoveTo leaves it (GamePlayer.cs:8000-8001), then
    // quit: the quit's event, then Delete. Made in its own frame, so only the marker can keep it.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference TeleportThenQuit()
    {
        HeadlessClient client = new(new DbAccount { Name = "hdbot-quit", Language = "EN", PrivLevel = (int)ePrivLevel.Player });
        GamePlayer player = (GamePlayer)RuntimeHelpers.GetUninitializedObject(typeof(GamePlayer));
        typeof(GamePlayer).GetField("m_client", Hidden)!.SetValue(player, client);
        typeof(GameLiving).GetField("<TempProperties>k__BackingField", Hidden)!.SetValue(player, new PropertyCollection());
        typeof(GameObject).GetField("m_health", Hidden)!.SetValue(player, 100);
        player.ObjectState = GameObject.eObjectState.Active;

        TemporaryGroupStableTravel.MarkAcceptedPlayerTransfer(player);
        Assert.That(TemporaryGroupStableTravel.RelocateHelpersAfterPlayerTransfer(player, explicitMoveTo: true), Is.Zero);
        Assert.That(Pending().ContainsKey(player), Is.True, "a groupless player's marker outlives its MoveTo");

        GameEventMgr.Notify(GamePlayerEvent.Quit, player);                   // GamePlayer.Quit(true), :1102
        player.ObjectState = GameObject.eObjectState.Deleted;
        client.Player = null;
        return new WeakReference(player);
    }

    [Test]
    public void A_quit_drops_the_players_transfer_marker_so_nothing_keeps_it()
    {
        TemporaryGroupStableTravel.OnServerStarted(null, null, EventArgs.Empty);
        try
        {
            WeakReference session = TeleportThenQuit();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Assert.That((Pending().Count, session.IsAlive), Is.EqualTo((0, false)));
        }
        finally
        {
            TemporaryGroupStableTravel.OnServerStopped(null, null, EventArgs.Empty);
        }
    }

    // The handler goes with the server: after the stop, a quit leaves the (cleared, then new) markers alone.
    [Test]
    public void The_quit_handler_is_removed_when_the_server_stops()
    {
        TemporaryGroupStableTravel.OnServerStarted(null, null, EventArgs.Empty);
        TemporaryGroupStableTravel.OnServerStopped(null, null, EventArgs.Empty);
        GamePlayer player = (GamePlayer)RuntimeHelpers.GetUninitializedObject(typeof(GamePlayer));
        TemporaryGroupStableTravel.MarkAcceptedPlayerTransfer(player);
        GameEventMgr.Notify(GamePlayerEvent.Quit, player);
        Assert.That(Pending().ContainsKey(player), Is.True);
    }
}
