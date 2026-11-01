using System.Globalization;
using System.Numerics;

namespace DOL.GS.HearthDAoC.PlayerBots;

/// <summary>#188: keeps a player bot on the piece of navmesh it walks, its island (NavIsland: a strongly connected
/// component of the polygons, where a path leads each way). Every query of the mover starts from Detour's polygon nearest
/// the bot's point, so a step that leaves the bot off its floor can leave it on another piece, from which no walk leads
/// back: in Domnann (st-deathsE) a corridor's last corner, put at a rest spot on 18 polygons walled in under a dome, took
/// Pbothaaem 188 units straight down onto them, and in Muspelheim (st-deathsE2b) Pbotmaada stood on a ledge piece of
/// 10,551 polygons above its camp's 42,359 until the run ended, 654 walks blocked in 3.5 minutes; in the walks rebuilt
/// from that run, 428 of the 437 first steps off the camp's island were direct steps (the review of #188). Offline, 31
/// of 330,163 walks round the camps of spots-lm3 (frozen starts) ended so, by corridor steps (27) and floor steps (4).
/// A step may change island only onto the bot's camp's island (home), or, with no camp in the zone or a camp on a smaller
/// island than the bot's (a camp anchored on a pocket must not pull the bot onto it), onto a larger one; any other change
/// is refused, or the floor under the step on the bot's island taken instead.
/// A step that ends in the air (no polygon in the moves' box) changes nothing yet; on a floor link the link's far corner
/// is judged, once, from its foot (the steps up or down it hang in the air or snap to whatever floor lies near).</summary>
public sealed class PlayerBotNetwork
{
    public const string OffItsNetwork = "The step would leave the piece of navmesh the bot walks for one with no way back.";

    /// <summary>How far under a refused step its floor is looked for (the surface moves' reach, PlayerBotPathMover.FloorStep).</summary>
    public const int FloorBelow = 64;

    private Zone _zone;                     // where _on was read, and the spot it was read for (where the mover last put
    private Vector3 _at;                    //   the bot)
    private NavIsland? _on;                 // the island of the last floor the bot stood on (kept over a step in the air)
    private Zone _homeZone;                 // the home spot last read, and its island
    private Vector3 _homeAt;
    private NavIsland? _home;
    private Vector3? _linkAt;               // the far corner of the floor link last judged, and its island
    private NavIsland? _link;

    /// <summary>The island of the floor the bot last stood on, for the logs.</summary>
    public NavIsland? On => _on;

    /// <summary>Something else moved the bot (a teleport, a release, a floor step): where it stands is read anew.</summary>
    public void Reset() => (_zone, _on, _linkAt) = (null, null, null);

    /// <summary>Whether a bot on island from may stand on island to, its camp's island being home: on no polygon (to null)
    /// or the same island, yes; onto another, only its camp's, or, with none known or the camp's smaller than from, only a
    /// larger one; from nowhere known (from null), only its camp's, or, with none known, any.</summary>
    public static bool Accepts(NavIsland? from, NavIsland? to, NavIsland? home)
    {
        if (to is not NavIsland next || from is NavIsland was && next.Id == was.Id) return true;
        if (home is NavIsland camp && (from is not NavIsland on || camp.Polygons >= on.Polygons)) return next.Id == camp.Id;
        return from is not NavIsland smaller || next.Polygons > smaller.Polygons;
    }

    /// <summary>Whether the bot, standing at here, may stand at spot (a goal it would walk to: the driver's ring points).
    /// True where no island can be read.</summary>
    public bool Accepts(IPathfindingMgr nav, Zone zone, Vector3 here, Vector3 spot, Vector3? home)
    {
        if (zone == null || !nav.HasNavmesh(zone)) return true;
        Stand(nav, zone, here);
        return Accepts(_on, nav.GetIsland(zone, spot, nav.DefaultFilters), Home(nav, zone, home));
    }

    /// <summary>Whether the bot, standing at here, may be put on spot (FloorStep's test), as Keeps judges a step: where it
    /// may step onto its camp's island (Accepts), only there, so one standing off it is put back on it; else (no camp in
    /// the zone, or, the review of #188, a camp on a smaller island than the bot's, which must not pull the bot onto it)
    /// wherever Accepts lets it stand. True with no navmesh, or no polygon under spot.</summary>
    public bool AtHome(IPathfindingMgr nav, Zone zone, Vector3 here, Vector3 spot, Vector3? home)
    {
        if (zone == null || !nav.HasNavmesh(zone)) return true;
        Stand(nav, zone, here);
        if (nav.GetIsland(zone, spot, nav.DefaultFilters) is not NavIsland at) return true;
        NavIsland? camp = Home(nav, zone, home);
        return camp is NavIsland pulls && Accepts(_on, pulls, pulls) ? at.Id == pulls.Id : Accepts(_on, at, camp);
    }

    /// <summary>The mover's step from current to next (TryComputeNext's), before it is taken: true to take it (next, or
    /// the floor under it in its place), false when it is refused. crossing describes a change of island taken or
    /// refused, for PLAYER_BOT_CROSSING (null: none). linkEnd: the far corner of the floor link the step is on
    /// (PlayerTravelPath.LinkEnd); home: the bot's camp in its zone (null: none).</summary>
    public bool Keeps(IPathfindingMgr nav, Region region, Zone zone, Vector3 current, ref Vector3 next, Vector3? linkEnd,
        Vector3? home, out string crossing)
    {
        crossing = null;
        if (zone == null || !nav.HasNavmesh(zone) || region?.GetZone((int)next.X, (int)next.Y) != zone)
        {
            Reset();                                    // no navmesh, or over a zone's border: islands are per zone
            return true;
        }
        if (Vector3.DistanceSquared(current, next) < 1) return true;     // a still step
        Stand(nav, zone, current);
        NavIsland? from = _on, camp = Home(nav, zone, home);
        NavIsland? to = linkEnd is Vector3 end ? LinkIsland(nav, zone, end) : nav.GetIsland(zone, next, nav.DefaultFilters);
        if (Accepts(from, to, camp))
        {
            if (to is NavIsland t && from is NavIsland f && t.Id != f.Id) crossing = Describe(current, next, from, to, camp, "took");
            Took(next, to);
            return true;
        }
        // In its place, the floor under the step on an island it may stand on: the grounded move from where the bot stands,
        // else the floor straight under the step within the moves' reach (FloorBelow). In Muspelheim the live entry was
        // mostly a direct step whose plain move stood on the ledge's polygon about 40 units up (the rebuilt walk 4856);
        // the sweep's walk 7438, a corridor step that kept its line's height, stood 43 units over the camp's floor, where
        // the ledge piece is Detour's nearest polygon, and the grounded move found no floor there. Not on a link: its
        // steps hang in the air.
        if (linkEnd is null &&
            (Ground(nav, zone, current, nav.GetMoveAlongSurfaceLayered(zone, current, next, nav.DefaultFilters, true), from, camp) ??
             Ground(nav, zone, current, nav.GetClosestPoint(zone, next - new Vector3(0, 0, FloorBelow / 2f), 2, 2, FloorBelow / 2f,
                 nav.DefaultFilters), from, camp)) is (Vector3 floor, NavIsland under))
        {
            crossing = Describe(current, next, from, to, camp, "ground");
            next = floor;
            Took(floor, under);
            return true;
        }
        crossing = Describe(current, next, from, to, camp, linkEnd is null ? "step" : "link");
        return false;
    }

    // A floor in a refused step's place: one that moves the bot, on an island it may stand on.
    private static (Vector3, NavIsland)? Ground(IPathfindingMgr nav, Zone zone, Vector3 current, Vector3? floor, NavIsland? from,
        NavIsland? camp)
    {
        if (floor is not Vector3 at || Vector3.DistanceSquared(current, at) < 1) return null;
        return nav.GetIsland(zone, at, nav.DefaultFilters) is NavIsland under && Accepts(from, under, camp) ? (at, under) : null;
    }

    // Where the bot stands: read when the mover did not put it there (_at), else the island kept from its last step.
    private void Stand(IPathfindingMgr nav, Zone zone, Vector3 here)
    {
        if (_zone == zone && Vector3.DistanceSquared(_at, here) <= 1) return;
        NavIsland? read = nav.GetIsland(zone, here, nav.DefaultFilters);
        _on = read ?? (_zone == zone ? _on : null);    // in the air: the floor it last stood on
        (_zone, _at) = (zone, here);
    }

    private void Took(Vector3 next, NavIsland? to)
    {
        _at = next;
        if (to is NavIsland island) _on = island;
    }

    // The camp's island: its own polygon's, else the one Detour's corridor would start from (PTP:80's box).
    private NavIsland? Home(IPathfindingMgr nav, Zone zone, Vector3? home)
    {
        if (home is not Vector3 at) return null;
        if (_homeZone != zone || _homeAt != at)
        {
            (_homeZone, _homeAt) = (zone, at);
            _home = nav.GetIsland(zone, at, nav.DefaultFilters) ??
                    (nav.GetClosestPoint(zone, at, 64, 64, 128, nav.DefaultFilters) is Vector3 near ? nav.GetIsland(zone, near, nav.DefaultFilters) : null);
        }
        return _home;
    }

    private NavIsland? LinkIsland(IPathfindingMgr nav, Zone zone, Vector3 end)
    {
        if (_linkAt != end) (_linkAt, _link) = (end, nav.GetIsland(zone, end, nav.DefaultFilters));
        return _link;
    }

    private static string Describe(Vector3 from, Vector3 to, NavIsland? was, NavIsland? onto, NavIsland? camp, string kind) =>
        string.Create(CultureInfo.InvariantCulture,
            $"kind={kind} at=({from.X:0.0},{from.Y:0.0},{from.Z:0.0}) to=({to.X:0.0},{to.Y:0.0},{to.Z:0.0}) " +
            $"island={Name(was)}->{Name(onto)} home={Name(camp)}");

    /// <summary>An island for the logs: id/polygons, or none.</summary>
    public static string Name(NavIsland? island) => island is NavIsland i ? $"{i.Id}/{i.Polygons}" : "none";
}
