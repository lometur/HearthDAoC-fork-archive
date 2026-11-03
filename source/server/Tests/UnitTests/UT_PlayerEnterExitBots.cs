using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using DOL.Database;
using DOL.GS.GameEvents;
using DOL.GS.HearthDAoC.PlayerBots;
using DOL.GS.ServerRules;
using NUnit.Framework;

namespace DOL.GS.Tests;

// HearthDAoC (#160; the owner, 2026-11-03: "the bots should only do local messaging", "'/region' is fine"): with play
// sessions hundreds of player bots log in and out all day, and PlayerEnterExit told every player online of each. A
// player bot's log-in and log-out now go to the players its /region reaches (RegionCommandHandler.Broadcast: the
// players of its region whom the server rules let understand it), never to itself; a human's go to every player
// online as before; show_logins off or an anonymous player tells nobody. PlayerEnterExit.Audience picks them from the
// players online, and both handlers (GameEntered, Quit) send to it alone.
[TestFixture, NonParallelizable]
public sealed class UT_PlayerEnterExitBots
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;

    // As UT_HeadlessClient: GameObject's type initializer reads the database. NormalServerRules is the server's own
    // (the world runs GST_Normal): a player of another realm doesn't understand, staff understand anyone.
    private sealed class EmptyServer : GameServer
    {
        protected override IServerRules ServerRulesImpl => new NormalServerRules();
        protected override IObjectDatabase DataBaseImpl =>
            DispatchProxy.Create<IObjectDatabase, UT_HeadlessClient.EmptyReads>();
    }

    private GameServer _savedServer;
    private bool _savedShowLogins;

    [SetUp]
    public void SaveServer()
    {
        _savedServer = GameServer.Instance;
        GameServer.LoadTestDouble((EmptyServer)RuntimeHelpers.GetUninitializedObject(typeof(EmptyServer)));
        _savedShowLogins = ServerProperties.Properties.SHOW_LOGINS;
        ServerProperties.Properties.SHOW_LOGINS = true; // its default (ServerProperties.cs:575); no test loads it
    }

    [TearDown]
    public void RestoreServer()
    {
        ServerProperties.Properties.SHOW_LOGINS = _savedShowLogins;
        GameServer.LoadTestDouble(_savedServer);
    }

    private static readonly Region AlbionMainland = RegionOf(1), Camelot = RegionOf(10);

    private static Region RegionOf(ushort id)
    {
        Region region = (Region)RuntimeHelpers.GetUninitializedObject(typeof(Region));
        typeof(Region).GetField("m_regionData", Hidden)!.SetValue(region, new RegionData { Id = id });
        return region;
    }

    // A player online: a bot is one by its account's name (PlayerBotPlan.IsBotAccount), whatever its client.
    private static GamePlayer Player(string account, eRealm realm, Region region,
        ePrivLevel privLevel = ePrivLevel.Player)
    {
        HeadlessClient client = new(new DbAccount { Name = account, Language = "EN", PrivLevel = (uint)privLevel });
        GamePlayer player = (GamePlayer)RuntimeHelpers.GetUninitializedObject(typeof(GamePlayer));
        typeof(GamePlayer).GetField("m_client", Hidden)!.SetValue(player, client);
        player.Realm = realm;
        player.CurrentRegion = region;
        client.Player = player;
        return player;
    }

    // The account names of who is told, in the order online lists them (an uninitialized GamePlayer has no ToString).
    private static string[] Audience(GamePlayer player, params GamePlayer[] online) =>
        PlayerEnterExit.Audience(player, online).Select(listener => listener.Client.Account.Name).ToArray();

    [Test]
    public void A_bots_log_in_and_out_go_to_its_regions_players_who_understand_it_as_its_region_chat_would()
    {
        GamePlayer bot = Player(PlayerBotPlan.AccountName(eRealm.Albion, 1), eRealm.Albion, AlbionMainland);
        GamePlayer near = Player("alb-near", eRealm.Albion, AlbionMainland);
        GamePlayer otherBot = Player(PlayerBotPlan.AccountName(eRealm.Albion, 2), eRealm.Albion, AlbionMainland);
        GamePlayer inCamelot = Player("alb-camelot", eRealm.Albion, Camelot);
        GamePlayer enemy = Player("mid-raider", eRealm.Midgard, AlbionMainland);  // NormalServerRules: can't understand
        GamePlayer gm = Player("mid-gm", eRealm.Midgard, AlbionMainland, ePrivLevel.GM); // staff understand anyone

        Assert.That(Audience(bot, bot, near, otherBot, inCamelot, enemy, gm),
            Is.EqualTo(new[] { "alb-near", "hdbot-alb-02", "mid-gm" }));
    }

    [Test]
    public void A_humans_log_in_and_out_go_to_every_player_online_but_itself_as_before()
    {
        GamePlayer human = Player("alb-human", eRealm.Albion, AlbionMainland);
        GamePlayer bot = Player(PlayerBotPlan.AccountName(eRealm.Albion, 1), eRealm.Albion, AlbionMainland);
        GamePlayer inCamelot = Player("alb-camelot", eRealm.Albion, Camelot);
        GamePlayer enemy = Player("mid-human", eRealm.Midgard, RegionOf(100));
        GamePlayer nowhere = Player("hib-nowhere", eRealm.Hibernia, null);

        Assert.That(Audience(human, bot, human, inCamelot, enemy, nowhere),
            Is.EqualTo(new[] { "hdbot-alb-01", "alb-camelot", "mid-human", "hib-nowhere" }));
    }

    [Test]
    public void With_show_logins_off_nobody_is_told()
    {
        GamePlayer human = Player("alb-human", eRealm.Albion, AlbionMainland);
        GamePlayer bot = Player(PlayerBotPlan.AccountName(eRealm.Albion, 1), eRealm.Albion, AlbionMainland);
        GamePlayer near = Player("alb-near", eRealm.Albion, AlbionMainland);
        ServerProperties.Properties.SHOW_LOGINS = false;

        Assert.That(Audience(human, human, bot, near).Concat(Audience(bot, human, bot, near)), Is.Empty);
    }

    // A bot out of every region (no CurrentRegion) has no /region audience; nor is one of no region its neighbour.
    [Test]
    public void A_bot_with_no_region_tells_nobody()
    {
        GamePlayer bot = Player(PlayerBotPlan.AccountName(eRealm.Albion, 1), eRealm.Albion, null);
        GamePlayer near = Player("alb-near", eRealm.Albion, AlbionMainland);
        GamePlayer nowhere = Player("alb-nowhere", eRealm.Albion, null);

        Assert.That(Audience(bot, bot, near, nowhere), Is.Empty);
    }

    [Test]
    public void An_anonymous_player_tells_nobody()
    {
        GamePlayer human = Player("alb-human", eRealm.Albion, AlbionMainland);
        GamePlayer bot = Player(PlayerBotPlan.AccountName(eRealm.Albion, 1), eRealm.Albion, AlbionMainland);
        GamePlayer near = Player("alb-near", eRealm.Albion, AlbionMainland);
        foreach (GamePlayer player in new[] { human, bot })
            typeof(GamePlayer).GetField("m_dbCharacter", Hidden)!.SetValue(player,
                new DbCoreCharacter { IsAnonymous = true, Realm = (int)eRealm.Albion, Region = 1 });

        Assert.That(Audience(human, human, bot, near).Concat(Audience(bot, human, bot, near)), Is.Empty);
    }
}
