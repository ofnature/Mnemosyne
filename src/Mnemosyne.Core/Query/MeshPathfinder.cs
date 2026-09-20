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

    /// <summary>Search budget, in A* node expansions. Sized from measurement, not taste: the
    /// old disconnected-goal search spent 7.6 s exhausting ~90k polys on the Yak T'el field
    /// zone, so an expansion costs roughly 85 µs and 25k of them is the ~2 s worth paying for a
    /// consumer that is standing still (PLAN item 6). A *real* route is nowhere near it —
    /// A* on a good heuristic expands hundreds of polys for a kilometre of walking — so what
    /// this bounds is the pathological case: proving a no-route the islands could not, or a
    /// search that has to sweep a component the goal is not in. Stopping early yields the best
    /// partial route plus `budgetExhausted`, which is a different and more honest claim than
    /// `noRouteOnMesh`.</summary>
    public const int DefaultStepBudget = 25_000;

    /// <summary>Expansions per sliced-search call. Small enough that the budget check runs
    /// often, large enough not to matter next to the search itself.</summary>
    private const int SliceSize = 4096;

    public sealed record WalkResult(List<Vector3> Waypoints, bool Partial, int PolyCount,
        bool BudgetExhausted = false, Vector3? DisconnectedTo = null);

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
    public WalkResult? FindWalkPath(Vector3 from, Vector3 to, IDtQueryFilter? filter = null,
        int stepBudget = DefaultStepBudget)
    {
        _query.FindNearestPoly(from.SystemToRecast(), SnapExtents, _filter, out var startRef, out var startPt, out _);
        _query.FindNearestPoly(to.SystemToRecast(), SnapExtents, _filter, out var endRef, out var endPt, out _);
        if (startRef == 0 || endRef == 0)
            return null;

        var (polys, exhausted) = FindPolys(startRef, endRef, startPt, endPt, filter ?? _filter, stepBudget);
        if (polys.Count == 0)
            return null;

        var straight = new List<DtStraightPath>();
        var status = _query.FindStraightPath(startPt, endPt, polys, ref straight, 1024, 0);
        if (status.Failed())
            return null;

        return new([.. straight.Select(w => w.pos.RecastToSystem())], polys[^1] != endRef, polys.Count, exhausted);
    }

    /// <summary>Sliced A* with a step budget. Detour's sliced search remembers its best node,
    /// so stopping early still yields a usable route toward the goal — and `exhausted` is set
    /// only when the *budget* stopped it, never when the search drained its own open list:
    /// that is a proof, and the caller reports it as one.</summary>
    private (List<long> Polys, bool Exhausted) FindPolys(long startRef, long endRef,
        DotRecast.Core.Numerics.RcVec3f startPt, DotRecast.Core.Numerics.RcVec3f endPt,
        IDtQueryFilter filter, int stepBudget)
    {
        var polys = new List<long>();
        if (_query.InitSlicedFindPath(startRef, endRef, startPt, endPt, filter, 0).Failed())
            return (polys, false);

        var spent = 0;
        var exhausted = false;
        while (true)
        {
            var slice = Math.Min(SliceSize, stepBudget - spent);
            if (slice <= 0)
            {
                exhausted = true;
                break;
            }
            var status = _query.UpdateSlicedFindPath(slice, out var done);
            spent += done;
            if (status.Failed())
                return (polys, false);
            if (!status.InProgress())
                break;
        }

        if (_query.FinalizeSlicedFindPath(ref polys).Failed())
            polys.Clear();
        return (polys, exhausted);
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
    public WalkResult? FindWalkPath(Vector3 from, Vector3 to, IReadOnlyList<OverrideLink> links,
        IDtQueryFilter? filter = null, int stepBudget = DefaultStepBudget)
    {
        // Connectivity before cost. When `to` sits in another island, no amount of searching
        // will reach it, and a goal with no route is the case that must answer *fastest*: the
        // islands are one pass over the mesh, cached per loaded zone, so this costs
        // milliseconds where the exhaustive search cost seconds past a consumer's timeout.
        if (TryDisconnected(from, to, links, out var nearestReachable))
        {
            var toward = FindWalkPath(from, nearestReachable, filter, stepBudget);
            return new WalkResult(toward?.Waypoints ?? [], Partial: true,
                toward?.PolyCount ?? 0, toward?.BudgetExhausted ?? false,
                DisconnectedTo: nearestReachable);
        }

        var direct = FindWalkPath(from, to, filter, stepBudget);
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
            if (FindWalkPath(here, to, filter, stepBudget) is { Partial: false } home)
                Relax(goal, Length(home.Waypoints), home);
            for (int h = 0; h < hops.Count; ++h)
            {
                if (settled[h + 1] || h + 1 == node)
                    continue;
                if (FindWalkPath(here, hops[h].A, filter, stepBudget) is not { Partial: false } leg)
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

    // ---- islands: which walkable polys can reach which -------------------------------------

    private Dictionary<long, int>? _islandOf;
    private List<HashSet<long>>? _islandPolys;

    /// <summary>Nearest walkable poly to a point, by the same snap the searches use.</summary>
    public long NearestPolyRef(Vector3 p)
    {
        _query.FindNearestPoly(p.SystemToRecast(), SnapExtents, _filter, out var polyRef, out _, out _);
        return polyRef;
    }

    /// <summary>Label every walkable poly with its connected component, once per mesh. Override
    /// links are deliberately not baked in: they arrive per query and there are only ever a
    /// handful, so <see cref="UnionIslands"/> merges the few islands they bridge on top.</summary>
    private void BuildIslands()
    {
        if (_islandOf != null)
            return;

        var islands = new List<HashSet<long>>();
        var map = new Dictionary<long, int>();
        // Flood (the reachableCells component walk) is the same traversal `components` reports,
        // so an island here and an island there are the same island.
        var flood = new ReachableCellsQuery(mesh, _filter, []);
        for (var t = 0; t < mesh.GetMaxTiles(); ++t)
        {
            var tile = mesh.GetTile(t);
            var data = tile?.data;
            if (data?.header == null)
                continue;

            var refBase = mesh.GetPolyRefBase(tile);
            for (var p = 0; p < data.header.polyCount; ++p)
            {
                var poly = data.polys[p];
                if (poly.GetPolyType() != 0)
                    continue; // an off-mesh connection is a line, not walkable area
                var polyRef = refBase | (uint)p;
                if (map.ContainsKey(polyRef) || !_filter.PassFilter(polyRef, tile, poly))
                    continue;

                var component = flood.Flood(polyRef);
                var id = islands.Count;
                islands.Add(component);
                foreach (var member in component)
                    map[member] = id;
            }
        }

        _islandPolys = islands;
        _islandOf = map;
    }

    /// <summary>Union-find over island ids, seeded with a bridge per override link: a link says
    /// "you can get from A to B even though the mesh disagrees", so the islands holding its two
    /// ends are one island in practice.</summary>
    private Dictionary<int, int> UnionIslands(IReadOnlyList<OverrideLink> links)
    {
        var parent = new Dictionary<int, int>();
        if (links.Count == 0 || _islandOf is not { } map)
            return parent;

        int Root(int x)
        {
            var r = x;
            while (parent.TryGetValue(r, out var up) && up != r)
                r = up;
            while (parent.TryGetValue(x, out var up2) && up2 != r)
                (parent[x], x) = (r, up2);
            return r;
        }
        void Union(int a, int b)
        {
            a = Root(a);
            b = Root(b);
            if (a != b)
                parent[b] = a;
        }

        foreach (var link in links)
        {
            var aRef = NearestPolyRef(new Vector3(link.From[0], link.From[1], link.From[2]));
            var bRef = NearestPolyRef(new Vector3(link.To[0], link.To[1], link.To[2]));
            if (aRef != 0 && bRef != 0 && map.TryGetValue(aRef, out var ai) && map.TryGetValue(bRef, out var bi))
                Union(ai, bi);
        }
        return parent;
    }

    private int IslandRoot(Dictionary<int, int> parent, int id)
    {
        while (parent.TryGetValue(id, out var up) && up != id)
            id = up;
        return id;
    }

    /// <summary>Is `to` in a different island from `from`, links included? True means no search
    /// can reach it, and <paramref name="nearest"/> is the closest ground the start *can* reach
    /// — which is the useful half of that answer, since walking as far as possible is usually
    /// what the caller wants anyway.</summary>
    private bool TryDisconnected(Vector3 from, Vector3 to, IReadOnlyList<OverrideLink> links, out Vector3 nearest)
    {
        nearest = to;
        BuildIslands();
        var map = _islandOf!;
        if (map.Count == 0)
            return false; // no walkable polys at all: not an island question

        var startRef = NearestPolyRef(from);
        var endRef = NearestPolyRef(to);
        if (startRef == 0 || endRef == 0)
            return false; // one end is off-mesh; other answers own that
        if (!map.TryGetValue(startRef, out var startIsland) || !map.TryGetValue(endRef, out var endIsland))
            return false;

        if (startIsland == endIsland)
            return false;

        var parent = UnionIslands(links);
        if (IslandRoot(parent, startIsland) == IslandRoot(parent, endIsland))
            return false; // a link bridges them: let the search decide

        nearest = NearestPointInIsland(parent, startIsland, to);
        return true;
    }

    /// <summary>Closest walkable point, to <paramref name="to"/>, among every island merged
    /// with the start's. Cheap poly centres pick the handful of candidates; the exact
    /// closest-point pass runs on those, so the answer is a real point on a real poly
    /// rather than a poly centroid in the middle of a wall.</summary>
    private Vector3 NearestPointInIsland(Dictionary<int, int> parent, int startIsland, Vector3 to)
    {
        var root = IslandRoot(parent, startIsland);
        var cheap = new List<(float Dist, long Ref, DtMeshData Data, DtPoly Poly)>();

        for (var i = 0; i < _islandPolys!.Count; ++i)
        {
            if (IslandRoot(parent, i) != root)
                continue;
            foreach (var polyRef in _islandPolys[i])
            {
                mesh.GetTileAndPolyByRefUnsafe(polyRef, out var tile, out var poly);
                var data = tile.data;
                if (data == null)
                    continue;
                float cx = 0, cz = 0;
                for (var v = 0; v < poly.vertCount; ++v)
                {
                    var at = poly.verts[v] * 3;
                    cx += data.verts[at];
                    cz += data.verts[at + 2];
                }
                cx /= poly.vertCount;
                cz /= poly.vertCount;
                var dx = cx - to.X;
                var dz = cz - to.Z;
                cheap.Add((dx * dx + dz * dz, polyRef, data, poly));
            }
        }

        if (cheap.Count == 0)
            return to;

        cheap.Sort(static (a, b) => a.Dist.CompareTo(b.Dist));
        var best = to;
        var bestDist = float.PositiveInfinity;
        var considered = Math.Min(8, cheap.Count);
        for (var i = 0; i < considered; ++i)
        {
            var cand = cheap[i];
            if (_query.ClosestPointOnPoly(cand.Ref, to.SystemToRecast(), out var closest, out _).Failed())
                continue;
            var point = closest.RecastToSystem();
            var dist = Vector3.Distance(point, to);
            if (dist < bestDist)
            {
                bestDist = dist;
                best = point;
            }
        }
        return best;
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
