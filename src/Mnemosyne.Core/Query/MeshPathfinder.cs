using DotRecast.Detour;
using Navmesh;
using System.Numerics;

namespace Mnemosyne.Core;

// Shared walk pathfinding used by both the service and the viewer, including custom-link
// stitching: override links are virtual off-mesh connections resolved at query time
// (path to link start + jump + path from link end), since injecting real off-mesh
// connections into an already-built DtNavMesh isn't supported by the runtime.
public sealed class MeshPathfinder(DtNavMesh mesh)
{
    private readonly DtNavMeshQuery _query = new(mesh);
    private readonly IDtQueryFilter _filter = new DtQueryDefaultFilter();
    private static readonly DotRecast.Core.Numerics.RcVec3f SnapExtents = new(5, 5, 5);

    public sealed record WalkResult(List<Vector3> Waypoints, bool Partial, int PolyCount);

    /// <summary>Nearest walkable surface point, searching far vertically (landing spots).</summary>
    public bool TryNearestGround(Vector3 p, float verticalRange, out Vector3 ground)
    {
        ground = p;
        var extents = new DotRecast.Core.Numerics.RcVec3f(20, verticalRange, 20);
        _query.FindNearestPoly(p.SystemToRecast(), extents, _filter, out var polyRef, out var pt, out _);
        if (polyRef == 0)
            return false;
        ground = pt.RecastToSystem();
        return true;
    }

    /// <summary><paramref name="filter"/> overrides the default walkability filter — used
    /// for Nav.PathfindAvoid, where polys near a hazard are rejected. Endpoint snapping
    /// deliberately keeps the default filter: a start or goal inside the avoid circle must
    /// still resolve to a poly, or the search has nowhere to begin.</summary>
    public WalkResult? FindWalkPath(Vector3 from, Vector3 to, IDtQueryFilter? filter = null)
    {
        _query.FindNearestPoly(from.SystemToRecast(), SnapExtents, _filter, out var startRef, out var startPt, out _);
        _query.FindNearestPoly(to.SystemToRecast(), SnapExtents, _filter, out var endRef, out var endPt, out _);
        if (startRef == 0 || endRef == 0)
            return null;

        var polys = new List<long>();
        _query.FindPath(startRef, endRef, startPt, endPt, filter ?? _filter, ref polys, new(DtDefaultQueryHeuristic.Default, 0, 0));
        if (polys.Count == 0)
            return null;

        var straight = new List<DtStraightPath>();
        var status = _query.FindStraightPath(startPt, endPt, polys, ref straight, 1024, 0);
        if (status.Failed())
            return null;

        return new([.. straight.Select(w => w.pos.RecastToSystem())], polys[^1] != endRef, polys.Count);
    }

    // Try direct first; when it fails or is partial, route through the custom links and return
    // the shortest complete route.
    //
    // Links chain. The first version tried exactly one link per route (walk, jump, walk), which
    // is enough for a single door but not for a dungeon: Mistwake needs a slide and then a ledge
    // down onto the walkway, in that order, and with one link per route neither alone reaches
    // the end - so the route stopped at the top of the slide however many links were recorded.
    //
    // So this is Dijkstra over link exits. A node is "standing at the far end of link i" (or at
    // the start); from each node we walk to the goal or to the near end of another link. Walk
    // legs are computed lazily and only from settled nodes, so a zone with a handful of links
    // costs a handful of extra queries, and a route that needs none never gets here.
    public WalkResult? FindWalkPath(Vector3 from, Vector3 to, IReadOnlyList<OverrideLink> links, IDtQueryFilter? filter = null)
    {
        var direct = FindWalkPath(from, to, filter);
        if (links.Count == 0 || direct is { Partial: false })
            return direct;

        var hops = links.SelectMany(LinkDirections).ToList();
        // node 0 = start; node i+1 = exit of hop i; node hops.Count+1 = goal
        int nodeCount = hops.Count + 2, goal = nodeCount - 1;
        Vector3 At(int node) => node == 0 ? from : hops[node - 1].B;

        var cost = Enumerable.Repeat(float.PositiveInfinity, nodeCount).ToArray();
        var previous = new int[nodeCount];
        var legInto = new WalkResult?[nodeCount]; // the walk that reached this node's hop entry (or the goal)
        var settled = new bool[nodeCount];
        cost[0] = 0;

        var open = new PriorityQueue<int, float>();
        open.Enqueue(0, 0);
        while (open.TryDequeue(out var node, out var nodeCost))
        {
            if (settled[node] || nodeCost > cost[node])
                continue;
            settled[node] = true;
            if (node == goal)
                break;

            var here = At(node);
            if (FindWalkPath(here, to, filter) is { Partial: false } home)
                Relax(goal, Length(home.Waypoints), home);
            for (int h = 0; h < hops.Count; ++h)
            {
                if (settled[h + 1] || h + 1 == node)
                    continue;
                if (FindWalkPath(here, hops[h].A, filter) is not { Partial: false } leg)
                    continue;
                // the hop itself costs its straight-line length: a slide or a drop is travel too
                Relax(h + 1, Length(leg.Waypoints) + Vector3.Distance(hops[h].A, hops[h].B), leg);
            }

            void Relax(int target, float stepCost, WalkResult leg)
            {
                if (nodeCost + stepCost >= cost[target])
                    return;
                cost[target] = nodeCost + stepCost;
                previous[target] = node;
                legInto[target] = leg;
                open.Enqueue(target, cost[target]);
            }
        }

        if (!settled[goal])
            return direct; // no chain of links completes it either: keep the honest partial

        // walk the chain back, then stitch: each leg ends at a hop entry, and the next leg starts
        // at that hop's exit - which is exactly the jump the follower has to make
        var chain = new List<int>();
        for (int n = goal; n != 0; n = previous[n])
            chain.Add(n);
        chain.Reverse();

        var waypoints = new List<Vector3>();
        var polys = 0;
        foreach (var n in chain)
        {
            var leg = legInto[n]!;
            // a leg starts where the last hop landed, snapped onto the mesh - drop that repeat
            // rather than hand the follower a zero-length step
            var repeatsLast = waypoints.Count > 0 && Vector3.Distance(waypoints[^1], leg.Waypoints[0]) < 0.75f;
            waypoints.AddRange(repeatsLast ? leg.Waypoints.Skip(1) : leg.Waypoints);
            polys += leg.PolyCount;
            if (n != goal)
                waypoints.Add(hops[n - 1].B);
        }
        return new WalkResult(waypoints, false, polys);
    }

    private static IEnumerable<(Vector3 A, Vector3 B)> LinkDirections(OverrideLink link)
    {
        var from = new Vector3(link.From[0], link.From[1], link.From[2]);
        var to = new Vector3(link.To[0], link.To[1], link.To[2]);
        yield return (from, to);
        if (link.Bidirectional)
            yield return (to, from);
    }

    private static float Length(List<Vector3> path)
    {
        float length = 0;
        for (int i = 1; i < path.Count; ++i)
            length += Vector3.Distance(path[i - 1], path[i]);
        return length;
    }
}
