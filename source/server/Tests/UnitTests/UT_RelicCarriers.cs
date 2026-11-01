using System.Reflection;
using System.Runtime.CompilerServices;
using DOL.Database;
using DOL.GS;
using NUnit.Framework;

namespace DOL.UnitTests
{
    /// <summary>
    /// #189 (owner's live test, 2026-10-31): no player could take a relic. Upstream 0.34's pickup accepted only
    /// IGamePlayer, which only GameBot implements, and refused everyone else without a message. A companion that took
    /// one could never set it on a pad.
    /// </summary>
    [TestFixture, NonParallelizable]
    public class UT_RelicCarriers
    {
        private GameServer _oldServer;
        private sealed class EmptyServer : GameServer
        {
            protected override IObjectDatabase DataBaseImpl =>
                DispatchProxy.Create<IObjectDatabase, UT_UnobservedConcentration.EmptyReads>();
        }

        [SetUp]
        public void SetUp()
        {
            _oldServer = GameServer.Instance;
            GameServer.LoadTestDouble((EmptyServer)RuntimeHelpers.GetUninitializedObject(typeof(EmptyServer)));
        }

        [TearDown]
        public void TearDown() => GameServer.LoadTestDouble(_oldServer);

        private static T Blank<T>() => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));

        [Test]
        public void PlayersAndBotsCanCarryARelic()
        {
            Assert.That(GameRelic.CanCarry(Blank<GamePlayer>()), Is.True);
            Assert.That(GameRelic.CanCarry(Blank<GameBot>()), Is.True);
            Assert.That(GameRelic.CanCarry(Blank<GameNPC>()), Is.False);
            Assert.That(GameRelic.CanCarry(null), Is.False);
        }

        [Test]
        public void APlayersRelicGoesInItsOwnInventory()
        {
            GamePlayer player = Blank<GamePlayer>();
            GamePlayerInventory inventory = Blank<GamePlayerInventory>();
            player.Inventory = inventory;
            Assert.That(GameRelic.InventoryOf(player), Is.SameAs(inventory));
            Assert.That(GameRelic.InventoryOf(Blank<GameNPC>()), Is.Null);
        }

        [Test]
        public void ACarrierOutOfTheWorldKeepsTheRelicForAMinute()
        {
            // A player is out of the world while the client loads a new region (portal keep, zone line); 1.60 gave
            // relics up to 60 seconds to cross. Dead or stealthed still drops at once.
            Assert.That(GameRelic.DropsForAbsence(alive: true, active: true, stealthed: false, awayMs: 0), Is.False);
            Assert.That(GameRelic.DropsForAbsence(alive: true, active: false, stealthed: false, awayMs: 4_000), Is.False);
            Assert.That(GameRelic.DropsForAbsence(alive: true, active: false, stealthed: false, awayMs: 59_999), Is.False);
            Assert.That(GameRelic.DropsForAbsence(alive: true, active: false, stealthed: false, awayMs: 60_000), Is.True);
            Assert.That(GameRelic.DropsForAbsence(alive: false, active: true, stealthed: false, awayMs: 0), Is.True);
            Assert.That(GameRelic.DropsForAbsence(alive: true, active: true, stealthed: true, awayMs: 0), Is.True);
            Assert.That(GameRelic.DropsForAbsence(alive: false, active: false, stealthed: false, awayMs: 0), Is.True);
        }

        [Test]
        public void ABotCarrierSetsTheRelicOnlyOnItsOwnRealmsPadOfTheType()
        {
            Assert.That(GameRelic.IsHomePad(eRealm.Albion, eRelicType.Strength, eRealm.Albion, eRelicType.Strength), Is.True);
            Assert.That(GameRelic.IsHomePad(eRealm.Albion, eRelicType.Strength, eRealm.Albion, eRelicType.Magic), Is.False);
            Assert.That(GameRelic.IsHomePad(eRealm.Albion, eRelicType.Strength, eRealm.Midgard, eRelicType.Strength), Is.False);
            Assert.That(GameRelic.IsHomePad(eRealm.None, eRelicType.Strength, eRealm.None, eRelicType.Strength), Is.False);
        }
    }
}
