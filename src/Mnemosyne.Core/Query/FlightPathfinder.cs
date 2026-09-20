using Navmesh.NavVolume;
using System.Numerics;

namespace Mnemosyne.Core;

// Layered flight planner (PLAN 9b), per the volumetric-pathing handoff:
//   1. greedy raycast: exact voxel line-of-sight straight to the goal (answers most
//      open-sky hops in ~ms);
//   2. coarse corridor: Lazy Theta* (Nash/Koenig/Tovey AAAI'10, <=1 LOS per expansion)
//      over the FlightNav octree, whose empty leaves are guaranteed clear at full
//      resolution - long hops cross a handful of large nodes;
//   3. escape legs: endpoints connect to the coarse graph via exact voxel LOS, so
//      near-terrain starts/goals still work.
// Any stage failing returns [] and the caller falls back to the fine-grained
// VoxelPathfind - correctness never depends on the coarse graph.
public sealed class FlightPathfinder(VoxelMap volume)
{
    public FlightNav Nav { get; } = new(volume);

    private readonly VoxelMap _volume = volume;

    // per-leaf search state, epoch-stamped so repeated queries reuse the arrays
    private int[] _epochOf = [];
    private float[] _g = [];
    private int[] _parent = [];
    private bool[] _closed = [];
    private int _epoch;

    private readonly List<(float F, int Node)> _open = []; // binary heap with lazy deletion
    private readonly List<int> _neighborScratch = [];
    private readonly List<int> _entryScratch = [];

    public string LastOutcome { get; private set; } = "";

    public List<Vector3> FindPath(Vector3 from, Vector3 to, out bool partial, int maxExpansions = 20_000) // successes measure <200 expansions; the cap only bounds time wasted proving unreachability before fallback
    {
        partial = false;

        // normalize endpoints into empty voxels first (same 3 m snap the fine engine uses)
        if (!SnapToEmpty(ref from) || !SnapToEmpty(ref to))
        {
            LastOutcome = "endpoint snap failed";
            return [];
        }

        // stage 1: straight shot (exact voxel LOS)
        if (VoxelLineOfSight(from, to))
        {
            LastOutcome = "direct LOS";
            return [from, to];
        }

        // stage 3 first (cheap): connect both endpoints to the coarse graph. Multiple
        // candidates per endpoint - a single entry can sit in a coarse-disconnected pocket
        var starts = FindEntries(from);
        var goals = FindEntries(to);
        if (starts.Count == 0 || goals.Count == 0)
        {
            LastOutcome = $"no graph entry ({(starts.Count == 0 ? "start" : "goal")})";
            return []; // caller falls back to the fine pathfinder
        }
        var goalSet = new HashSet<int>(goals);

        // stage 2: coarse any-angle corridor
        var nodes = Nav.Nodes;
        EnsureCapacity(nodes.Length);
        ++_epoch;
        _open.Clear();

        foreach (var s0 in starts)
        {
            Visit(s0);
            _g[s0] = Vector3.Distance(from, nodes[s0].Center); // escape-leg cost
            _parent[s0] = s0;
            Push(_g[s0] + Heuristic(nodes[s0].Center, to), s0);
        }

        int best = starts[0];
        float bestH = float.MaxValue;
        int expansions = 0;
        bool reached = false;
        int reachedGoal = -1;

        while (_open.Count > 0 && expansions < maxExpansions)
        {
            int s = Pop();
            if (s < 0)
                break;
            if (_closed[s])
                continue;

            // lazy fixup: parent links are assigned without LOS; verify on pop, and reroute
            // through the best closed neighbor when the assumed shortcut fails
            if (_parent[s] != s)
            {
                int p = _parent[s];
                if (!Nav.LineOfSight(nodes[p].Center, nodes[s].Center))
                {
                    // reroute through the best closed neighbor. Adjacent center-to-center hops
                    // can corner-clip on variable-size boxes, so prefer an LOS-verified one -
                    // but NEVER drop the node (its recorded g would poison future relaxations);
                    // unverified adjacent edges are repaired at reconstruction via the shared
                    // face point, which is provably clear by convexity of the two empty boxes.
                    float bgVerified = float.MaxValue, bgAny = float.MaxValue;
                    int bpVerified = -1, bpAny = -1;
                    foreach (var nb in Neighbors(s))
                    {
                        if (_epochOf[nb] != _epoch || !_closed[nb])
                            continue;
                        float cand = _g[nb] + Vector3.Distance(nodes[nb].Center, nodes[s].Center);
                        if (cand < bgAny)
                        {
                            bgAny = cand;
                            bpAny = nb;
                        }
                        if (cand < bgVerified && Nav.LineOfSight(nodes[nb].Center, nodes[s].Center))
                        {
                            bgVerified = cand;
                            bpVerified = nb;
                        }
                    }
                    if (bpVerified >= 0)
                    {
                        _parent[s] = bpVerified;
                        _g[s] = bgVerified;
                    }
                    else if (bpAny >= 0)
                    {
                        _parent[s] = bpAny;
                        _g[s] = bgAny;
                    }
                    else
                    {
                        continue; // discovered by a neighbor that is somehow gone - drop safely
                    }
                }
            }

            _closed[s] = true;
            ++expansions;

            if (goalSet.Contains(s))
            {
                reached = true;
                reachedGoal = s;
                break;
            }

            float h = Heuristic(nodes[s].Center, to);
            if (h < bestH)
            {
                bestH = h;
                best = s;
            }

            var sPos = nodes[s].Center;
            int sParent = _parent[s];
            var pPos = nodes[sParent].Center;
            float gParent = _g[sParent];
            foreach (var t in Neighbors(s))
            {
                if (_epochOf[t] == _epoch && _closed[t])
                    continue;
                var tPos = nodes[t].Center;
                // lazy theta*: assume LOS from s's parent without checking (verified when t pops)
                float viaParent = gParent + Vector3.Distance(pPos, tPos);
                float viaS = _g[s] + Vector3.Distance(sPos, tPos);
                var (candidateG, candidateParent) = viaParent <= viaS ? (viaParent, sParent) : (viaS, s);
                if (_epochOf[t] != _epoch)
                    Visit(t);
                else if (candidateG >= _g[t])
                    continue;
                _g[t] = candidateG;
                _parent[t] = candidateParent;
                Push(candidateG + Heuristic(tPos, to), t);
            }
        }

        int endNode = reached ? reachedGoal : best;
        LastOutcome = $"{(reached ? "reached" : "partial")}, {expansions} expansions, open {_open.Count}";
        if (!reached)
        {
            // coarse partial paths are worse than the fine engine's - let the caller fall back
            partial = true;
            return [];
        }

        // chain of coarse nodes, start -> endNode
        var nodeChain = new List<int>();
        for (int node = endNode; ; node = _parent[node])
        {
            nodeChain.Add(node);
            if (_parent[node] == node)
                break;
        }
        nodeChain.Reverse();

        // positions, repairing any unverified adjacency edge with its shared-face point:
        // center -> face point -> center stays inside the two empty boxes by convexity
        var chain = new List<Vector3> { Nav.Nodes[nodeChain[0]].Center };
        for (int i = 1; i < nodeChain.Count; ++i)
        {
            var a = Nav.Nodes[nodeChain[i - 1]].Center;
            var b = Nav.Nodes[nodeChain[i]].Center;
            if (!Nav.LineOfSight(a, b))
                chain.Add(SharedFacePoint(nodeChain[i - 1], nodeChain[i]));
            chain.Add(b);
        }

        // smoothing: drop points whose neighbors see each other (coarse LOS keeps clearance)
        var waypoints = new List<Vector3> { from };
        int anchor = 0;
        for (int i = 1; i < chain.Count - 1; ++i)
            if (!Nav.LineOfSight(chain[anchor], chain[i + 1]))
            {
                waypoints.Add(chain[i]);
                anchor = i;
            }
        waypoints.Add(chain[^1]);
        waypoints.Add(to);
        return waypoints;
    }

    // ---- naming a point the flight can certainly reach ------------------------------------------

    /// <summary>Closest point the flight can *certainly* reach from <paramref name="from"/>,
    /// measured to <paramref name="to"/>: a bounded breadth-first walk over the coarse graph
    /// from the start's entry leaves, returning the nearest centre it visited. Anything visited
    /// is provably reachable — the coarse graph's empty leaves are clear at full resolution — so
    /// this can only ever understate, never lie. That is what a failed fly search can still tell
    /// a consumer: "how close can I get", which the caller's walk fallback then completes.
    ///
    /// A bounded walk and not a full component map, deliberately. The octree has no cheap
    /// neighbour lookup — `CollectFaceNeighbors` probes the tree spatially, per direction — so
    /// labelling every leaf of a field zone's octree costs seconds: measured, a cold fly request
    /// went from 1,434 ms to 16,066 ms with the full map in its path. A failed search only needs
    /// a good-enough point nearby, and `maxVisits` bounds this the same way the search's budget
    /// bounds the search.</summary>
    public Vector3? NearestReachable(Vector3 from, Vector3 to, int maxVisits = 1_200)
    {
        // The caller's point can sit inside a solid voxel - a floor surface is exactly that - and
        // every entry test from there fails (VoxelLineOfSight wants empty space), which silently
        // produced "no nearest at all" for the plane's own takeoff position. FindPath snaps its
        // endpoints for this reason; do the same here, and keep the raw point when the snap fails.
        var p = from;
        SnapToEmpty(ref p);
        var starts = FindEntries(p);
        if (starts.Count == 0)
            return null;

        var nodes = Nav.Nodes;
        var seen = new HashSet<int>(starts);
        var queue = new Queue<int>(starts);
        var scratch = new List<int>();
        Vector3? best = null;
        var bestDist = float.MaxValue;
        var visits = 0;

        while (queue.Count > 0 && visits < maxVisits)
        {
            var node = queue.Dequeue();
            ++visits;

            var dist = Vector3.DistanceSquared(nodes[node].Center, to);
            if (dist < bestDist)
            {
                bestDist = dist;
                best = nodes[node].Center;
            }

            for (var dir = 0; dir < 6; ++dir)
            {
                scratch.Clear();
                Nav.CollectFaceNeighbors(node, dir, scratch);
                foreach (var neighbour in scratch)
                {
                    if (neighbour < 0 || neighbour >= nodes.Length || nodes[neighbour].State != FlightNav.StateEmpty)
                        continue;
                    if (seen.Add(neighbour))
                        queue.Enqueue(neighbour);
                }
            }
        }
        return best;
    }

    private static float Heuristic(Vector3 a, Vector3 b) => Vector3.Distance(a, b) * 1.7f; // weighted: narrows the search corridor; closed nodes never reopen so no re-expansion churn, and any-angle smoothing recovers path quality

    // coarse empty leaves reachable from p by an exact-LOS escape leg (up to MaxEntries,
    // spread across the search rings so they don't all land in the same pocket)
    private const int MaxEntries = 6;

    private List<int> FindEntries(Vector3 p)
    {
        var entries = new List<int>();
        int direct = Nav.LeafAt(p);
        if (direct >= 0 && Nav.Nodes[direct].State == FlightNav.StateEmpty)
            entries.Add(direct);

        for (float radius = 16; radius <= 128 && entries.Count < MaxEntries; radius *= 2)
        {
            _entryScratch.Clear();
            Nav.CollectEmptyInBox(p - new Vector3(radius), p + new Vector3(radius), _entryScratch);
            foreach (var leaf in _entryScratch.OrderBy(l => Vector3.DistanceSquared(Nav.Nodes[l].Center, p)).Take(24))
            {
                if (entries.Count >= MaxEntries)
                    break;
                if (entries.Contains(leaf))
                    continue;
                if (VoxelLineOfSight(p, Nav.Nodes[leaf].Center))
                    entries.Add(leaf);
            }
        }
        return entries;
    }

    // center of the (degenerate) overlap box where two face-adjacent leaves touch
    private Vector3 SharedFacePoint(int a, int b)
    {
        var na = Nav.Nodes[a];
        var nb = Nav.Nodes[b];
        var lo = Vector3.Max(na.Center - na.Half, nb.Center - nb.Half);
        var hi = Vector3.Min(na.Center + na.Half, nb.Center + nb.Half);
        return (lo + hi) * 0.5f;
    }

    private bool SnapToEmpty(ref Vector3 p)
    {
        var voxel = VoxelSearch.FindNearestEmptyVoxel(_volume, p, new Vector3(3, 3, 3));
        if (voxel == VoxelMap.InvalidVoxel)
            return false;
        p = VoxelSearch.FindClosestVoxelPoint(_volume, voxel, p);
        return true;
    }

    // exact full-resolution check against the raw voxel map (endpoints already in empty space)
    private bool VoxelLineOfSight(Vector3 a, Vector3 b)
    {
        var va = _volume.RootTile.FindLeafVoxel(a);
        var vb = _volume.RootTile.FindLeafVoxel(b);
        if (!va.empty || !vb.empty)
            return false;
        return VoxelSearch.LineOfSight(_volume, va.index, vb.index, a, b);
    }

    private List<int> Neighbors(int node)
    {
        _neighborScratch.Clear();
        for (int dir = 0; dir < 6; ++dir)
            Nav.CollectFaceNeighbors(node, dir, _neighborScratch);
        return _neighborScratch;
    }

    private void EnsureCapacity(int count)
    {
        if (_epochOf.Length >= count)
            return;
        _epochOf = new int[count];
        _g = new float[count];
        _parent = new int[count];
        _closed = new bool[count];
        _epoch = 0;
    }

    private void Visit(int node)
    {
        _epochOf[node] = _epoch;
        _g[node] = float.MaxValue;
        _parent[node] = -1;
        _closed[node] = false;
    }

    // ---- binary min-heap with lazy deletion ----

    private void Push(float f, int node)
    {
        _open.Add((f, node));
        int i = _open.Count - 1;
        while (i > 0)
        {
            int p = (i - 1) / 2;
            if (_open[p].F <= _open[i].F)
                break;
            (_open[p], _open[i]) = (_open[i], _open[p]);
            i = p;
        }
    }

    private int Pop()
    {
        if (_open.Count == 0)
            return -1;
        var top = _open[0];
        _open[0] = _open[^1];
        _open.RemoveAt(_open.Count - 1);
        int i = 0;
        while (true)
        {
            int l = 2 * i + 1, r = l + 1, m = i;
            if (l < _open.Count && _open[l].F < _open[m].F)
                m = l;
            if (r < _open.Count && _open[r].F < _open[m].F)
                m = r;
            if (m == i)
                break;
            (_open[i], _open[m]) = (_open[m], _open[i]);
            i = m;
        }
        return top.Node;
    }
}


