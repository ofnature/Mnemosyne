using DotRecast.Detour;
using Mnemosyne.Core;
using Navmesh;
using System.Numerics;

namespace Mnemosyne.Cli;

// "Can you get from the start of this dungeon to the end?" is a question about the mesh as a
// whole, and reachmap only answers it locally. This lists the mesh's disconnected islands with
// where they sit, then the closest approaches between the big ones - because a dungeon that
// cannot be pathed end to end is almost always two large islands joined in the world by
// something the rasterizer could not walk: a slide, a drop, a jump pad, a lift.
//
// A pair that is close horizontally with a large height difference is a drop or a slide; a
// pair that is close in all three axes is a seam the mesh should never have split.
//
// usage: Mnemosyne.Cli components <zone-substring> [top]
public static class Components
{
    private sealed class Island
    {
        public int Id;
        public readonly List<long> Polys = [];
        public readonly List<Vector3> Verts = [];
        public Vector3 Min = new(float.MaxValue), Max = new(float.MinValue);
        public Vector3 Centroid;
        public float Area;
    }

    public static int Run(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("usage: components <zone-substring> [top]");
            return 1;
        }
        var top = args.Length > 2 && int.TryParse(args[2], out var t) ? t : 12;
        if (Probe.ResolveZoneOrReport(args[1]) is not { } entry)
            return 1;
        Console.WriteLine($"zone: {entry.Key}");

        var mesh = Probe.LoadServed(entry.Path, entry.Key).Mesh;
        var islands = FindIslands(mesh, out var islandOf);
        var total = islands.Sum(i => i.Polys.Count);
        var totalArea = islands.Sum(i => i.Area);
        var big = islands.OrderByDescending(i => i.Area).Take(top).ToList();
        for (int r = 0; r < big.Count; ++r)
            big[r].Id = r + 1;

        Console.WriteLine($"{total} walkable polys, {totalArea:f0} m2, in {islands.Count} islands");
        Console.WriteLine();
        Console.WriteLine($"  {"#",-3} {"polys",6} {"area",8} {"share",6}   {"centroid",-26} {"height range",-16} extent");
        foreach (var i in big)
        {
            var size = i.Max - i.Min;
            Console.WriteLine($"  {i.Id,-3} {i.Polys.Count,6} {i.Area,6:f0}m2 {100 * i.Area / totalArea,5:f1}%   "
                + $"{Fmt(i.Centroid),-26} {i.Min.Y,6:f1} .. {i.Max.Y,-6:f1} {size.X:f0} x {size.Z:f0} m");
        }

        Console.WriteLine();
        Console.WriteLine("closest approaches between the large islands:");
        Console.WriteLine($"  {"pair",-8} {"horiz",6} {"drop",7}   {"from (upper)",-26} {"to (lower)",-26}");
        var gaps = new List<(Island A, Island B, Vector3 Pa, Vector3 Pb, float Horiz)>();
        for (int a = 0; a < big.Count; ++a)
            for (int b = a + 1; b < big.Count; ++b)
                if (ClosestApproach(big[a], big[b]) is { } g)
                    gaps.Add((big[a], big[b], g.Pa, g.Pb, g.Horiz));
        foreach (var g in gaps.Where(g => g.Horiz < 12).OrderBy(g => g.Horiz))
        {
            // report upper -> lower, since that is the direction a slide or drop travels
            var (hi, lo, phi, plo) = g.Pa.Y >= g.Pb.Y ? (g.A, g.B, g.Pa, g.Pb) : (g.B, g.A, g.Pb, g.Pa);
            Console.WriteLine($"  {hi.Id,2} -> {lo.Id,-2} {g.Horiz,5:f1}m {phi.Y - plo.Y,6:f1}m   {Fmt(phi),-26} {Fmt(plo),-26}");
        }

        Profile(islands, islandOf.Count > 0 ? big.Count : 0);

        // One island in detail: where its ends are and what each end is nearest. For a slide,
        // the top end and the bottom end are where a link has to start and land.
        var at = Array.IndexOf(args, "--island");
        if (at >= 0 && at + 1 < args.Length)
        {
            var c = args[at + 1].Split(',').Select(float.Parse).ToArray();
            var q = new DtNavMeshQuery(mesh);
            q.FindNearestPoly(new Vector3(c[0], c[1], c[2]).SystemToRecast(), new(5, 10, 5), new DtQueryDefaultFilter(), out var pr, out _, out _);
            if (pr == 0 || !islandOf.TryGetValue(pr, out var isl))
                Console.WriteLine($"no mesh near {args[at + 1]}");
            else
            {
                var highest = isl.Verts.MaxBy(v => v.Y);
                var lowest = isl.Verts.MinBy(v => v.Y);
                Console.WriteLine();
                Console.WriteLine($"island at {args[at + 1]}: {isl.Polys.Count} polys, {isl.Area:f0}m2, "
                    + $"box {Fmt(isl.Min)} .. {Fmt(isl.Max)}");
                foreach (var (name, end) in new[] { ("top", highest), ("bottom", lowest) })
                {
                    Console.WriteLine($"  {name} end {Fmt(end)} - nearest large islands:");
                    foreach (var other in big
                        .Select(b => (b, p: b.Verts.MinBy(v => Vector3.Distance(v, end))))
                        .OrderBy(x => Vector3.Distance(x.p, end)).Take(3))
                        Console.WriteLine($"    #{other.b.Id,-3} {Vector3.Distance(other.p, end),6:f1}m away at {Fmt(other.p)}  (dy {other.p.Y - end.Y:+0.0;-0.0})");
                }
            }
        }

        // Top-down local map: which island owns each cell, and how high it is. Stairs that
        // did not mesh show up as a gap between two islands with a height step across it.
        var mapAt = Array.IndexOf(args, "--map");
        if (mapAt >= 0 && mapAt + 1 < args.Length)
        {
            var m = args[mapAt + 1].Split(',').Select(float.Parse).ToArray(); // cx,cz,radius,step,ymin,ymax
            LocalMap(mesh, islandOf, m[0], m[1], m[2], m[3], m[4], m[5]);
        }

        // The specks between the plateaus, as coordinates: a slide's actual path through the air.
        if (args.Contains("--specks"))
        {
            var bandLo = big.Min(i => i.Max.Y);
            var bandHi = big.Max(i => i.Min.Y);
            Console.WriteLine();
            Console.WriteLine($"small islands between the plateaus (height {bandLo:f0} .. {bandHi:f0}), highest first:");
            foreach (var i in islands.Where(i => i.Id == 0 && i.Centroid.Y > bandLo && i.Centroid.Y < bandHi)
                         .OrderByDescending(i => i.Centroid.Y))
                Console.WriteLine($"  {Fmt(i.Centroid)}  {i.Polys.Count,3} polys  {i.Area,5:f0}m2");
        }

        // Where the game says things start and end, and which island each lands on.
        if (ZoneNames.Lookup(entry.Key)?.Bg is { Length: > 0 } bg && GamePaths.FindSqpackDir() is { } sqpack)
        {
            var query = new DtNavMeshQuery(mesh);
            var filter = new DtQueryDefaultFilter();
            var markers = Mnemosyne.Builder.LayoutQuery.Near(sqpack, bg, Vector3.Zero, 100000)
                .Where(o => o.Type is "PopRange" or "ExitRange" or "MapRange" or "EventRange")
                .OrderBy(o => o.Type).ThenBy(o => o.Pos.Y)
                .ToList();
            Console.WriteLine();
            Console.WriteLine($"layout markers ({markers.Count}):");
            foreach (var m in markers)
            {
                query.FindNearestPoly(m.Pos.SystemToRecast(), new(3, 6, 3), filter, out var poly, out _, out _);
                var on = poly != 0 && islandOf.TryGetValue(poly, out var isl)
                    ? (isl.Id > 0 ? $"island #{isl.Id}" : $"small island ({isl.Polys.Count} polys)")
                    : "no mesh within 3m";
                Console.WriteLine($"  {m.Type,-11} {Fmt(m.Pos),-26} {on,-24} [{m.Layer}]");
            }
        }
        return 0;
    }

    /// <summary>Side view: the whole zone squashed onto its longest horizontal axis against
    /// height, one glyph per island. A slide or a drop shows up as a staircase of empty rows
    /// between two plateaus - which is hard to see from any list of coordinates.</summary>
    private static void Profile(List<Island> islands, int named)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var i in islands)
        {
            min = Vector3.Min(min, i.Min);
            max = Vector3.Max(max, i.Max);
        }
        var alongX = max.X - min.X >= max.Z - min.Z;
        float lo = alongX ? min.X : min.Z, hi = alongX ? max.X : max.Z;
        const int cols = 120, rows = 36;
        var grid = new char[rows, cols];
        for (int r = 0; r < rows; ++r)
            for (int c = 0; c < cols; ++c)
                grid[r, c] = ' ';

        foreach (var i in islands)
        {
            var glyph = i.Id is > 0 and <= 9 ? (char)('0' + i.Id) : i.Id > 9 ? (char)('A' + i.Id - 10) : '.';
            foreach (var v in i.Verts)
            {
                var c = (int)((( alongX ? v.X : v.Z) - lo) / (hi - lo + 1e-3f) * (cols - 1));
                var r = (int)((max.Y - v.Y) / (max.Y - min.Y + 1e-3f) * (rows - 1));
                // a named island wins over a speck, so the plateaus stay legible
                if (grid[r, c] == ' ' || (grid[r, c] == '.' && glyph != '.'))
                    grid[r, c] = glyph;
            }
        }

        Console.WriteLine();
        Console.WriteLine($"side profile: {(alongX ? "X" : "Z")} {lo:f0} .. {hi:f0} across, height {max.Y:f0} (top) .. {min.Y:f0} (bottom)");
        for (int r = 0; r < rows; ++r)
        {
            var label = r == 0 ? $"{max.Y,6:f0}" : r == rows - 1 ? $"{min.Y,6:f0}" : "      ";
            var line = new char[cols];
            for (int c = 0; c < cols; ++c)
                line[c] = grid[r, c];
            Console.WriteLine($"  {label} |{new string(line)}");
        }
        Console.WriteLine($"         {(alongX ? "X" : "Z")}={lo:f0}{new string(' ', cols - 12)}{hi:f0}");
        Console.WriteLine("  digits/letters = the numbered islands above, . = small islands");
    }

    private static void LocalMap(DtNavMesh mesh, Dictionary<long, Island> islandOf,
        float cx, float cz, float radius, float step, float ymin, float ymax)
    {
        var n = (int)(2 * radius / step) + 1;
        var owner = new char[n, n];
        var height = new float[n, n];
        for (int r = 0; r < n; ++r)
            for (int c = 0; c < n; ++c)
            {
                owner[r, c] = ' ';
                height[r, c] = float.MinValue;
            }

        foreach (var (polyRef, island) in islandOf)
        {
            mesh.GetTileAndPolyByRefUnsafe(polyRef, out var tile, out var poly);
            var v = new Vector3[poly.vertCount];
            for (int k = 0; k < poly.vertCount; ++k)
            {
                var o = poly.verts[k] * 3;
                v[k] = new Vector3(tile.data.verts[o], tile.data.verts[o + 1], tile.data.verts[o + 2]);
            }
            var glyph = island.Id is > 0 and <= 9 ? (char)('0' + island.Id) : island.Id > 9 ? (char)('A' + island.Id - 10) : '.';
            for (int k = 1; k + 1 < v.Length; ++k)
            {
                var (a, b, t) = (v[0], v[k], v[k + 1]);
                var loX = MathF.Min(a.X, MathF.Min(b.X, t.X)); var hiX = MathF.Max(a.X, MathF.Max(b.X, t.X));
                var loZ = MathF.Min(a.Z, MathF.Min(b.Z, t.Z)); var hiZ = MathF.Max(a.Z, MathF.Max(b.Z, t.Z));
                for (int c = Math.Max(0, (int)MathF.Floor((loX - (cx - radius)) / step)); c <= Math.Min(n - 1, (int)MathF.Ceiling((hiX - (cx - radius)) / step)); ++c)
                    for (int r = Math.Max(0, (int)MathF.Floor((loZ - (cz - radius)) / step)); r <= Math.Min(n - 1, (int)MathF.Ceiling((hiZ - (cz - radius)) / step)); ++r)
                    {
                        var px = cx - radius + c * step;
                        var pz = cz - radius + r * step;
                        if (!Barycentric(a, b, t, px, pz, out var y) || y < ymin || y > ymax || y <= height[r, c])
                            continue;
                        height[r, c] = y;
                        owner[r, c] = glyph;
                    }
            }
        }

        Console.WriteLine();
        Console.WriteLine($"top-down map around ({cx:f0}, {cz:f0}), {step}m cells, heights {ymin:f0} .. {ymax:f0}; north (low Z) at top");
        Console.WriteLine("  island" + new string(' ', n + 2) + "height (0 = low, 9 = high)");
        for (int r = 0; r < n; ++r)
        {
            var own = new char[n];
            var hgt = new char[n];
            for (int c = 0; c < n; ++c)
            {
                own[c] = owner[r, c];
                hgt[c] = height[r, c] == float.MinValue ? ' ' : (char)('0' + (int)Math.Clamp((height[r, c] - ymin) / (ymax - ymin + 1e-3f) * 10, 0, 9));
            }
            var z = cz - radius + r * step;
            Console.WriteLine($"  {z,6:f0} |{new string(own)}|   |{new string(hgt)}|");
        }
        Console.WriteLine($"         X {cx - radius:f0} .. {cx + radius:f0}");
    }

    private static bool Barycentric(Vector3 a, Vector3 b, Vector3 c, float px, float pz, out float y)
    {
        y = 0;
        var d = (b.Z - c.Z) * (a.X - c.X) + (c.X - b.X) * (a.Z - c.Z);
        if (MathF.Abs(d) < 1e-6f)
            return false;
        var l1 = ((b.Z - c.Z) * (px - c.X) + (c.X - b.X) * (pz - c.Z)) / d;
        var l2 = ((c.Z - a.Z) * (px - c.X) + (a.X - c.X) * (pz - c.Z)) / d;
        var l3 = 1 - l1 - l2;
        if (l1 < -0.01f || l2 < -0.01f || l3 < -0.01f)
            return false;
        y = l1 * a.Y + l2 * b.Y + l3 * c.Y;
        return true;
    }

    private static List<Island> FindIslands(DtNavMesh mesh, out Dictionary<long, Island> islandOf)
    {
        islandOf = [];
        var all = new List<long>();
        for (int i = 0; i < mesh.GetMaxTiles(); ++i)
        {
            var tile = mesh.GetTile(i);
            if (tile?.data?.header == null)
                continue;
            long refBase = mesh.GetPolyRefBase(tile);
            for (int p = 0; p < tile.data.header.polyCount; ++p)
                if (tile.data.polys[p].GetPolyType() == 0)
                    all.Add(refBase | (uint)p);
        }

        var islands = new List<Island>();
        foreach (var start in all)
        {
            if (islandOf.ContainsKey(start))
                continue;
            var island = new Island();
            islands.Add(island);
            var stack = new Stack<long>();
            stack.Push(start);
            islandOf[start] = island;
            while (stack.Count > 0)
            {
                var next = stack.Pop();
                island.Polys.Add(next);
                mesh.GetTileAndPolyByRefUnsafe(next, out var tile, out var poly);
                AddPoly(island, tile, poly);
                for (int l = tile.polyLinks[poly.index]; l != DtNavMesh.DT_NULL_LINK; l = tile.links[l].next)
                {
                    var neighbour = tile.links[l].refs;
                    if (neighbour == 0 || islandOf.ContainsKey(neighbour))
                        continue;
                    mesh.GetTileAndPolyByRefUnsafe(neighbour, out _, out var np);
                    if (np.GetPolyType() != 0)
                        continue; // off-mesh connection: not floor, and not a join for this picture
                    islandOf[neighbour] = island;
                    stack.Push(neighbour);
                }
            }
            if (island.Area > 0)
                island.Centroid /= island.Area;
        }
        return islands;
    }

    private static void AddPoly(Island island, DtMeshTile tile, DtPoly poly)
    {
        var v = new Vector3[poly.vertCount];
        for (int k = 0; k < poly.vertCount; ++k)
        {
            var o = poly.verts[k] * 3;
            v[k] = new Vector3(tile.data.verts[o], tile.data.verts[o + 1], tile.data.verts[o + 2]);
            island.Verts.Add(v[k]);
            island.Min = Vector3.Min(island.Min, v[k]);
            island.Max = Vector3.Max(island.Max, v[k]);
        }
        for (int k = 1; k + 1 < v.Length; ++k)
        {
            var area = Vector3.Cross(v[k] - v[0], v[k + 1] - v[0]).Length() * 0.5f;
            island.Area += area;
            island.Centroid += (v[0] + v[k] + v[k + 1]) / 3 * area;
        }
    }

    private static (Vector3 Pa, Vector3 Pb, float Horiz)? ClosestApproach(Island a, Island b)
    {
        // cheap reject: bounding boxes more than 12m apart horizontally cannot produce a gap worth listing
        var dx = MathF.Max(0, MathF.Max(a.Min.X - b.Max.X, b.Min.X - a.Max.X));
        var dz = MathF.Max(0, MathF.Max(a.Min.Z - b.Max.Z, b.Min.Z - a.Max.Z));
        if (dx > 12 || dz > 12)
            return null;

        float best = float.MaxValue;
        Vector3 pa = default, pb = default;
        foreach (var va in a.Verts)
            foreach (var vb in b.Verts)
            {
                // horizontal distance, with a small height penalty so a vertex directly above
                // another storey does not beat a genuine edge-to-edge gap
                var h = MathF.Sqrt((va.X - vb.X) * (va.X - vb.X) + (va.Z - vb.Z) * (va.Z - vb.Z));
                var score = h + 0.05f * MathF.Abs(va.Y - vb.Y);
                if (score < best)
                {
                    best = score;
                    pa = va;
                    pb = vb;
                }
            }
        var horiz = MathF.Sqrt((pa.X - pb.X) * (pa.X - pb.X) + (pa.Z - pb.Z) * (pa.Z - pb.Z));
        return (pa, pb, horiz);
    }

    private static string Fmt(Vector3 v) => $"({v.X,6:f1}, {v.Y,6:f1}, {v.Z,6:f1})";
}
