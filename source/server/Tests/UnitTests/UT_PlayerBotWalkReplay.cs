using System;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using DOL.GS;
using DOL.GS.HearthDAoC.PlayerBots;
using DOL.GS.Tests;
using NUnit.Framework;

namespace DOL.UnitTests;

// HearthDAoC: player-bot walks that stalled, replayed on the installed navmesh. Read-only; never starts the server.
// Set OFFLINE_DAOC_NAV_ROOT to a folder with navmesh/zone008.nav, zone012.nav, zone014.nav, zone104.nav, zone120.nav,
// zone126.nav, zone152.nav, zone181.nav, zone209.nav, zone250.nav and lib/Detour
// (tools/linux/odaoc_fetch.py --lock deploy/upstream.lock extract runtime/server/navmesh/zone008.nav
// FOLDER/navmesh/zone008.nav, and the same for the others). One test per zone: loading a zone's navmesh twice in one
// process crashes the test host. lib/Detour must be built from this tree (source/server/Pathing/Detour, as
// deploy/Dockerfile builds it): the player bots' layer-aware queries (#166) are not in upstream's release library.
[TestFixture, NonParallelizable, Explicit("Needs OFFLINE_DAOC_NAV_ROOT: a folder with navmesh/zone008.nav, zone012.nav, zone014.nav, zone104.nav, zone120.nav, zone126.nav, zone152.nav, zone181.nav, zone209.nav, zone250.nav and lib/Detour")]
public class UT_PlayerBotWalkReplay
{
    // The quest bot stood 513 units short of Olorustos for 14 minutes (lv-r1-stall, 2026-10-25 04:46): its whole-unit
    // spot lay 0.6 units off the mesh, so each step only snapped it back on, and rounding put it back. (Since the corner
    // rule of #166 the rounded walk here gets out too, by its near corner; SliverMesh in UT_PlayerBotDriver keeps the
    // rounding case.)
    [Test]
    public void TheBotWalksOnFromItsStallShortOfOlorustos() =>
        // Campacorentin Forest (region 1, zone 8): the Zones row's OffsetX 59, OffsetY 71, Width 8, Height 8.
        OnZone(8, "Campacorentin Forest", 59, 71, (nav, region, zone) =>
        {
            Vector3 stall = new(497254, 587786, 2310), olorustos = new(496777, 587597, 2292);
            Assert.That(UT_PlayerBotDriver.Walk(nav, region, zone, stall, olorustos, keepExact: true), Is.InRange(1, 20));
        });

    // st-smoke (2026-10-27, #166): Pbotaaaao stood for the rest of the run in Snowdonia, 0.4 units beside its corridor's
    // first corner and 70 units above it, whatever its goal. The corner is passed now.
    [Test]
    public void TheBotWalksOnFromBesideACornerFarBelowIt() =>
        // Snowdonia (region 1, zone 12): OffsetX 61, OffsetY 37, Width 8, Height 8.
        OnZone(12, "Snowdonia", 61, 37, (nav, region, zone) =>
        {
            Vector3 stall = new(535840.4f, 349184, 5387.15f), goal = new(539712, 353104, 4300);
            Assert.That(UT_PlayerBotDriver.Walk(nav, region, zone, stall, goal, keepExact: true), Is.InRange(1, 200));

            // st-fix3: Pbotaaadv stood 88 units under the floor, beyond the surface moves' reach: it steps up onto it.
            Vector3 sunk = new(521313, 317795, 4713), target = new(521067, 316426, 4267);
            Vector3? floor = PlayerBotPathMover.FloorStep(nav, zone, sunk);
            Assert.That(floor, Is.EqualTo(new Vector3(521313, 317795, 4801.47f)).Using<Vector3>((a, b) => Vector3.Distance(a, b) < 1));
            Assert.That(UT_PlayerBotDriver.Walk(nav, region, zone, floor!.Value, target, keepExact: true), Is.InRange(1, 200));

            // st-replan2: Pbotaaabq stood 161 units over the floor, off the mesh, for minutes. Each step stood still
            // without failing, so only a stall ends its walks: then it steps down onto the floor and walks on.
            Vector3 over = new(515046.1f, 358050.7f, 3161.16f), camp = new(513749, 357535, 2823);
            Assert.That(UT_PlayerBotDriver.Walk(nav, region, zone, over, camp, keepExact: true, blockEnds: true), Is.EqualTo(-2), "the stall");
            Vector3? down = PlayerBotPathMover.FloorStep(nav, zone, over);
            Assert.That(down, Is.EqualTo(new Vector3(515046.1f, 358050.7f, 3000.47f)).Using<Vector3>((a, b) => Vector3.Distance(a, b) < 1));
            Assert.That(UT_PlayerBotDriver.Walk(nav, region, zone, down!.Value, camp, keepExact: true), Is.InRange(1, 200));

            // st-final2: Pbotaaack stood for minutes on a polygon edge, where 117 of 507 starts within 0.06 units of its
            // spot don't move (this one among them). Down the mountain it then met the corridor line's float (19 over
            // the floor) and a slope whose surface move returns the start though the ray is clear. 10,000 units on, it
            // gets there.
            Vector3 edge = new(536300.6f, 352870.4f, 6563.43f), worryWort = new(546122, 353692, 4510);
            Assert.That(UT_PlayerBotDriver.Walk(nav, region, zone, edge, worryWort, keepExact: true, ticks: 400, blockEnds: true),
                Is.InRange(1, 400));

            // st-layers2: roam goals 391 and 210 units under the bot, on floors no corridor leads down to. The corridor ends
            // where the bot stands, and each walk stood 3 seconds and ended stalled; now it ends at the first step.
            foreach ((Vector3 from, Vector3 under) in new[] {
                (new Vector3(545789.90f, 356891.30f, 3416.63f), new Vector3(545790, 356889, 3026)),     // Pbotaaabk
                (new Vector3(547655.20f, 353792.10f, 4363.42f), new Vector3(547628, 353675, 4153)) })   // Pbotaaada
            {
                Assert.That(PlayerBotPathMover.TryComputeNext(nav, region, zone, new(), from, under, 0, 47.75f, 0, out _, out string failure),
                    Is.False, $"from {from}");
                Assert.That(failure, Is.EqualTo(PlayerBotPathMover.OffTheCorridor));
            }
        });

    // st-smoke: in Caledon Pbothaade's first corner lay 2.2 units beside it and 123 above it. A surface step that only
    // dropped it back onto the floor counted as a step, so it bounced there for the rest of the run.
    [Test]
    public void TheBotWalksOnUnderACornerFarAboveIt() =>
        // Caledon (region 250, zone 250): OffsetX 1, OffsetY 1, Width 8, Height 8.
        OnZone(250, "Caledon", 1, 1, (nav, region, zone) =>
        {
            Vector3 stall = new(18475, 18946, 4325), goal = new(34053, 13870, 4508);
            Assert.That(UT_PlayerBotDriver.Walk(nav, region, zone, stall, goal, keepExact: true, ticks: 600), Is.InRange(1, 600));
            // Over Caledon's 7 jump links straight up or down, their two ends on one spot, the layer-aware corridor is the
            // plain one (TheLayeredCorridorOverAJumpLinkIsThePlainOne). The corner rule of the fourth review put the landing
            // on the floor before it, the take-off's, and the corridor dropped it: the bot floated 93 units up over 35
            // across instead of taking the jump (the fifth review of #166).
            AssertLayeredIsPlain(nav, zone, CaledonJumps);
        });

    // st-smoke: in the Gripklosa Mountains Pbotmaaae, 267 units from its target with a wall between, took the straight
    // surface move of the last 300 units and crept along the wall.
    [Test]
    public void TheBotWalksRoundAWallNearItsTarget() =>
        // Gripklosa Mountains (region 151, zone 152): OffsetX 34, OffsetY 30, Width 8, Height 8.
        OnZone(152, "Gripklosa Mountains", 34, 30, (nav, region, zone) =>
        {
            Vector3 stall = new(282187.1f, 280977.1f, 4229.36f), target = new(282046, 281204, 4239);
            Assert.That(UT_PlayerBotDriver.Walk(nav, region, zone, stall, target, keepExact: true), Is.InRange(1, 40));

            // The layered corridor (#166) where the floors on the two sides of a seam part: at one seam a tile's floor drops
            // 170 units in the middle of it, and the corner there stays on the slope the bot walks down, not at the drop's
            // foot. At another, a corner at the vertices' height lay 68 units over the floor, and the bot went back and
            // forth under it (75 steps here, 62 of them turning back).
            var corners = new WrappedPathfindingNode[64];
            PathfindingResult down = nav.GetPathStraightLayered(zone, new(302508.3f, 275732.9f, 11465.6f), new(302170.2f, 275883.3f, 11711.7f),
                nav.DefaultFilters, corners);
            int k = Array.FindIndex(corners, 0, down.NodeCount,
                n => Vector2.Distance(new(n.Position.X, n.Position.Y), new(302489.6f, 275741.2f)) < 1);
            Assert.That(k, Is.InRange(1, down.NodeCount - 2), "the corner at the drop");
            Assert.That(corners[k].Position.Z, Is.InRange(corners[k + 1].Position.Z, corners[k - 1].Position.Z), "on the slope");
            Vector3 under = new(283424.9f, 289390.7f, 8300.9f), beyond = new(283031.2f, 289587.8f, 8373.9f);
            Assert.That(UT_PlayerBotDriver.Walk(nav, region, zone, under, beyond, keepExact: true, blockEnds: true), Is.InRange(1, 20));
            // By a ledge 94 units over a slope, the direct step floated the bot off the ledge along the line to its goal,
            // and the next corridor step took it back up onto the ledge, back and forth for good: over a drop the line now
            // takes the corridor (LineLeavesFloor, the fourth review). Up the slope, a corner on the ledge 81 units over it
            // took the bot onto a piece of the ledge with no way on: there the corner lies on the floor the bot reaches it
            // on (the fourth review).
            Assert.That(UT_PlayerBotDriver.Walk(nav, region, zone, new(285453.5f, 270846.8f, 4504.5f), new(285342.8f, 270532.0f, 4378.8f),
                keepExact: true, blockEnds: true), Is.InRange(1, 20), "down off the ledge");
            Assert.That(UT_PlayerBotDriver.Walk(nav, region, zone, new(285358.5f, 270605.5f, 4379.6f), new(285611.7f, 270929.5f, 4429.8f),
                keepExact: true, blockEnds: true), Is.InRange(1, 20), "up the slope and over the ledge");
            // A sliver of no area on a seam, which the corridor crosses at one point: its two corners there stay two, on
            // the floors on its two sides, not one on the sliver's own, 35 and 48 units under them.
            PathfindingResult sliver = nav.GetPathStraightLayered(zone, new(283081.9f, 289800.8f, 8350.1f), new(283046.0f, 289415.8f, 8467.0f),
                nav.DefaultFilters, corners);
            Assert.That(sliver.NodeCount, Is.EqualTo(5));
            Assert.That((corners[2].Position.Z, corners[3].Position.Z), Is.EqualTo((8457.5f, 8470.8f))
                .Using<(float, float)>((a, b) => Math.Abs(a.Item1 - b.Item1) < 1 && Math.Abs(a.Item2 - b.Item2) < 1), "the sliver's two corners");
            Assert.That(LayeredQueriesRan(), Is.True, "lib/Detour has the layer-aware queries");
        });

    // st-replan: in Vendo Caverns Pbotmaaab's corridor turned round a wall by two corners 13 and 20 units from it. Both
    // counted as reached, so it walked at the next corner, 100 units off behind the wall, and every fresh corridor did
    // the same: it stood there for minutes.
    [Test]
    public void TheBotWalksRoundAWallByTheCornersNearIt() =>
        // Vendo Caverns (region 126, zone 126): OffsetX 1, OffsetY 1, Width 8, Height 8.
        OnZone(126, "Vendo Caverns", 1, 1, (nav, region, zone) =>
        {
            Vector3 stall = new(27716.25f, 31724.64f, 16070.8f), goal = new(28693, 33846, 15744);
            Assert.That(UT_PlayerBotDriver.Walk(nav, region, zone, stall, goal, keepExact: true, ticks: 600, blockEnds: true),
                Is.InRange(1, 600));
        });

    // st-final4 (and six runs before it): in Jordheim a roaming bot's corridor crossed a ridge by a corner 35 units up. Its moves
    // toward the corner slid along the ridge's foot into a pocket, and every fresh corridor from there led out and back into it.
    // The pocket is one of eight small floors walled in under the city's ramps, which the mesh links only to the ramps 70-133
    // units above them, at tile seams: the surface moves fell through the ramps onto them, and the corridors out hid the
    // climb in one corner. With the layer-aware queries (#166) the bot gets out of them and walks over them.
    [Test]
    public void TheBotGetsOutOfAndKeepsOffTheFloorsUnderJordheimsRamps() =>
        // Jordheim (region 101, zone 120): OffsetX 1, OffsetY 1, Width 8, Height 8.
        OnZone(120, "Jordheim", 1, 1, (nav, region, zone) =>
        {
            Vector3 pocket = new(32225.28f, 26621.72f, 8762.75f), firstStall = new(32254.63f, 26615.85f, 8762.75f);
            foreach (Vector3 goal in new Vector3[] { new(32204, 28405, 8801), new(30494, 28040, 8766), new(31746, 27019, 8763) })
            {
                Assert.That(UT_PlayerBotDriver.Walk(nav, region, zone, pocket, goal, keepExact: true, ticks: 400, blockEnds: true),
                    Is.InRange(1, 400), $"from the pocket to {goal}");
                Assert.That(UT_PlayerBotDriver.Walk(nav, region, zone, firstStall, goal, keepExact: true, ticks: 400, blockEnds: true),
                    Is.InRange(1, 400), $"from the first stall to {goal}");
            }
            // Out of two others and the pocket floor's south end, toward goals bots had there: blocked after 29-40 steps
            // before.
            foreach ((Vector3 from, Vector3 goal) in new[] {
                (new Vector3(32169, 26518, 8766), new Vector3(32145, 26460, 8823)),                             // st-fix6
                (new Vector3(31257.7f, 26596.0f, 8818.8f), new Vector3(30859, 27162, 8818)),                    // lm-fsmoke
                (new Vector3(30990.1f, 26822.2f, 8847.4f), new Vector3(31024, 26697, 8891)) })                  // lm-ona
                Assert.That(UT_PlayerBotDriver.Walk(nav, region, zone, from, goal, keepExact: true, ticks: 400, blockEnds: true),
                    Is.InRange(1, 100), $"from {from} to {goal}");
            // Over each of them on the ramp, not down onto it: these walks timed out after 150 steps on the floor below, or
            // were blocked after 33 and 18.
            foreach ((Vector3 from, Vector3 goal) in new[] {
                (new Vector3(32315.82f, 26506.08f, 8796.79f), new Vector3(32091.76f, 26532.80f, 8781.00f)),
                (new Vector3(31378.30f, 26576.80f, 8864.81f), new Vector3(31235.76f, 26625.87f, 8816.79f)),
                (new Vector3(31026.96f, 26935.20f, 8804.27f), new Vector3(30992.94f, 26782.56f, 8917.07f)) })
                Assert.That(UT_PlayerBotDriver.Walk(nav, region, zone, from, goal, keepExact: true, ticks: 400, blockEnds: true),
                    Is.InRange(1, 20), $"from {from} to {goal}");
            // Without a stall (st-layers1 and the reviews): on the way down a link to a foot 73 units below, the bot took
            // the ramp it came from 22.6 units above the foot (38 steps, with a replan); a corner on the ramp at its
            // vertices' height, 47 units over a floor walled in under it and 50 under the ramp's own, took it onto that
            // floor (28); the top of the link out of a floor walled in under another part of the city lay 72 units under
            // the floor it leads onto, and the bot stalled 56 units under that floor, where its replan found no path; and
            // a direct step from the top of a link left that corner queued, and each corridor step after went back to it
            // (73 steps). By two other links the direct steps, the line of sight seeing through the link, and the corridor
            // took turns, back and forth to the end (400 steps), the corridor's next corner left behind the bot.
            foreach ((Vector3 from, Vector3 goal, int most) in new[] {
                (new Vector3(31976.0f, 27124.0f, 8829.8f), new Vector3(32364, 27658, 8766), 34),
                (new Vector3(30957.2f, 26791.2f, 8933.5f), new Vector3(31336.3f, 26464.8f, 8913.1f), 20),
                (new Vector3(34860, 32405.5f, 8353), new Vector3(34900, 32470, 8480), 10),
                (new Vector3(31369.525f, 26484.291f, 8803.395f), new Vector3(31023.115f, 26619.355f, 8936.37f), 20),
                (new Vector3(30625.0f, 26644.5f, 9083.0f), new Vector3(30334.7f, 26579.7f, 8978.3f), 20),
                (new Vector3(31787.4f, 26990.5f, 8762.7f), new Vector3(32130.6f, 27010.3f, 8802.6f), 20) })
                Assert.That(UT_PlayerBotDriver.Walk(nav, region, zone, from, goal, keepExact: true, ticks: 400, blockEnds: true),
                    Is.InRange(1, most), $"from {from} to {goal}");
            Assert.That(LayeredQueriesRan(), Is.True, "lib/Detour has the layer-aware queries");
        });

    // #166: where no tile seam joins two floors, the layer-aware corridor has the plain one's corners, each on its polygon's
    // floor. A first version took an off-mesh connection's link (Detour gives it edge 0xff) for a seam's: on corridors back
    // across a two-way jump link between two tiles it read past the polygon's vertices and put corners as far off as
    // (0, 0, z), in zones 8, 12, 14 and 250. Here the Pennine Mountains' 15 such links, both ways.
    [Test]
    public void TheLayeredCorridorOverAJumpLinkIsThePlainOne() =>
        // Pennine Mountains (region 1, zone 14): OffsetX 69, OffsetY 43, Width 8, Height 8.
        OnZone(14, "Pennine Mountains", 69, 43, (nav, region, zone) => AssertLayeredIsPlain(nav, zone, PennineJumps));

    private static void AssertLayeredIsPlain(IPathfindingMgr nav, Zone zone, (Vector3, Vector3)[] jumps)
    {
        var plain = new WrappedPathfindingNode[256];
        var layered = new WrappedPathfindingNode[256];
        foreach ((Vector3 start, Vector3 end) in jumps)
            foreach ((Vector3 a, Vector3 b) in new[] { (start, end), (end, start) })
            {
                PathfindingResult p = nav.GetPathStraight(zone, a, b, nav.DefaultFilters, plain);
                PathfindingResult l = nav.GetPathStraightLayered(zone, a, b, nav.DefaultFilters, layered);
                Assert.That(p.NodeCount, Is.GreaterThan(1), $"{a} -> {b}");
                Assert.That(l.Status, Is.EqualTo(p.Status), $"{a} -> {b}");
                Assert.That(Array.ConvertAll(layered[..l.NodeCount], n => (n.Position.X, n.Position.Y, n.Flags)),
                    Is.EqualTo(Array.ConvertAll(plain[..p.NodeCount], n => (n.Position.X, n.Position.Y, n.Flags))), $"{a} -> {b}");
            }
        Assert.That(LayeredQueriesRan(), Is.True, "lib/Detour has the layer-aware queries");
    }

    // Whether the layer-aware queries ran on lib/Detour, not on the plain ones it falls back to without them (one warning,
    // which the test host does not show): with those, the comparison above compares the plain corridor with itself.
    private static bool LayeredQueriesRan() =>
        (int)typeof(LocalPathfindingMgr).GetField("_noLayers", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)! == 0;

    // The two ends, near each, of the jump links straight up or down in zone250.nav: two-way, 80-95 units high.
    private static readonly (Vector3, Vector3)[] CaledonJumps =
    {
        (new(53198, 23932, 4595), new(53210, 23932, 4681)),
        (new(53140, 23905, 4595), new(53156, 23914, 4691)),
        (new(54870, 24542, 4595), new(54871, 24541, 4690)),
        (new(54832, 24522, 4595), new(54814, 24529, 4680)),
        (new(53023, 24888, 4579), new(53025, 24891, 4659)),
        (new(54333, 25372, 4579), new(54344, 25363, 4659)),
        (new(54351, 25442, 4579), new(54372, 25422, 4659)),
    };

    // The two ends of the cross-tile two-way off-mesh connections in zone014.nav.
    private static readonly (Vector3, Vector3)[] PennineJumps =
    {
        (new(583219.2f, 365446.4f, 6345.6f), new(583180.8f, 365475.2f, 6345.6f)),
        (new(583158.4f, 365478.4f, 6233.6f), new(583206.4f, 365449.6f, 6246.4f)),
        (new(581616.0f, 366384.0f, 6345.6f), new(581606.4f, 366406.4f, 6345.6f)),
        (new(581619.2f, 366428.8f, 6249.6f), new(581625.6f, 366377.6f, 6249.6f)),
        (new(585212.8f, 396300.8f, 3984.0f), new(585232.0f, 396307.2f, 3984.0f)),
        (new(585174.4f, 396284.8f, 3792.0f), new(585257.6f, 396316.8f, 3795.2f)),
        (new(585232.0f, 396307.2f, 3984.0f), new(585212.8f, 396300.8f, 3984.0f)),
        (new(585232.0f, 396307.2f, 3984.0f), new(585200.0f, 396294.4f, 4070.4f)),
        (new(585257.6f, 396316.8f, 3795.2f), new(585174.4f, 396284.8f, 3792.0f)),
        (new(585257.6f, 396316.8f, 3795.2f), new(585164.8f, 396281.6f, 4044.8f)),
        (new(585484.8f, 396784.0f, 2841.6f), new(585523.2f, 396745.6f, 2841.6f)),
        (new(585523.2f, 396780.8f, 2710.4f), new(585497.6f, 396812.8f, 2710.4f)),
        (new(585523.2f, 396745.6f, 2841.6f), new(585484.8f, 396784.0f, 2841.6f)),
        (new(585497.6f, 396812.8f, 2710.4f), new(585484.8f, 396784.0f, 2841.6f)),
        (new(575993.6f, 398880.0f, 5552.0f), new(576016.0f, 398883.2f, 5552.0f)),
    };

    // st-fix2: in Tir na Nog Pbothaacw stood 84 units above the floor, off the mesh: every step lost the walkable
    // surface, for the rest of the run. It steps onto the nearest floor, 51 units away, and walks on from there.
    [Test]
    public void TheBotStepsOntoTheFloorFromOffTheMeshAndWalksOn() =>
        // Tir na Nog (region 201, zone 209): OffsetX 1, OffsetY 1, Width 8, Height 8.
        OnZone(209, "Tir na Nog", 1, 1, (nav, region, zone) =>
        {
            Vector3 lost = new(24125, 33752, 6576), goal = new(23045, 34295, 6400);
            Assert.That(PlayerBotPathMover.TryComputeNext(nav, region, zone, new(), lost, goal, 20, 47.75f, 0, out _, out string failure),
                Is.False);
            Assert.That(failure, Does.StartWith("Lost the walkable surface"));
            Vector3? floor = PlayerBotPathMover.FloorStep(nav, zone, lost);
            Assert.That(floor, Is.Not.Null);
            Assert.That(Vector3.Distance(lost, floor!.Value), Is.LessThan(PlayerBotPathMover.FloorStepRange));
            Assert.That(PlayerBotPathMover.FloorStep(nav, zone, floor.Value), Is.Null, "on floor with a way out: no step");
            Assert.That(UT_PlayerBotDriver.Walk(nav, region, zone, floor.Value, goal, keepExact: true), Is.InRange(1, 200));

            // st-fix3: the same bot stood 79 units under the floor: it steps up onto the floor straight above.
            Vector3 sunk = new(24140, 33873, 6582);
            Assert.That(PlayerBotPathMover.FloorStep(nav, zone, sunk),
                Is.EqualTo(new Vector3(24140, 33873, 6660.8f)).Using<Vector3>((a, b) => Vector3.Distance(a, b) < 1));

            // st-fix4: here the mesh joins the floor at 6494 to the one at 6661, 167 units up over 41, with no floor
            // between. Half way up, the surface moves found a middle floor (6574) 30 units aside, and the bot stood on its
            // edge for minutes, its next corner 87 units overhead. It goes up the link to the corner, as a GameBot does.
            Vector3 middle = new(24084.06f, 33671.36f, 6574.4f), upstairs = new(24298, 33669, 6661);
            Assert.That(UT_PlayerBotDriver.Walk(nav, region, zone, middle, upstairs, keepExact: true), Is.InRange(1, 200));
            Vector3 below = new(24063.2f, 33648, 6494.4f);
            Assert.That(UT_PlayerBotDriver.Walk(nav, region, zone, below, upstairs, keepExact: true), Is.InRange(1, 200));
        });

    // st-layers3: Pbothaaeq's respawn spot in Domnann lay 124 units under the floor, and its corridor from there led
    // nowhere. Its walks ended blocked 22 times in two minutes, to a target straight above it among them. Detour's 64-unit
    // box found the floor, so the bot counted as within the surface moves' reach. It steps up onto the floor and walks on.
    [Test]
    public void TheBotStepsUpOntoTheFloorFarAboveItsRespawnSpot() =>
        // Domnann (region 181, zone 181): OffsetX 48, OffsetY 48, Width 8, Height 8.
        OnZone(181, "Domnann", 48, 48, (nav, region, zone) =>
        {
            Vector3 spot = new(446669, 401530, 4620), target = new(446668, 401531, 4743), far = new(443010, 404859, 4522);
            Assert.That(PlayerBotPathMover.TryComputeNext(nav, region, zone, new(), spot, target, 20, 47.75f, 0, out _, out string failure),
                Is.False, "from the spot");
            Assert.That(failure, Does.StartWith("No complete walkable path"));
            Vector3? floor = PlayerBotPathMover.FloorStep(nav, zone, spot);
            Assert.That(floor, Is.EqualTo(new Vector3(446669, 401530, 4744.6f)).Using<Vector3>((a, b) => Vector3.Distance(a, b) < 1));
            Assert.That(PlayerBotPathMover.FloorStep(nav, zone, floor!.Value), Is.Null, "on floor with a way out: no step");
            Assert.That(UT_PlayerBotDriver.Walk(nav, region, zone, floor.Value, target, keepExact: true), Is.InRange(0, 200));
            Assert.That(UT_PlayerBotDriver.Walk(nav, region, zone, floor.Value, far, keepExact: true, ticks: 400), Is.InRange(1, 400));
            DomeRestWalk(nav, region, zone);          // #188, here: one test per zone
        });

    // #188, st-deathsE: Pbothaaem's rest walk from its fight's end to a rest spot 600 units out, on 18 polygons walled in
    // under a dome. The corridor there is found (its last part ends at the spot's X and Y at the spot's height,
    // PlayerTravelPath.cs:102-105), and the walk arrives on the dome's floor, from which no walk leads back. With the
    // island guard (home: its camp) the step down the dome's link is refused on the camp's island, and the spot is no
    // spot to walk to (OnMesh's test).
    private static void DomeRestWalk(IPathfindingMgr nav, Region region, Zone zone)
    {
        Vector3 fightEnd = new(448159, 402116, 4473), dome = new(448679, 402416, 4474), camp = new(449594, 399561, 4541);
        NavIsland Island(Vector3 at) => nav.GetIsland(zone, at, nav.DefaultFilters) ?? throw new AssertionException($"no island at {at}");
        Assert.That((Island(fightEnd), Island(camp).Polygons, Island(dome).Polygons, Island(new(448759, 402116, 4474)) == Island(dome)),
            Is.EqualTo((Island(camp), 57_083, 18, true)), "the camp's island, and the dome's 18 polygons with both rest spots");
        Vector3 end = fightEnd;
        Assert.That(UT_PlayerBotDriver.Walk(nav, region, zone, fightEnd, dome, keepExact: true, blockEnds: true, end: p => end = p),
            Is.InRange(1, 200), "unguarded: it arrives");
        Assert.That(Island(end), Is.EqualTo(Island(dome)), "unguarded: on the dome's floor");
        Assert.That(UT_PlayerBotDriver.Walk(nav, region, zone, fightEnd, dome, keepExact: true, blockEnds: true,
            network: new PlayerBotNetwork(), home: camp, end: p => end = p), Is.EqualTo(-3), "guarded: refused");
        Assert.That(Island(end), Is.EqualTo(Island(camp)), "guarded: on the camp's island");
        Assert.That(new PlayerBotNetwork().Accepts(nav, zone, fightEnd, dome, camp), Is.False, "no rest spot");
    }

    // #188, st-deathsE2b: Pbotmaada's camp in Muspelheim (camp 185 of spots-lm3) lies under a ledge piece of the navmesh
    // (10,551 polygons) with no way down to the camp's (42,359), and Detour's nearest polygon over the camp's floor is often
    // the ledge's. The sweep's rest and anchor walks there (its frozen starts), from the camp centre and from the Thane's
    // fire-eater: their corridor steps kept the line's height over a ramp and stood on the ledge, blocked there for good.
    // With the island guard the floor under such a step, on the camp's island, is taken, and both walks arrive. The live
    // entry was mostly direct steps (the review of #188): DirectStepOntoTheLedge.
    [Test]
    public void TheWalksOntoTheLedgeAboveTheThanesCampKeepToTheCamp() =>
        // Muspelheim (region 100, zone 104): OffsetX 100, OffsetY 78, Width 8, Height 8.
        OnZone(104, "Muspelheim", 100, 78, (nav, region, zone) =>
        {
            Vector3 camp = new(840323, 702066, 8343), login = new(840322, 702055, 8327), thane = new(840884, 702153, 8784),
                ledge = new(839450, 702117, 8244), over = new(840400, 702037, 8392);
            NavIsland Island(Vector3 at) => nav.GetIsland(zone, at, nav.DefaultFilters) ?? throw new AssertionException($"no island at {at}");
            Assert.That((Island(camp).Polygons, Island(login), Island(thane).Polygons, Island(ledge), Island(over)),
                Is.EqualTo((42_359, Island(camp), 10_551, Island(thane), Island(thane))), "the camp's island and the ledge's");
            Vector3 fireEater = new(840919.75f, 702040.3f, 8514.667f);
            foreach ((string walk, Vector3 from, Vector3 goal) in new[]
                     {
                         ("6771 rest", camp, new Vector3(840922.44f, 702040.25f, 8515.732f)),
                         ("7438 anchor", fireEater, camp),
                     })
            {
                Vector3 end = from;
                Assert.That(UT_PlayerBotDriver.Walk(nav, region, zone, from, goal, keepExact: true, ticks: 480, blockEnds: true,
                    end: p => end = p), Is.EqualTo(-2), $"{walk} unguarded: stalled");
                Assert.That(Island(end), Is.EqualTo(Island(thane)), $"{walk} unguarded: on the ledge");
                Assert.That(UT_PlayerBotDriver.Walk(nav, region, zone, from, goal, keepExact: true, ticks: 480, blockEnds: true,
                    network: new PlayerBotNetwork(), home: camp, end: p => end = p), Is.InRange(1, 40), $"{walk} guarded: arrives");
                Assert.That(Island(end), Is.EqualTo(Island(camp)), $"{walk} guarded: on the camp's island");
            }
            // 7438's first step: the corridor's plain move, 25 units up the line, over the ledge's polygon; the floor 43 units
            // under it is the camp's.
            Assert.That(PlayerBotPathMover.TryComputeNext(nav, region, zone, new DOL.GS.Commands.PlayerTravelPath(), fireEater, camp,
                20, 47.75f, 0, out Vector3 next, out _), Is.True);
            Assert.That(Island(next), Is.EqualTo(Island(thane)));
            Assert.That(new PlayerBotNetwork().Keeps(nav, region, zone, fireEater, ref next, null, camp, out string crossing), Is.True);
            Assert.That((Island(next), next.Z, crossing), Is.EqualTo((Island(camp), 8496.05f, crossing)).Using<(NavIsland, float, string)>(
                (a, b) => a.Item1 == b.Item1 && Math.Abs(a.Item2 - b.Item2) < 1 && a.Item3.StartsWith("kind=ground")));
            DirectStepOntoTheLedge(nav, region, zone, camp, Island(camp), Island(thane));     // the review's, here: one test per zone
        });

    // #188 review R2: the live entry. In the walks rebuilt from st-deathsE2b's leg (w104-big's START lines at camp 185),
    // 428 of the 437 first moves off the camp's island were direct steps, and 367 of the 376 walks that ended off it were
    // hunt walks to igniterA, a dverge igniter by the ledge's edge, which arrived on the ledge with a last direct step
    // about 40 units up.
    // Rebuilt walk 4856, from the slope below the camp: unguarded it arrives on the ledge; with the island guard (home:
    // the camp) the floor under that step, on the camp's island, is taken, and it arrives there.
    private static void DirectStepOntoTheLedge(IPathfindingMgr nav, Region region, Zone zone, Vector3 camp, NavIsland campIsland,
        NavIsland ledge)
    {
        Vector3 slope = new(839573, 700791, 6577.16f), igniter = new(840856, 702055, 8516), end = slope;
        NavIsland? Island(Vector3 at) => nav.GetIsland(zone, at, nav.DefaultFilters);
        Assert.That(Island(slope), Is.EqualTo(campIsland), "4856 starts on the camp's island");
        Assert.That(UT_PlayerBotDriver.Walk(nav, region, zone, slope, igniter, keepExact: true, blockEnds: true, end: p => end = p),
            Is.InRange(1, 200), "4856 unguarded: arrives");
        Assert.That(Island(end), Is.EqualTo(ledge), "4856 unguarded: on the ledge");
        Assert.That(UT_PlayerBotDriver.Walk(nav, region, zone, slope, igniter, keepExact: true, blockEnds: true,
            network: new PlayerBotNetwork(), home: camp, end: p => end = p), Is.InRange(1, 200), "4856 guarded: arrives");
        Assert.That(Island(end), Is.EqualTo(campIsland), "4856 guarded: on the camp's island");
        // Its last step, from the rebuild (to a tenth of a unit): a direct step, within a corner's reach of the goal (17 units
        // across, 24 down: TryComputeNext's first rule for one), whose plain move stands on the ledge's polygon about 40
        // units up; the guard takes the floor under it instead.
        Vector3 from = new(840852.3f, 702038.2f, 8492.3f);
        Assert.That(DOL.GS.Commands.PlayerTravelPath.HasReachedCorner(from, igniter), Is.True, "a direct step");
        Assert.That(PlayerBotPathMover.TryComputeNext(nav, region, zone, new DOL.GS.Commands.PlayerTravelPath(), from, igniter,
            20, 47.75f, 0, out Vector3 next, out _), Is.True);
        Assert.That((Island(next), next.Z - from.Z), Is.EqualTo(((NavIsland?)ledge, 42.8f)).Using<(NavIsland?, float)>(
            (a, b) => a.Item1 == b.Item1 && Math.Abs(a.Item2 - b.Item2) < 1), "onto the ledge, about 40 units up");
        Assert.That(new PlayerBotNetwork().Keeps(nav, region, zone, from, ref next, null, camp, out string crossing), Is.True);
        Assert.That((Island(next), crossing), Is.EqualTo(((NavIsland?)campIsland, crossing)).Using<(NavIsland?, string)>(
            (a, b) => a.Item1 == b.Item1 && a.Item2.StartsWith("kind=ground")), "the floor under it, on the camp's island");
    }

    // One test per zone: UnloadNavMesh frees the mesh but leaves the thread's query on it (LocalPathfindingMgr's
    // _navmeshQueries, by zone ID), and a second load of the zone in the same run then crashes the test host.
    private static void OnZone(ushort id, string name, int offsetX, int offsetY, Action<IPathfindingMgr, Region, Zone> walk)
    {
        string root = Environment.GetEnvironmentVariable("OFFLINE_DAOC_NAV_ROOT");
        Assert.That(root, Is.Not.Null.And.Not.Empty);
        string previous = Environment.CurrentDirectory;
        Zone zone = null;
        try
        {
            Environment.CurrentDirectory = root;
            var region = (Region)RuntimeHelpers.GetUninitializedObject(typeof(Region));
            typeof(Region).GetField("m_regionData", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(region, new RegionData { Id = 1 });
            zone = new(region, id, name, offsetX * 8192, offsetY * 8192, 8 * 8192, 8 * 8192, id, false, 0, false, 0, 0, 0, 0, 0);
            typeof(Region).GetField("m_zones", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(region, new List<Zone> { zone });
            LocalPathfindingMgr.LoadNavMesh(zone);
            var nav = PathfindingProvider.LocalPathfindingMgr;
            Assert.That(nav.HasNavmesh(zone), Is.True);
            walk(nav, region, zone);
        }
        finally
        {
            if (zone != null) LocalPathfindingMgr.UnloadNavMesh(zone);
            Environment.CurrentDirectory = previous;
        }
    }
}
