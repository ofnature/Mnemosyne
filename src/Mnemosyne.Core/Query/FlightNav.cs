using Navmesh.NavVolume;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Mnemosyne.Core;

// Sparse voxel octree derived from a zone's VoxelMap (PLAN 9b): a COARSE clearance graph.
// Uniform regions collapse at every power-of-two level (a 64 m empty cube is ONE node) and,
// crucially, mixed regions at or below MinLeafSize collapse to SOLID - measured on x6f2,
// full 2 m fidelity exploded to 19M nodes/618 MB and searched SLOWER than the plain A*
// because the terrain-surface shell is inherently mixed at fine scale. With the 8 m cutoff,
// empty leaves are truly empty at full resolution (mixed never becomes empty), so any
// segment through them has honest >=8 m-ish clearance; the fine 2 m detail stays the old
// voxel pathfinder's job. Built at load time in-memory - invisible to protocol/Ariadne.
public sealed class FlightNav
{
    public const float MinLeafSize = 4f; // world units; mixed regions this small read as solid
    public const byte StateEmpty = 0;
    public const byte StateSolid = 1;
    public const byte StateMixed = 2;

    public struct Node
    {
        public Vector3 Center;
        public Vector3 Half;
        public int ChildBase; // index into child-link table (8 entries), -1 for leaves
        public byte State;
    }

    private readonly List<Node> _nodes = new(1 << 16);
    private readonly List<int> _childLinks = new(1 << 16);
    private readonly VoxelMap _volume;

    public int NodeCount => _nodes.Count;
    public int EmptyLeafCount { get; private set; }
    public double BuildMs { get; }
    public Vector3 BoundsMin { get; }
    public Vector3 BoundsMax { get; }

    public FlightNav(VoxelMap volume)
    {
        _volume = volume;
        BoundsMin = volume.RootTile.BoundsMin;
        BoundsMax = volume.RootTile.BoundsMax;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Build(volume.RootTile, 0, 0, 0, volume.RootTile.LevelDesc.NumCellsX);
        BuildMs = sw.Elapsed.TotalMilliseconds;
    }

    public Span<Node> Nodes => CollectionsMarshal.AsSpan(_nodes);

    private int Build(VoxelMap.Tile tile, int x0, int y0, int z0, int size)
    {
        var ld = tile.LevelDesc;

        // classify the covered cell range
        bool anyEmpty = false, anySolid = false, anySub = false;
        for (int z = z0; z < z0 + size; ++z)
            for (int x = x0; x < x0 + size; ++x)
                for (int y = y0; y < y0 + size; ++y)
                {
                    var v = tile.Contents[ld.VoxelToIndex(x, y, z)];
                    if ((v & VoxelMap.VoxelOccupiedBit) == 0)
                        anyEmpty = true;
                    else if ((v & VoxelMap.VoxelIdMask) == VoxelMap.VoxelIdMask)
                        anySolid = true;
                    else
                        anySub = true;
                }

        var cellSize = ld.CellSize;
        var min = tile.BoundsMin + new Vector3(x0, y0, z0) * cellSize;
        var half = cellSize * (size * 0.5f);
        var center = min + half;

        if (!anySolid && !anySub)
            return AddLeaf(center, half, StateEmpty);
        if (!anyEmpty && !anySub && anySolid)
            return AddLeaf(center, half, StateSolid);

        // coarse cutoff: small mixed regions are solid as far as flight corridors care
        if (cellSize.X * size <= MinLeafSize)
            return AddLeaf(center, half, StateSolid);

        if (size == 1)
        {
            // single subdivided cell: recurse into its subtile (adds 3 octree levels)
            var v = tile.Contents[ld.VoxelToIndex(x0, y0, z0)];
            var sub = tile.Subdivision[(ushort)(v & VoxelMap.VoxelIdMask)];
            return Build(sub, 0, 0, 0, sub.LevelDesc.NumCellsX);
        }

        // mixed: split into 8 octants
        int self = _nodes.Count;
        _nodes.Add(new() { Center = center, Half = half, ChildBase = -2, State = StateMixed });
        int childBase = _childLinks.Count;
        for (int i = 0; i < 8; ++i)
            _childLinks.Add(-1);
        int h = size / 2;
        for (int i = 0; i < 8; ++i)
        {
            int cx = x0 + ((i & 1) != 0 ? h : 0);
            int cy = y0 + ((i & 2) != 0 ? h : 0);
            int cz = z0 + ((i & 4) != 0 ? h : 0);
            _childLinks[childBase + i] = Build(tile, cx, cy, cz, h);
        }
        var span = CollectionsMarshal.AsSpan(_nodes);
        span[self].ChildBase = childBase;
        return self;
    }

    private int AddLeaf(Vector3 center, Vector3 half, byte state)
    {
        if (state == StateEmpty)
            ++EmptyLeafCount;
        _nodes.Add(new() { Center = center, Half = half, ChildBase = -1, State = state });
        return _nodes.Count - 1;
    }

    // ---- queries ----

    /// <summary>Leaf containing the point, or -1 when outside the volume.</summary>
    public int LeafAt(Vector3 p)
    {
        if (p.X < BoundsMin.X || p.Y < BoundsMin.Y || p.Z < BoundsMin.Z
            || p.X >= BoundsMax.X || p.Y >= BoundsMax.Y || p.Z >= BoundsMax.Z)
            return -1;
        var nodes = Nodes;
        int current = 0;
        while (nodes[current].ChildBase >= 0)
        {
            ref var n = ref nodes[current];
            int i = (p.X >= n.Center.X ? 1 : 0) | (p.Y >= n.Center.Y ? 2 : 0) | (p.Z >= n.Center.Z ? 4 : 0);
            current = _childLinks[n.ChildBase + i];
        }
        return current;
    }

    /// <summary>All empty leaves sharing (part of) the given face of node <paramref name="leaf"/>.
    /// Directions: 0=-X 1=+X 2=-Y 3=+Y 4=-Z 5=+Z.</summary>
    public void CollectFaceNeighbors(int leaf, int direction, List<int> results)
    {
        ref var n = ref Nodes[leaf];
        // a thin probe box just across the face; tangent extents shrunk to avoid corner contacts
        const float eps = 0.05f;
        var min = n.Center - n.Half + new Vector3(eps);
        var max = n.Center + n.Half - new Vector3(eps);
        switch (direction)
        {
            case 0: min.X = n.Center.X - n.Half.X - eps; max.X = n.Center.X - n.Half.X + eps; break;
            case 1: min.X = n.Center.X + n.Half.X - eps; max.X = n.Center.X + n.Half.X + eps; break;
            case 2: min.Y = n.Center.Y - n.Half.Y - eps; max.Y = n.Center.Y - n.Half.Y + eps; break;
            case 3: min.Y = n.Center.Y + n.Half.Y - eps; max.Y = n.Center.Y + n.Half.Y + eps; break;
            case 4: min.Z = n.Center.Z - n.Half.Z - eps; max.Z = n.Center.Z - n.Half.Z + eps; break;
            case 5: min.Z = n.Center.Z + n.Half.Z - eps; max.Z = n.Center.Z + n.Half.Z + eps; break;
        }
        CollectEmptyIntersecting(0, min, max, leaf, results);
    }

    /// <summary>All empty leaves intersecting an AABB (entry-point search).</summary>
    public void CollectEmptyInBox(Vector3 min, Vector3 max, List<int> results) =>
        CollectEmptyIntersecting(0, min, max, -1, results);

    private void CollectEmptyIntersecting(int node, Vector3 min, Vector3 max, int exclude, List<int> results)
    {
        var nodes = Nodes;
        ref var n = ref nodes[node];
        if (n.Center.X - n.Half.X > max.X || n.Center.X + n.Half.X < min.X
            || n.Center.Y - n.Half.Y > max.Y || n.Center.Y + n.Half.Y < min.Y
            || n.Center.Z - n.Half.Z > max.Z || n.Center.Z + n.Half.Z < min.Z)
            return;
        if (n.ChildBase < 0)
        {
            if (n.State == StateEmpty && node != exclude)
                results.Add(node);
            return;
        }
        for (int i = 0; i < 8; ++i)
            CollectEmptyIntersecting(_childLinks[n.ChildBase + i], min, max, exclude, results);
    }

    /// <summary>Segment stays in empty space (and inside the volume)?</summary>
    public bool LineOfSight(Vector3 a, Vector3 b)
    {
        if (LeafAt(a) < 0 || LeafAt(b) < 0)
            return false; // outside the volume counts as blocked, matching VoxelMap semantics
        return SegmentClear(0, a, b);
    }

    private bool SegmentClear(int node, Vector3 a, Vector3 b)
    {
        var nodes = Nodes;
        ref var n = ref nodes[node];
        if (!SegmentIntersectsBox(a, b, n.Center, n.Half))
            return true;
        if (n.ChildBase < 0)
            return n.State != StateSolid;
        for (int i = 0; i < 8; ++i)
            if (!SegmentClear(_childLinks[n.ChildBase + i], a, b))
                return false;
        return true;
    }

    private static bool SegmentIntersectsBox(Vector3 a, Vector3 b, Vector3 center, Vector3 half)
    {
        // slab test on the segment parameterized a + t(b-a), t in [0,1]
        var d = b - a;
        float tMin = 0, tMax = 1;
        for (int axis = 0; axis < 3; ++axis)
        {
            float da = axis == 0 ? d.X : axis == 1 ? d.Y : d.Z;
            float aa = axis == 0 ? a.X : axis == 1 ? a.Y : a.Z;
            float c = axis == 0 ? center.X : axis == 1 ? center.Y : center.Z;
            float h = axis == 0 ? half.X : axis == 1 ? half.Y : half.Z;
            if (MathF.Abs(da) < 1e-8f)
            {
                if (aa < c - h || aa > c + h)
                    return false;
                continue;
            }
            float t1 = (c - h - aa) / da;
            float t2 = (c + h - aa) / da;
            if (t1 > t2)
                (t1, t2) = (t2, t1);
            tMin = MathF.Max(tMin, t1);
            tMax = MathF.Min(tMax, t2);
            if (tMin > tMax)
                return false;
        }
        return true;
    }
}

