using System;
using System.Linq;
using DOL.GS.HearthDAoC.PlayerBots;
using NUnit.Framework;

namespace DOL.GS.Tests;

// HearthDAoC player bots (#160 step 1): the HEARTHDAOC_PLAYER_BOT_SESSIONS switch (PlayerBotSessionSettings), the
// spawner's session rules (PlayerBotSessions: the ramp, what a leave leads to, the dice) and the log-off's steps
// (PlayerBotLogOffPlan), with no server.
[TestFixture]
public sealed class UT_PlayerBotSessions
{
    private static readonly PlayerBotSessionSettings On = PlayerBotSessionSettings.Default;

    [TestCase(null)] [TestCase("")] [TestCase("  ")] [TestCase("off")] [TestCase("OFF")]
    public void Off_values_turn_it_off(string text)
    {
        PlayerBotSessionSettings settings = PlayerBotSessionSettings.Parse(text);
        Assert.That(settings, Is.EqualTo(PlayerBotSessionSettings.Off));
        Assert.That(settings.Describe(), Is.EqualTo("off"));
    }

    [Test]
    public void On_alone_is_the_gamebots_ramp_and_sessions_of_one_to_four_hours()
    {
        PlayerBotSessionSettings settings = PlayerBotSessionSettings.Parse(" On ");
        Assert.That(settings, Is.EqualTo(new PlayerBotSessionSettings(true, 15, 60, 240, 30, 120, 60)));
        Assert.That(settings.Describe(), Is.EqualTo("on ramp=15 session=60-240 offline=30-120 relog=60"));
    }

    [TestCase("on,ramp=2,session=3-6,offline=1-2,relog=30", 2, 3, 6, 1, 2, 30)]   // the leg's short shape
    [TestCase("ON , Ramp=0 , session=5", 0, 5, 5, 30, 120, 60)]
    [TestCase("on,offline=0", 15, 60, 240, 0, 0, 60)]
    [TestCase("on,ramp=60,session=1440,offline=10080,relog=3600", 60, 1440, 1440, 10080, 10080, 3600)]
    public void Keys_set_their_own_values_and_leave_the_rest(string text, int ramp, int sMin, int sMax, int oMin, int oMax, int relog) =>
        Assert.That(PlayerBotSessionSettings.Parse(text), Is.EqualTo(new PlayerBotSessionSettings(true, ramp, sMin, sMax, oMin, oMax, relog)));

    [TestCase("yes")] [TestCase("1")] [TestCase("on,")] [TestCase("on,ramp")] [TestCase("on,ramp=61")] [TestCase("on,ramp=+1")]
    [TestCase("on,session=6-3")] [TestCase("on,session=0-3")] [TestCase("on,session=1-2-3")] [TestCase("on,session=3-")]
    [TestCase("on,offline=-1")] [TestCase("on,relog=4")] [TestCase("on,relog=3601")] [TestCase("on,ramp=1,ramp=2")]
    [TestCase("on,foo=1")] [TestCase("off,ramp=1")]
    public void Anything_else_is_refused_naming_the_variable(string text)
    {
        var e = Assert.Throws<FormatException>(() => PlayerBotSessionSettings.Parse(text));
        Assert.That(e.Message, Does.StartWith("HEARTHDAOC_PLAYER_BOT_SESSIONS must be off, on, or on with any of ramp=MINUTES"));
        Assert.That(e.Message, Does.EndWith($"got '{text}'"));
    }

    [Test]
    public void Off_leaves_first_logins_to_the_pacing_alone() =>
        Assert.That(PlayerBotSessions.FirstLoginRoom(PlayerBotSessionSettings.Off, 390, 0, TimeSpan.Zero), Is.EqualTo(int.MaxValue));

    // AutonomousPopulationRamp.DesiredActiveCount for 390 over 15 minutes: 129 (a third, rounded up) by minute 5,
    // linear before; the other 261 by minute 15. The spawner's first tick is at 5 s.
    [TestCase(0, 0, 0)] [TestCase(5, 0, 2)] [TestCase(60, 0, 25)] [TestCase(300, 0, 129)] [TestCase(600, 0, 259)]
    [TestCase(600, 100, 159)] [TestCase(600, 300, 0)] [TestCase(900, 0, 390)] [TestCase(3600, 390, 0)]
    public void On_first_logins_follow_the_gamebots_ramp(int seconds, int started, int room) =>
        Assert.That(PlayerBotSessions.FirstLoginRoom(On, 390, started, TimeSpan.FromSeconds(seconds)), Is.EqualTo(room));

    [Test]
    public void A_ramp_of_zero_lets_every_first_login_go_at_once() =>
        Assert.That(PlayerBotSessions.FirstLoginRoom(On with { RampMinutes = 0 }, 390, 10, TimeSpan.Zero), Is.EqualTo(380));

    [TestCase("quit")] [TestCase("logoff-forced")] [TestCase("error")] [TestCase("reaped")] [TestCase("kicked")] [TestCase("none")]
    public void Sessions_off_a_bot_that_leaves_stays_lost(string reason) =>
        Assert.That(PlayerBotSessions.AfterLeave(false, reason, 1), Is.EqualTo(PlayerBotLeave.Lost));

    [TestCase("quit", 0, PlayerBotLeave.Offline)] [TestCase("logoff-forced", 0, PlayerBotLeave.Offline)]
    [TestCase("quit", 9, PlayerBotLeave.Offline)]                                   // its own log-off is never a drop
    [TestCase("error", 1, PlayerBotLeave.Relog)] [TestCase("reaped", 5, PlayerBotLeave.Relog)]
    [TestCase("kicked", 6, PlayerBotLeave.Lost)] [TestCase("none", 6, PlayerBotLeave.Lost)]   // a GM's /kick says none
    public void Sessions_on_a_log_off_goes_offline_and_a_drop_comes_back_five_times(string reason, int drops, PlayerBotLeave next) =>
        Assert.That(PlayerBotSessions.AfterLeave(true, reason, drops), Is.EqualTo(next));

    [Test]
    public void A_roll_lies_between_its_minutes_to_the_second()
    {
        var dice = new Random(1);
        TimeSpan[] rolls = Enumerable.Range(0, 2_000).Select(_ => PlayerBotSessions.Roll(3, 6, dice)).ToArray();
        Assert.That((rolls.Min(), rolls.Max()), Is.EqualTo((TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(6))));
        Assert.That(rolls.All(r => r.Ticks % TimeSpan.TicksPerSecond == 0), Is.True);
        Assert.That(PlayerBotSessions.Roll(5, 5, dice), Is.EqualTo(TimeSpan.FromMinutes(5)));
        Assert.That(PlayerBotSessions.Roll(0, 0, dice), Is.EqualTo(TimeSpan.Zero));
    }

    [Test]
    public void Each_slot_rolls_the_same_sessions_every_run()
    {
        PlayerBotSlot alb1 = new("hdbot-alb-01", eRealm.Albion, 1), mid1 = new("hdbot-mid-01", eRealm.Midgard, 1);
        int[] Rolls(PlayerBotSlot slot)
        {
            Random dice = PlayerBotSessions.Dice(slot);
            return Enumerable.Range(0, 5).Select(_ => (int)PlayerBotSessions.Roll(60, 240, dice).TotalSeconds).ToArray();
        }
        Assert.That(Rolls(alb1), Is.EqualTo(Rolls(alb1)));
        Assert.That(Rolls(alb1), Is.Not.EqualTo(Rolls(mid1)));
    }

    private static PlayerBotLogOffStep Next(bool underAttack = false, bool sitting = false, bool quitAsked = false,
        long msSinceQuit = 0, bool playing = false, bool spotChecked = true, bool walking = false, bool moving = false,
        bool givesUp = false, bool seen = false) =>
        PlayerBotLogOffPlan.Next(underAttack, sitting, quitAsked, msSinceQuit, playing, spotChecked, walking, moving, givesUp, seen);

    [Test]
    public void The_fight_comes_first_even_on_the_quit_timer() =>
        Assert.That(Next(underAttack: true, sitting: true, quitAsked: true, playing: true, givesUp: true), Is.EqualTo(PlayerBotLogOffStep.Fight));

    [Test]
    public void A_running_quit_timer_holds_the_bot_seated_even_when_giving_up()
    {
        Assert.That(Next(sitting: true, quitAsked: true, msSinceQuit: 30_000), Is.EqualTo(PlayerBotLogOffStep.Hold));
        Assert.That(Next(sitting: true, quitAsked: true, msSinceQuit: 89_999, givesUp: true), Is.EqualTo(PlayerBotLogOffStep.Hold));
    }

    [Test]
    public void Still_here_after_ninety_seconds_seated_it_asks_again() =>
        Assert.That(Next(sitting: true, quitAsked: true, msSinceQuit: 90_000), Is.EqualTo(PlayerBotLogOffStep.Quit));

    [Test]
    public void Given_up_it_logs_out_only_where_no_human_sees_it()
    {
        Assert.That(Next(givesUp: true), Is.EqualTo(PlayerBotLogOffStep.Force));
        Assert.That(Next(givesUp: true, seen: true), Is.EqualTo(PlayerBotLogOffStep.Quit));
        Assert.That(Next(givesUp: true, seen: true, playing: true), Is.EqualTo(PlayerBotLogOffStep.Play));
    }

    [Test]
    public void Then_it_plays_on_after_a_refusal_finds_its_spot_walks_stops_and_quits()
    {
        Assert.That(Next(playing: true, spotChecked: false), Is.EqualTo(PlayerBotLogOffStep.Play));
        Assert.That(Next(spotChecked: false, walking: true, moving: true), Is.EqualTo(PlayerBotLogOffStep.Find));
        Assert.That(Next(walking: true, moving: true), Is.EqualTo(PlayerBotLogOffStep.Walk));
        Assert.That(Next(moving: true), Is.EqualTo(PlayerBotLogOffStep.Stop));
        Assert.That(Next(), Is.EqualTo(PlayerBotLogOffStep.Quit));
        Assert.That(Next(sitting: true), Is.EqualTo(PlayerBotLogOffStep.Quit));   // seated from a rest: /quit as it sits
    }

    // The review of step 1: a bot seated from a rest stands before it walks to its spot (the mover's steps don't stand
    // it); seated for /quit it holds, and seated at its spot it quits as it sits.
    [Test]
    public void Seated_from_a_rest_it_stands_before_the_walk()
    {
        Assert.That(Next(sitting: true, walking: true), Is.EqualTo(PlayerBotLogOffStep.Stand));
        Assert.That(Next(walking: true), Is.EqualTo(PlayerBotLogOffStep.Walk));
        Assert.That(Next(sitting: true, walking: true, quitAsked: true, msSinceQuit: 1_000), Is.EqualTo(PlayerBotLogOffStep.Hold));
        Assert.That(Next(sitting: true, spotChecked: false), Is.EqualTo(PlayerBotLogOffStep.Find));
    }

    [TestCase(599_999, 4, false)] [TestCase(600_000, 0, true)] [TestCase(0, 5, true)]
    public void It_gives_up_after_ten_minutes_or_five_refusals(long ms, int refusals, bool givesUp) =>
        Assert.That(PlayerBotLogOffPlan.GivesUp(ms, refusals), Is.EqualTo(givesUp));

    // The review of step 1: with sessions off, the spot, the self-test and the errands are as at 350d60c, also on a
    // retry after a login that entered the world and dropped before the spawner saw it (entries 1, not the errand's).
    [TestCase(false, 0, false, true)] [TestCase(false, 1, false, true)] [TestCase(false, 1, true, false)]
    [TestCase(true, 0, false, true)] [TestCase(true, 1, false, false)] [TestCase(true, 2, true, false)]
    public void Sessions_off_places_the_spot_as_before_and_on_only_at_the_first_login(bool on, int entries,
        bool errandRelogged, bool places) =>
        Assert.That(PlayerBotSessions.PlacesSpot(on, entries, errandRelogged), Is.EqualTo(places));

    [TestCase(false, true, 1, true)] [TestCase(false, true, 2, true)] [TestCase(false, false, 1, false)]
    [TestCase(true, true, 1, true)] [TestCase(true, true, 2, false)] [TestCase(true, false, 1, false)]
    public void Sessions_off_runs_the_self_test_as_before_and_on_only_at_the_first_login(bool on, bool slot, int entries,
        bool runs) =>
        Assert.That(PlayerBotSessions.RunsSelfTest(on, slot, entries), Is.EqualTo(runs));

    [TestCase(false, 1, true)] [TestCase(false, 2, true)] [TestCase(true, 1, true)] [TestCase(true, 2, false)]
    public void Sessions_off_gives_the_errands_as_before_and_on_once_a_run(bool on, int entries, bool gives) =>
        Assert.That(PlayerBotSessions.GivesErrands(on, entries), Is.EqualTo(gives));

    [TestCase(false, false, false, "no-driver")] [TestCase(false, true, true, "no-driver")] [TestCase(true, true, true, "task")]
    [TestCase(true, false, true, "errand-relog")] [TestCase(true, false, false, null)]
    public void The_log_off_waits_for_a_driver_its_task_and_the_errands_relog(bool driven, bool task, bool errandRelog,
        string wait) =>
        Assert.That(PlayerBotSessions.LogOffWait(driven, task, errandRelog), Is.EqualTo(wait));

    [TestCase(false, 0, true)] [TestCase(true, 0, false)] [TestCase(false, 1, false)] [TestCase(true, 2, false)]
    public void A_camp_is_kept_unless_the_bot_died_there_or_on_its_log_off(bool replanOwed, int deathsSince, bool keeps) =>
        Assert.That(PlayerBotSessions.KeepsCamp(replanOwed, deathsSince), Is.EqualTo(keeps));

    // A think that finds its bot out of the world (a quit racing it) is no death, with sessions on; off, as at 350d60c.
    [TestCase(true, false, true)] [TestCase(true, true, false)] [TestCase(false, false, false)] [TestCase(false, true, false)]
    public void With_sessions_on_a_bot_that_left_during_the_think_is_no_death(bool on, bool active, bool left) =>
        Assert.That(PlayerBotSessions.LeftDuringThink(on, active), Is.EqualTo(left));

    [TestCase(false, false, false)] [TestCase(false, true, false)] [TestCase(true, false, true)] [TestCase(true, true, false)]
    public void Only_with_sessions_on_is_a_dead_bot_released_as_it_logs_out(bool on, bool alive, bool releases) =>
        Assert.That(PlayerBotSessions.ReleasesOnLogout(on, alive), Is.EqualTo(releases));
}
