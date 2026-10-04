using DotRecast.Detour;
using Navmesh;
using System.Numerics;

namespace Mnemosyne.Core;

// Push a route off the walls it hugs.
//
// Recast's cost is pure distance, so the cheapest path clips the corner of every obstacle it
// rounds. Geometrically optimal, practically unwalkable: the character has a body radius and
// a turning arc, so a waypoint flush against a pillar puts the body inside the pillar before
// the turn finishes. Measured on Ul'dah - Steps of Nald, 7 of 8 waypoints on a routine
// 38 m route sat at 0.00 m clearance.
//
// This is a post-process rather than a cost change on purpose: it works on meshes that are
// already built and cached, needs no rebuild, and cannot make a route worse â€” a waypoint is
// only moved when the move both increases its clearance and lands on the mesh.
public static class PathPadding
{
    /// <summary>Default breathing room in yalms. A body is ~0.5 y wide, so 1 y leaves half a
    /// body of slack for the turning arc.</summary>
    public const float DefaultPad = 1.0f;

    /// <summary>How far to search for a wall. Beyond this a point is clear by any measure and
    /// the query cost is wasted.</summary>
    private const float WallSearchRadius = 4f;

    /// <summary>Moving away from one wall can walk you into another, so re-solve a couple of
    /// times. It converges fast; more passes buy nothing.</summary>
    private const int Passes = 3;

    /// <summary>Closest two waypoints may sit. Padding a corner and then bending the leg beside it
    /// put pairs 0.4-0.6 m apart at every corner of Eulmore's ramps (2026-10-02): the follower
    /// turned sharply at each, zigzagged at the foot of the stairs and caught on the edge.</summary>
    private const float MinSpacing = 1f;

    /// <summary>What a merged leg must still clear: half a body, the same line the route
    /// diagnostics draw as "inside body radius".</summary>
    private const float MergeClearance = 0.5f;

    /// <summary>Widen the corridor a route takes, in place. Endpoints are never moved â€” the
    /// caller asked to arrive at a specific spot, and a padded goal is the wrong goal.
    /// Returns how many waypoints ended up moved.</summary>
    public static int Apply(DtNavMesh mesh, List<Vector3> waypoints, float pad = DefaultPad,
        IReadOnlyList<ObstacleShape>? obstacles = null)
    {
        if (pad <= 0 || waypoints.Count <= 2)
            return 0;

        var query = new DtNavMeshQuery(mesh);
        var filter = new DtQueryDefaultFilter();
        // A zone can carry thousands of obstacles once the service records the uncarved solids
        // it finds itself (8,807 in Amh Araeng), and padding asks for clearance thousands of times
        // per route: look them up by grid cell, not by scanning the list.
        var solids = new ObstacleIndex(obstacles ?? []);
        var moved = 0;

        for (int pass = 0; pass < Passes; ++pass)
        {
            var movedThisPass = 0;
            for (int i = 1; i < waypoints.Count - 1; ++i)
            {
                if (TryPad(query, filter, solids, waypoints[i], pad, out var padded))
                {
                    if (waypoints[i] != padded)
                        ++movedThisPass;
                    waypoints[i] = padded;
                }
            }
            moved = Math.Max(moved, movedThisPass);
            if (movedThisPass == 0)
                break;
        }

        moved += PadLegs(query, filter, solids, waypoints, pad);
        MergeCrowded(query, filter, solids, waypoints);
        return moved;
    }

    /// <summary>Drop a waypoint that sits within <see cref="MinSpacing"/> of the one before it,
    /// when the leg that replaces it still clears <see cref="MergeClearance"/>. Endpoints stay.</summary>
    private static void MergeCrowded(DtNavMeshQuery query, IDtQueryFilter filter,
        ObstacleIndex solids, List<Vector3> waypoints)
    {
        for (int i = 1; i < waypoints.Count - 1;)
        {
            if (Vector3.Distance(waypoints[i - 1], waypoints[i]) >= MinSpacing
                || LegClearance(query, filter, solids, waypoints[i - 1], waypoints[i + 1]) < MergeClearance)
            {
                ++i;
                continue;
            }
            waypoints.RemoveAt(i);
        }
    }

    private static float LegClearance(DtNavMeshQuery query, IDtQueryFilter filter,
        ObstacleIndex solids, Vector3 a, Vector3 b)
    {
        var steps = Math.Min(64, Math.Max(2, (int)(Vector3.Distance(a, b) / 0.25f)));
        var worst = float.MaxValue;
        for (int step = 1; step < steps; ++step)
        {
            var c = Clearance(query, filter, solids, Vector3.Lerp(a, b, step / (float)steps));
            if (c < 0)
                return -1; // leaves the mesh: never merge across that
            worst = MathF.Min(worst, c);
        }
        return worst;
    }

    /// <summary>Corners are not where a body spends its time. A leg can leave both its
    /// waypoints comfortably clear and still scrape a wall halfway along, which is precisely
    /// the "it swings too close" complaint â€” so find the worst point on each leg and bend the
    /// route around it by inserting a padded waypoint there.</summary>
    private static int PadLegs(DtNavMeshQuery query, IDtQueryFilter filter,
        ObstacleIndex solids, List<Vector3> waypoints, float pad)
    {
        const int MaxInsertions = 24; // a route that needs more than this is narrow everywhere
        var inserted = 0;

        for (int pass = 0; pass < Passes && inserted < MaxInsertions; ++pass)
        {
            var insertedThisPass = 0;
            for (int i = 1; i < waypoints.Count && inserted < MaxInsertions; ++i)
            {
                var a = waypoints[i - 1];
                var b = waypoints[i];
                var span = Vector3.Distance(a, b);
                if (span < 1f)
                    continue;

                var steps = Math.Min(32, Math.Max(2, (int)(span / 0.5f)));
                var worst = float.MaxValue;
                var worstT = 0f;
                for (int step = 1; step < steps; ++step)
                {
                    var t = step / (float)steps;
                    var c = Clearance(query, filter, solids, Vector3.Lerp(a, b, t));
                    if (c >= 0 && c < worst)
                    {
                        worst = c;
                        worstT = t;
                    }
                }
                if (worst >= pad || worst == float.MaxValue)
                    continue;

                // Bend the leg at its tightest point rather than everywhere along it: one
                // well-placed waypoint turns a scrape into an arc, and extra waypoints cost
                // the follower turns.
                var pinch = Vector3.Lerp(a, b, worstT);
                if (!TryPad(query, filter, solids, pinch, pad, out var relief))
                    continue;
                if (Clearance(query, filter, solids, relief) <= worst + 0.05f)
                    continue; // no real gain; leave the leg straight
                if (Vector3.Distance(relief, a) < MinSpacing || Vector3.Distance(relief, b) < MinSpacing)
                    continue; // hard by a corner already padded: a second turn there is a zigzag

                waypoints.Insert(i, relief);
                ++inserted;
                ++insertedThisPass;
                ++i; // skip past the point we just added
            }
            if (insertedThisPass == 0)
                break;
        }
        return inserted;
    }

    /// <summary>Clearance at a point: distance to the nearest thing you would walk into - a
    /// mesh boundary, or a recorded obstacle the mesh never carved. -1 when off the mesh.</summary>
    public static float Clearance(DtNavMeshQuery query, IDtQueryFilter filter, Vector3 p)
        => Clearance(query, filter, ObstacleIndex.Empty, p);

    public static float Clearance(DtNavMeshQuery query, IDtQueryFilter filter,
        IReadOnlyList<ObstacleShape> solids, Vector3 p)
        => Clearance(query, filter, new ObstacleIndex(solids), p);

    private static float Clearance(DtNavMeshQuery query, IDtQueryFilter filter,
        ObstacleIndex solids, Vector3 p)
    {
        query.FindNearestPoly(p.SystemToRecast(), new(2, 4, 2), filter, out var polyRef, out _, out _);
        if (polyRef == 0)
            return -1;
        if (query.FindDistanceToWall(polyRef, p.SystemToRecast(), WallSearchRadius, filter,
                out var dist, out _, out _).Failed())
            return -1;
        return MathF.Min(dist, ObstacleClearance(solids, p, out _));
    }

    /// <summary>Distance to the nearest recorded obstacle's surface, and which way to escape.
    /// Obstacles are treated as vertical cylinders - a lamppost is one, and a low wall is
    /// close enough once the audit has broken it into clusters.</summary>
    private static float ObstacleClearance(ObstacleIndex solids, Vector3 p, out Vector3 away)
    {
        away = Vector3.Zero;
        var best = float.MaxValue;
        foreach (var o in solids.Near(p, WallSearchRadius))
        {
            var centre = new Vector3(o.Center[0], o.Center[1], o.Center[2]);
            // only when we share its vertical span: a bollard under a bridge does not block
            // the bridge deck above it
            if (p.Y < centre.Y - 1f || p.Y > centre.Y + MathF.Max(o.Height, 1f))
                continue;
            var offset = new Vector3(p.X - centre.X, 0, p.Z - centre.Z);
            var distance = offset.Length() - o.Radius;
            if (distance >= best)
                continue;
            best = distance;
            away = offset.LengthSquared() > 1e-6f ? Vector3.Normalize(offset) : new Vector3(1, 0, 0);
        }
        return best;
    }

    private static bool TryPad(DtNavMeshQuery query, IDtQueryFilter filter,
        ObstacleIndex solids, Vector3 point, float pad, out Vector3 result)
    {
        result = point;
        query.FindNearestPoly(point.SystemToRecast(), new(2, 4, 2), filter, out var polyRef, out _, out _);
        if (polyRef == 0)
            return false;
        if (query.FindDistanceToWall(polyRef, point.SystemToRecast(), WallSearchRadius, filter,
                out var wallDist, out _, out var normal).Failed())
            return false;

        // Whichever is nearer decides where we go: the mesh edge, or an obstacle standing in
        // walkable mesh. The second kind is invisible to the mesh itself, and is exactly the
        // "the lamp post is green and pathable" case.
        var obstacleDist = ObstacleClearance(solids, point, out var fromObstacle);
        var dist = MathF.Min(wallDist, obstacleDist);
        if (dist >= pad)
            return true; // already clear

        // hitNormal points from the wall towards us, so it is the direction of escape. Flatten
        // it: pushing along Y would lift the route off the floor.
        var away = obstacleDist < wallDist ? fromObstacle : new Vector3(normal.X, 0, normal.Z);
        if (away.LengthSquared() < 1e-6f)
            return true; // no usable direction (enclosed); leave the point alone
        away = Vector3.Normalize(away);

        // Try the full correction first, then shorter ones. A narrow corridor cannot give the
        // full pad, but half of it is still better than none.
        foreach (var fraction in new[] { 1f, 0.66f, 0.33f })
        {
            var candidate = point + away * ((pad - dist) * fraction);
            // The moved point must stay on the mesh, and stay on *walkable* mesh - snapping
            // with a tight extent keeps it from teleporting onto a neighbouring surface.
            query.FindNearestPoly(candidate.SystemToRecast(), new(0.5f, 2, 0.5f), filter,
                out var newRef, out var snapped, out _);
            if (newRef == 0)
                continue;
            var landed = snapped.RecastToSystem();
            if (query.FindDistanceToWall(newRef, landed.SystemToRecast(), WallSearchRadius, filter,
                    out var newWall, out _, out _).Failed())
                continue;
            var newDist = MathF.Min(newWall, ObstacleClearance(solids, landed, out _));
            if (newDist <= dist)
                continue; // no better off; a move that does not help is a move that adds risk
            result = landed;
            return true;
        }
        return true;
    }

    /// <summary>Obstacles bucketed by 4 m grid cell, so a clearance query only looks at the
    /// handful near it. Beyond <see cref="WallSearchRadius"/> plus the largest radius nothing can
    /// matter: clearance is only ever compared with a wall distance searched that far.</summary>
    private sealed class ObstacleIndex
    {
        private const float Cell = 4f;
        public static readonly ObstacleIndex Empty = new([]);
        private readonly Dictionary<(int, int), List<ObstacleShape>> _cells = [];
        private readonly float _maxRadius;

        public ObstacleIndex(IReadOnlyList<ObstacleShape> obstacles)
        {
            foreach (var o in obstacles)
            {
                var key = ((int)MathF.Floor(o.Center[0] / Cell), (int)MathF.Floor(o.Center[2] / Cell));
                if (!_cells.TryGetValue(key, out var list))
                    _cells[key] = list = [];
                list.Add(o);
                _maxRadius = MathF.Max(_maxRadius, o.Radius);
            }
        }

        public IEnumerable<ObstacleShape> Near(Vector3 p, float reach)
        {
            if (_cells.Count == 0)
                yield break;
            var r = reach + _maxRadius;
            int x0 = (int)MathF.Floor((p.X - r) / Cell), x1 = (int)MathF.Floor((p.X + r) / Cell);
            int z0 = (int)MathF.Floor((p.Z - r) / Cell), z1 = (int)MathF.Floor((p.Z + r) / Cell);
            for (int x = x0; x <= x1; ++x)
                for (int z = z0; z <= z1; ++z)
                    if (_cells.TryGetValue((x, z), out var list))
                        foreach (var o in list)
                            yield return o;
        }
    }
}
