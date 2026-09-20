using DotRecast.Core.Numerics;
using DotRecast.Detour;
using Navmesh;
using System.Numerics;

namespace Mnemosyne.Core;

/// <summary>
/// reachableCells — "where can I walk from here?" as a world-aligned grid of stacked walkable
/// surfaces (spec: Ariadne's docs/mnemosyne-protocol.md). Asked for by Theseus's dungeon
/// auto-solver to find ground it has not explored yet and the edges where walkable mesh is cut
/// off.
///
/// It answers <b>reachability only</b>: Mnemosyne has no notion of explored/visited, and must
/// not grow one. That state is per run and lives in the consumer.
///
/// Two halves, because they cost differently:
/// <list type="bullet">
/// <item>One flood over poly adjacency from the start poly — the component you are standing in.
/// Zone-wide, not clipped to the query window: a cell reachable only by leaving the window and
/// coming back is still reachable. Callers cache this per start poly.</item>
/// <item>A rasterisation of <i>walkable</i> polys into the grid, sampled at cell centres. A
/// surface is <c>reachable</c> when its poly is in that component and <c>cutOff</c> when it is
/// walkable mesh the flood never reached. Redone per query, because the window differs.</item>
/// </list>
/// </summary>
public sealed class ReachableCellsQuery(DtNavMesh mesh, IDtQueryFilter filter, IReadOnlyList<OverrideLink> links)
{
    /// <summary>The agent height: samples less than this apart vertically are one surface, so
    /// no two genuinely separate floors can ever merge into one answer.</summary>
    public const float SurfaceMergeHeight = 2f;

    public const byte StateReachable = 1;
    public const byte StateCutOff = 2;

    /// <summary>Column count ceiling: a bigger window is refused rather than truncated, because
    /// a truncated answer reads as "nothing out there" (spec).</summary>
    public const int MaxColumns = 65_536;

    private static readonly RcVec3f SnapExtents = new(5, 5, 5);
    private readonly DtNavMeshQuery _query = new(mesh);

    /// <summary>One answer's grid. <paramref name="Columns"/>, <paramref name="Heights"/> and
    /// <paramref name="States"/> are parallel — one entry per surface, not per column, since a
    /// column can hold several stacked floors.</summary>
    public sealed record Grid(Vector2 Origin, float CellSize, int Width, int Depth,
        int[] Columns, float[] Heights, byte[] States, bool ReachableOutside,
        int ReachablePolys, int WalkablePolys);

    /// <summary>The grid a query with this window will use: world-aligned (cells line up between
    /// queries with the same cell size, so one visited set can span many calls), and possibly a
    /// cell or row larger than the radius implies.</summary>
    public static (float OriginX, float OriginZ, int Width, int Depth) GridPlan(Vector3 from, float radius, float cellSize)
    {
        var originX = MathF.Floor((from.X - radius) / cellSize) * cellSize;
        var originZ = MathF.Floor((from.Z - radius) / cellSize) * cellSize;
        var width = (int)MathF.Ceiling((from.X + radius - originX) / cellSize);
        var depth = (int)MathF.Ceiling((from.Z + radius - originZ) / cellSize);
        return (originX, originZ, width, depth);
    }

    /// <summary>True when the plan is inside the column ceiling — the caller answers `failed`
    /// otherwise, rather than handing back a clipped window.</summary>
    public static bool PlanFits(in (float OriginX, float OriginZ, int Width, int Depth) plan) =>
        plan.Width > 0 && plan.Depth > 0 && (long)plan.Width * plan.Depth <= MaxColumns;

    // ---- the flood ----------------------------------------------------------------------

    /// <summary>The poly the flood begins from: the same 5 y snap findPath uses. False when
    /// <paramref name="from"/> is not on the mesh at all — the caller answers `startOffMesh`.</summary>
    public bool TryStart(Vector3 from, out long startRef, out Vector3 snapped)
    {
        _query.FindNearestPoly(from.SystemToRecast(), SnapExtents, filter, out startRef, out var pt, out _);
        snapped = startRef == 0 ? from : pt.RecastToSystem();
        return startRef != 0;
    }

    /// <summary>The connected component containing <paramref name="startRef"/>: a walk over the
    /// mesh's link table (the traversal ReachMap's component report uses), plus the virtual
    /// override links, which are resolved at query time rather than being real off-mesh
    /// connections in the mesh — MeshPathfinder stitches routes across them the same way.</summary>
    public HashSet<long> Flood(long startRef)
    {
        var component = new HashSet<long>();
        FloodInto(component, startRef);

        if (links.Count > 0)
        {
            // A link says "you can get from A to B even though the mesh disagrees", so a link
            // with one end in the component pulls in everything reachable from its other end.
            // Links chain (walk -> link -> walk -> link), so this settles rather than trying
            // each one once.
            bool changed;
            do
            {
                changed = false;
                foreach (var link in links)
                {
                    var a = new Vector3(link.From[0], link.From[1], link.From[2]);
                    var b = new Vector3(link.To[0], link.To[1], link.To[2]);
                    if (Bridge(component, a, b))
                        changed = true;
                    if (link.Bidirectional && Bridge(component, b, a))
                        changed = true;
                }
            } while (changed);
        }
        return component;
    }

    private void FloodInto(HashSet<long> component, long startRef)
    {
        if (!component.Add(startRef))
            return;

        var stack = new Stack<long>();
        stack.Push(startRef);
        while (stack.Count > 0)
        {
            var next = stack.Pop();
            mesh.GetTileAndPolyByRefUnsafe(next, out var tile, out var poly);
            for (var i = tile.polyLinks[poly.index]; i != DtNavMesh.DT_NULL_LINK; i = tile.links[i].next)
            {
                var neighbour = tile.links[i].refs;
                if (neighbour == 0 || component.Contains(neighbour))
                    continue;
                // walkable by the same filter the caller's pathfinding uses: blocked and pruned
                // polys are not somewhere you can walk, so they cannot carry the flood either
                mesh.GetTileAndPolyByRefUnsafe(neighbour, out var nTile, out var nPoly);
                if (!filter.PassFilter(neighbour, nTile, nPoly))
                    continue;
                component.Add(neighbour);
                stack.Push(neighbour);
            }
        }
    }

    /// <summary>Cross one override link when its near end is already reachable: everything from
    /// its far end joins the component. True when it added something.</summary>
    private bool Bridge(HashSet<long> component, Vector3 near, Vector3 far)
    {
        var nearRef = NearestPoly(near);
        if (nearRef == 0 || !component.Contains(nearRef))
            return false;
        var farRef = NearestPoly(far);
        if (farRef == 0 || component.Contains(farRef))
            return false;
        FloodInto(component, farRef);
        return true;
    }

    private long NearestPoly(Vector3 p)
    {
        _query.FindNearestPoly(p.SystemToRecast(), SnapExtents, filter, out var polyRef, out _, out _);
        return polyRef;
    }

    // ---- the rasterisation --------------------------------------------------------------

    /// <summary>Rasterise the window around <paramref name="from"/>. Sampling is at cell centres
    /// only, so a feature narrower than a cell can be absent from the answer while connectivity
    /// through it is still correct — reachability is decided on polys, not cells.</summary>
    public Grid Rasterize(HashSet<long> component, Vector3 from, float radius, float cellSize,
        float? minY, float? maxY)
    {
        var (originX, originZ, width, depth) = GridPlan(from, radius, cellSize);
        var samples = new List<(float Height, bool Reachable)>?[width * depth];
        var outside = false;
        int walkable = 0, reachable = 0;

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
                if (!filter.PassFilter(polyRef, tile, poly))
                    continue; // blocked or pruned by an override, or outside the include flags

                ++walkable;
                var inComponent = component.Contains(polyRef);
                if (inComponent)
                    ++reachable;

                var (minX, maxX, minZ, maxZ, minPolyY, maxPolyY) = Bounds(data, poly);

                // "is anything reachable still out there": asked of the component, not of the
                // rasterisation, so a window that clips the component says so
                if (inComponent && (minX < originX || maxX > originX + width * cellSize
                    || minZ < originZ || maxZ > originZ + depth * cellSize
                    || (minY is { } band && minPolyY < band)
                    || (maxY is { } ceiling && maxPolyY > ceiling)))
                {
                    outside = true;
                }

                var x0 = Math.Max(0, (int)MathF.Floor((minX - originX) / cellSize));
                var x1 = Math.Min(width - 1, (int)MathF.Floor((maxX - originX) / cellSize));
                var z0 = Math.Max(0, (int)MathF.Floor((minZ - originZ) / cellSize));
                var z1 = Math.Min(depth - 1, (int)MathF.Floor((maxZ - originZ) / cellSize));
                for (var zi = z0; zi <= z1; ++zi)
                {
                    for (var xi = x0; xi <= x1; ++xi)
                    {
                        var cx = originX + (xi + 0.5f) * cellSize;
                        var cz = originZ + (zi + 0.5f) * cellSize;
                        if (!ContainsXZ(data, poly, cx, cz))
                            continue;
                        // the height comes from the detail mesh at the centre (0.1 y precision)
                        if (_query.GetPolyHeight(polyRef, new RcVec3f(cx, from.Y, cz), out var h).Failed())
                            continue;
                        var column = zi * width + xi;
                        (samples[column] ??= []).Add((MathF.Round(h * 10f) / 10f, inComponent));
                    }
                }
            }
        }

        // stacked floors, highest first within a column; samples within the agent height are one
        // surface and it is reachable if either sample was
        var columns = new List<int>();
        var heights = new List<float>();
        var states = new List<byte>();
        for (var column = 0; column < samples.Length; ++column)
        {
            if (samples[column] is not { } list)
                continue; // no walkable mesh in this column at all

            list.Sort(static (a, b) => b.Height.CompareTo(a.Height));
            var top = list[0];
            var topReachable = top.Reachable;
            for (var i = 1; i <= list.Count; ++i)
            {
                if (i < list.Count && top.Height - list[i].Height < SurfaceMergeHeight)
                {
                    topReachable |= list[i].Reachable;
                    continue;
                }

                if (InBand(top.Height, minY, maxY))
                {
                    columns.Add(column);
                    heights.Add(top.Height);
                    states.Add(topReachable ? StateReachable : StateCutOff);
                }

                if (i < list.Count)
                {
                    top = list[i];
                    topReachable = top.Reachable;
                }
            }
        }

        return new Grid(new Vector2(originX, originZ), cellSize, width, depth,
            [.. columns], [.. heights], [.. states], outside, reachable, walkable);
    }

    private static bool InBand(float height, float? minY, float? maxY) =>
        (minY is not { } floor || height >= floor) && (maxY is not { } ceiling || height <= ceiling);

    private static (float MinX, float MaxX, float MinZ, float MaxZ, float MinY, float MaxY) Bounds(DtMeshData data, DtPoly poly)
    {
        float minX = float.MaxValue, maxX = float.MinValue;
        float minZ = float.MaxValue, maxZ = float.MinValue;
        float minY = float.MaxValue, maxY = float.MinValue;
        for (var i = 0; i < poly.vertCount; ++i)
        {
            var v = poly.verts[i] * 3;
            var x = data.verts[v];
            var y = data.verts[v + 1];
            var z = data.verts[v + 2];
            minX = MathF.Min(minX, x);
            maxX = MathF.Max(maxX, x);
            minZ = MathF.Min(minZ, z);
            maxZ = MathF.Max(maxZ, z);
            minY = MathF.Min(minY, y);
            maxY = MathF.Max(maxY, y);
        }
        return (minX, maxX, minZ, maxZ, minY, maxY);
    }

    /// <summary>Is the cell centre inside this poly, in the XZ projection the spec samples in?
    /// Recast polys are convex, so a consistent turn direction on every edge means inside — and
    /// the first decisive edge picks that direction, which keeps the test independent of the
    /// mesh's winding. A degenerate (zero-area) poly claims no cells.</summary>
    private static bool ContainsXZ(DtMeshData data, DtPoly poly, float px, float pz)
    {
        float? sign = null;
        for (var i = 0; i < poly.vertCount; ++i)
        {
            var a = poly.verts[i] * 3;
            var b = poly.verts[(i + 1) % poly.vertCount] * 3;
            var ax = data.verts[a];
            var az = data.verts[a + 2];
            var bx = data.verts[b];
            var bz = data.verts[b + 2];
            var cross = (bx - ax) * (pz - az) - (bz - az) * (px - ax);
            if (MathF.Abs(cross) < 1e-4f)
                continue; // on this edge's line: the other edges decide
            var turn = MathF.Sign(cross);
            if (sign is not { } known)
                sign = turn;
            else if (turn != known)
                return false;
        }
        return sign != null;
    }
}
