using System;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using DOL.GS.HearthDAoC.PlayerBots;
using NUnit.Framework;

namespace DOL.GS.Tests;

// #188: a flat floor in two islands, as Domnann's: the camp's floor west of Border (Camp: 57,082 polygons, near the
// main piece's 57,083), a floor walled in east of it (Pocket: 18 polygons, the piece under the dome); no polygon under
// Air (a step in the air). With Wall, the grounded move stops a unit short of Border (the floor's edge), the plain move
// goes on; with NoGround it finds no floor. With Shelf, east of Border only spots higher than Shelf are on the pocket
// (a ledge piece over the camp's floor, as Muspelheim's), lower ones on the camp's floor. Corridors run straight to the goal and are always found (as the false corridor tail of TryBuildCorridor,
// PlayerTravelPath.cs:102-105); no line of sight, so the mover walks them. Islands counts GetIsland's calls.
// With NoCorridor no corridor is found: a walk ends blocked at once. With Floor, one floor at that height: the closest
// point snaps onto it within the query box's height, an island is read only within the moves' 64 units of it, and the
// GameBots' floor searches run (IsAvailable): a bot 124 units under it stands on no polygon, as at #166's respawn spot.
internal sealed class PlayerBotIslandMesh : PathfindingMgrBase
{
    public static readonly NavIsland Camp = new(1, 57_082), Pocket = new(2, 18);
    public readonly float Border;
    public const float Air = -500;
    public bool Available = true, Wall, NoGround, NoCorridor;
    public float Shelf = float.NegativeInfinity;
    public float? Floor;
    public int Islands;

    public PlayerBotIslandMesh(float border = 1_000) => Border = border;
    public override bool HasNavmesh(Zone zone) => Available;
    public override bool IsAvailable => Floor.HasValue;
    public override NavIsland? GetIsland(Zone zone, Vector3 position, EDtPolyFlags[] filters)
    {
        Islands++;
        if (position.Z < Air || Floor is float floor && Math.Abs(position.Z - floor) > 64) return null;
        return position.X < Border || position.Z <= Shelf ? Camp : Pocket;
    }
    public override PathfindingResult GetPathStraight(Zone zone, Vector3 start, Vector3 end, EDtPolyFlags[] filters,
        Span<WrappedPathfindingNode> destination)
    {
        if (NoCorridor) return new(PathfindingStatus.NoPathFound, 0);
        destination[0] = new(start, EDtPolyFlags.Walk);
        destination[1] = new(end, EDtPolyFlags.Walk);
        return new(PathfindingStatus.PathFound, 2);
    }
    public override Vector3? GetMoveAlongSurfaceLayered(Zone zone, Vector3 start, Vector3 end, EDtPolyFlags[] filters, bool grounded) =>
        grounded && NoGround ? null :
        grounded && Wall && start.X < Border && end.X >= Border ? new Vector3(Math.Max(start.X, Border - 1), end.Y, end.Z) : end;
    public override Vector3? GetClosestPoint(Zone zone, Vector3 position, float xRange, float yRange, float zRange,
        EDtPolyFlags[] filters) =>
        position.Z < Air ? null :
        Floor is float floor ? (Math.Abs(position.Z - floor) <= zRange ? position with { Z = floor } : null) : position;
    public override bool HasLineOfSight(Zone zone, Vector3 start, Vector3 end, EDtPolyFlags[] filters) => false;
}

// #188: PlayerBotNetwork, the mover's guard against steps onto a piece of navmesh with no way back (PlayerBotPathMover
// .StepToward, .TryFloorStep and .OnItsNetwork), on the fake two-island floor above.
[TestFixture]
public sealed class UT_PlayerBotNetwork
{
    private static (Region Region, Zone Zone) World()
    {
        const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        var region = (Region)RuntimeHelpers.GetUninitializedObject(typeof(Region));
        Zone zone = new(region, 1, "Test", 0, 0, 65536, 65536, 1, false, 0, false, 0, 0, 0, 0, 0);
        typeof(Region).GetField("m_zones", Hidden)!.SetValue(region, new List<Zone> { zone });
        return (region, zone);
    }

    private static NavIsland? Island(int id, int polygons) => id == 0 ? null : new NavIsland(id, polygons);

    // The rule: a step may stay on its island or step onto no polygon (the air); onto another island only onto the
    // camp's, or, with no camp or a camp on a smaller island than the bot's (a pocket camp must not pull it in), onto a
    // larger one. From nowhere known: only the camp's, or any with no camp. 0 = none.
    [TestCase(1, 100, 0, 0, 0, 0, true, TestName = "Accepts: into the air")]
    [TestCase(1, 100, 1, 100, 3, 500, true, TestName = "Accepts: the same island")]
    [TestCase(2, 18, 1, 57_082, 1, 57_082, true, TestName = "Accepts: back onto the camp's island")]
    [TestCase(1, 57_082, 2, 18, 1, 57_082, false, TestName = "Accepts: off the camp's island onto a pocket")]
    [TestCase(1, 100, 3, 500, 1, 100, false, TestName = "Accepts: off the camp's island onto a larger one")]
    [TestCase(1, 42_359, 2, 10_551, 0, 0, false, TestName = "Accepts: no camp, onto a smaller island")]
    [TestCase(2, 10_551, 1, 42_359, 0, 0, true, TestName = "Accepts: no camp, onto a larger island")]
    [TestCase(1, 100, 2, 100, 0, 0, false, TestName = "Accepts: no camp, onto one as large")]
    [TestCase(1, 42_359, 2, 10_551, 2, 10_551, false, TestName = "Accepts: a camp on a smaller island does not pull the bot in")]
    [TestCase(2, 500, 1, 42_359, 3, 18, true, TestName = "Accepts: a camp on a smaller island, onto a larger one")]
    [TestCase(0, 0, 2, 18, 1, 57_082, false, TestName = "Accepts: from nowhere known, not off the camp's")]
    [TestCase(0, 0, 1, 57_082, 1, 57_082, true, TestName = "Accepts: from nowhere known, onto the camp's")]
    [TestCase(0, 0, 2, 18, 0, 0, true, TestName = "Accepts: from nowhere known, no camp")]
    public void Accepts(int from, int fromPolygons, int to, int toPolygons, int home, int homePolygons, bool accepted) =>
        Assert.That(PlayerBotNetwork.Accepts(Island(from, fromPolygons), Island(to, toPolygons), Island(home, homePolygons)),
            Is.EqualTo(accepted));

    // A walk on its island: every step kept, no crossing; one island read a step (the next spot's), the bot's own read
    // only where the guard did not put it.
    [Test]
    public void Steps_on_the_bots_island_are_kept_with_one_island_read_a_step()
    {
        var (region, zone) = World();
        var mesh = new PlayerBotIslandMesh();
        var network = new PlayerBotNetwork();
        Vector3 at = new(900, 50, 0), next = new(947.75f, 50, 0);
        Assert.That(network.Keeps(mesh, region, zone, at, ref next, null, null, out string crossing), Is.True);
        Assert.That((crossing, mesh.Islands, network.On), Is.EqualTo(((string)null, 2, (NavIsland?)PlayerBotIslandMesh.Camp)));
        Vector3 on = new(995.5f, 50, 0);
        Assert.That(network.Keeps(mesh, region, zone, next, ref on, null, null, out crossing), Is.True);
        Assert.That((crossing, mesh.Islands), Is.EqualTo(((string)null, 3)), "from where it put the bot: one read");
        Vector3 still = on;
        Assert.That(network.Keeps(mesh, region, zone, on, ref still, null, null, out crossing), Is.True);
        Assert.That(mesh.Islands, Is.EqualTo(3), "a still step: no read");
    }

    // Domnann (st-deathsE): the step onto the 18 polygons under the dome is refused, with no camp too (a smaller island);
    // the step and the crossing are logged, the step left as it was.
    [Test]
    public void A_step_onto_a_pocket_is_refused()
    {
        var (region, zone) = World();
        var mesh = new PlayerBotIslandMesh();
        foreach (Vector3? home in new Vector3?[] { new(0, 50, 0), null })
        {
            var network = new PlayerBotNetwork();
            Vector3 at = new(990, 50, 0), next = new(1_037.7f, 50, 0);
            Assert.That(network.Keeps(mesh, region, zone, at, ref next, null, home, out string crossing), Is.False);
            Assert.That(next, Is.EqualTo(new Vector3(1_037.7f, 50, 0)));
            Assert.That(crossing, Is.EqualTo("kind=step at=(990.0,50.0,0.0) to=(1037.7,50.0,0.0) island=1/57082->2/18 home=" +
                (home == null ? "none" : "1/57082")));
        }
    }

    // A refused step whose floor (the grounded move from where the bot stands) stays on its island: that floor is taken,
    // kind=ground. Short of a unit, the step is refused.
    [Test]
    public void The_floor_under_a_refused_step_is_taken_on_the_bots_island()
    {
        var (region, zone) = World();
        var mesh = new PlayerBotIslandMesh { Wall = true };
        var network = new PlayerBotNetwork();
        Vector3 at = new(980, 50, 0), next = new(1_027.7f, 50, 0);
        Assert.That(network.Keeps(mesh, region, zone, at, ref next, null, null, out string crossing), Is.True);
        Assert.That((next, crossing), Is.EqualTo((new Vector3(999, 50, 0),
            "kind=ground at=(980.0,50.0,0.0) to=(1027.7,50.0,0.0) island=1/57082->2/18 home=none")));
        Vector3 edge = next, on = new(1_046.75f, 50, 0);
        Assert.That(network.Keeps(mesh, region, zone, edge, ref on, null, null, out crossing), Is.False, "at the edge: no floor on");
        Assert.That(crossing, Does.StartWith("kind=step"));
    }

    // Muspelheim (the sweep's walk 7438): a corridor step that kept its line's height stood 43 units over the camp's floor,
    // where the ledge piece above is Detour's nearest polygon, and the grounded move found no floor: the floor straight
    // under the step, within FloorBelow, on the bot's island, is taken. Over FloorBelow under it, none: refused.
    [Test]
    public void The_floor_straight_under_a_refused_step_is_taken_on_the_bots_island()
    {
        var (region, zone) = World();
        var mesh = new PlayerBotIslandMesh { Shelf = 30, NoGround = true };
        var network = new PlayerBotNetwork();
        Vector3 at = new(980, 50, 0), next = new(1_027.7f, 50, 43);
        Assert.That(network.Keeps(mesh, region, zone, at, ref next, null, null, out string crossing), Is.True);
        Assert.That((next, crossing), Is.EqualTo((new Vector3(1_027.7f, 50, 11),
            "kind=ground at=(980.0,50.0,0.0) to=(1027.7,50.0,43.0) island=1/57082->2/18 home=none")));
        mesh.Shelf = -40;
        Vector3 high = new(1_075.4f, 50, 43);
        Assert.That(network.Keeps(mesh, region, zone, next, ref high, null, null, out crossing), Is.False, "its floor 83 units under it");
    }

    // Off its island (a teleport, a respawn), a step back onto the camp's is taken and logged as one (kind=took).
    [Test]
    public void A_step_back_onto_the_camps_island_is_taken()
    {
        var (region, zone) = World();
        var mesh = new PlayerBotIslandMesh();
        var network = new PlayerBotNetwork();
        Vector3 at = new(1_020, 50, 0), next = new(972.3f, 50, 0);
        Assert.That(network.Keeps(mesh, region, zone, at, ref next, null, new(0, 50, 0), out string crossing), Is.True);
        Assert.That(crossing, Is.EqualTo("kind=took at=(1020.0,50.0,0.0) to=(972.3,50.0,0.0) island=2/18->1/57082 home=1/57082"));
        Assert.That(network.On, Is.EqualTo(PlayerBotIslandMesh.Camp));
    }

    // A step into the air (no polygon in the moves' box: off a ledge, partway down a link) changes nothing yet; the next
    // step is judged from the floor the bot last stood on. A bot first seen in the air, with no camp, may land anywhere.
    [Test]
    public void A_step_in_the_air_is_judged_from_the_last_floor()
    {
        var (region, zone) = World();
        var mesh = new PlayerBotIslandMesh();
        var network = new PlayerBotNetwork();
        Vector3 at = new(900, 50, 0), up = new(947.75f, 50, -600);
        Assert.That(network.Keeps(mesh, region, zone, at, ref up, null, null, out string crossing), Is.True);
        Assert.That(crossing, Is.Null);
        Vector3 down = new(1_010, 50, 0);
        Assert.That(network.Keeps(mesh, region, zone, up, ref down, null, null, out crossing), Is.False);
        Assert.That(crossing, Does.Contain("island=1/57082->2/18"));
        var fresh = new PlayerBotNetwork();
        down = new(1_010, 50, 0);
        Assert.That(fresh.Keeps(mesh, region, zone, up, ref down, null, null, out crossing), Is.True, "no floor known, no camp");
    }

    // On a floor link (PlayerTravelPath.LinkEnd) the steps up or down hang in the air or snap to whatever floor lies near:
    // the link's far corner is judged, once a link, and no floor is taken in a refused step's place.
    [Test]
    public void On_a_floor_link_its_far_corner_is_judged_once()
    {
        var (region, zone) = World();
        var mesh = new PlayerBotIslandMesh { Wall = true };
        var network = new PlayerBotNetwork();
        Vector3 top = new(980, 50, 0), foot = new(990, 50, 188);   // down onto the camp's floor: kept, mid-link spots on the pocket
        Vector3 at = new(1_020, 50, 0);
        for (int i = 0; i < 3; i++)
        {
            Vector3 next = new(1_015 - i * 5, 50, 60 * (i + 1));
            Assert.That(network.Keeps(mesh, region, zone, at, ref next, foot, null, out _), Is.True, $"step {i}");
            at = next;
        }
        Assert.That(mesh.Islands, Is.EqualTo(2), "the bot's island and the link's corner, once");
        var refused = new PlayerBotNetwork();
        Vector3 off = new(1_005, 50, 60);
        Assert.That(refused.Keeps(mesh, region, zone, top, ref off, new(1_016, 50, -188), null, out string crossing), Is.False);
        Assert.That((off, crossing), Is.EqualTo((new Vector3(1_005, 50, 60),
            "kind=link at=(980.0,50.0,0.0) to=(1005.0,50.0,60.0) island=1/57082->2/18 home=none")));
    }

    // Islands are per zone's navmesh: a step over the zone's border, or with no navmesh, is kept, and the bot's island
    // read anew after it.
    [Test]
    public void Over_a_zone_border_or_with_no_navmesh_every_step_is_kept()
    {
        var (region, zone) = World();
        var mesh = new PlayerBotIslandMesh();
        var network = new PlayerBotNetwork();
        Vector3 at = new(900, 50, 0), next = new(900, -10, 0);
        Assert.That(network.Keeps(mesh, region, zone, at, ref next, null, null, out string crossing), Is.True);
        Assert.That((crossing, network.On, mesh.Islands), Is.EqualTo(((string)null, (NavIsland?)null, 0)));
        mesh.Available = false;
        Vector3 onto = new(1_037, 50, 0);
        Assert.That(network.Keeps(mesh, region, zone, new(990, 50, 0), ref onto, null, null, out _), Is.True);
    }

    // The driver's ring points (OnMesh): a spot on another island is no spot to walk to, unless the step there would be
    // taken (the camp's island, or a larger one with no camp); with no navmesh, any spot.
    [Test]
    public void Spots_off_the_bots_island_are_not_accepted()
    {
        var (_, zone) = World();
        var mesh = new PlayerBotIslandMesh();
        var network = new PlayerBotNetwork();
        Vector3 here = new(900, 50, 0), camp = new(0, 50, 0);
        Assert.That(network.Accepts(mesh, zone, here, new(1_200, 50, 0), camp), Is.False, "under the dome");
        Assert.That(network.Accepts(mesh, zone, here, new(600, 300, 0), camp), Is.True, "on its island");
        Assert.That(network.Accepts(mesh, zone, new(1_100, 50, 0), new(600, 50, 0), camp), Is.True, "back home");
        Assert.That(network.Accepts(mesh, zone, new(1_100, 50, 0), new(600, 50, 0), null), Is.True, "no camp, a larger island");
        mesh.Available = false;
        Assert.That(network.Accepts(mesh, zone, here, new(1_200, 50, 0), camp), Is.True, "no navmesh");
    }

    // TryFloorStep's test (AtHome): off the camp's island, the floor step goes onto the nearest floor on it (rings 50
    // units apart, 8 directions); on it, or with no camp, the floor step is as before (none on floor with a way out).
    [Test]
    public void A_floor_step_off_the_camps_island_goes_onto_it()
    {
        var (_, zone) = World();
        var mesh = new PlayerBotIslandMesh();
        var network = new PlayerBotNetwork();
        Vector3 pocket = new(1_100, 50, 0), camp = new(0, 50, 0);
        Vector3? floor = PlayerBotPathMover.FloorStep(mesh, zone, pocket, spot => network.AtHome(mesh, zone, pocket, spot, camp));
        Assert.That(floor, Is.Not.Null);
        Assert.That((floor!.Value.X < mesh.Border, Vector3.Distance(pocket, floor.Value)), Is.EqualTo((true, 150f)).Using<(bool, float)>(
            (a, b) => a.Item1 == b.Item1 && Math.Abs(a.Item2 - b.Item2) < 0.01f));
        Assert.That(PlayerBotPathMover.FloorStep(mesh, zone, pocket), Is.Null, "no test: as before");
        Assert.That(PlayerBotPathMover.FloorStep(mesh, zone, pocket, spot => network.AtHome(mesh, zone, pocket, spot, null)), Is.Null,
            "no camp");
        Vector3 field = new(900, 50, 0);
        Assert.That(PlayerBotPathMover.FloorStep(mesh, zone, field, spot => network.AtHome(mesh, zone, field, spot, camp)), Is.Null,
            "on the camp's island");
    }
}
