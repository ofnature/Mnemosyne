using Mnemosyne.Core;
using System.Numerics;

namespace Mnemosyne.Cli;

// The regression check for padding and recorded obstacles.
//
// Bulk-recording obstacles from a heuristic is exactly the kind of change that quietly ruins
// navigation: flag a staircase's risers and every route starts avoiding stairs, flag a doorway
// and a building becomes unreachable. Padding can only lengthen a route, never break one — but
// obstacles sit on top of that and can, so this measures both.
//
// Sample routes across the zone, path each one raw and padded, and report what changed.
//
// usage: Mnemosyne.Cli padcheck <zone-substring> [routes]
public static class PadCheck
{
    public static int Run(string[] args)
    {
        var routeCount = args.Length > 2 && int.TryParse(args[2], out var n) ? n : 60;
        if (Probe.ResolveZoneOrReport(args.Length > 1 ? args[1] : "") is not { } entry)
            return 1;

        var overrides = OverrideStore.Load(entry.Key);
        Console.WriteLine($"zone: {entry.Key}");
        Console.WriteLine($"{overrides.Obstacles.Count} recorded obstacles; sampling {routeCount} routes");

        var navmesh = MeshCache.Load(entry.Path);
        var pf = new MeshPathfinder(navmesh.Mesh);
        // Sample within one connected component. These meshes are badly fragmented - Old
        // Gridania is 892 components - so pairs picked from the whole zone almost never
        // connect, and the check reported "no routes completed" for a zone that routes fine.
        var centers = LargestComponentCenters(navmesh.Mesh);
        if (centers.Count < 2)
        {
            Console.WriteLine("not enough walkable mesh");
            return 1;
        }
        Console.WriteLine($"sampling within the largest connected component ({centers.Count} polys)");

        int routed = 0, worse = 0;
        double totalRaw = 0, totalPadded = 0, worstInflation = 1;
        Vector3 worstFrom = default, worstTo = default;
        var inflations = new List<double>();

        for (int i = 0; i < routeCount; ++i)
        {
            var a = centers[(int)((long)i * 7919 % centers.Count)];
            var b = centers[(int)((long)i * 104729 % centers.Count)];
            if (Vector3.Distance(a, b) < 15)
                continue;
            var route = pf.FindWalkPath(a, b);
            if (route is not { Partial: false } || route.Waypoints.Count < 2)
                continue;
            ++routed;

            var rawLength = Length(route.Waypoints);
            var padded = new List<Vector3>(route.Waypoints);
            PathPadding.Apply(navmesh.Mesh, padded, PathPadding.DefaultPad, overrides.Obstacles);
            var paddedLength = Length(padded);

            totalRaw += rawLength;
            totalPadded += paddedLength;
            var inflation = rawLength > 0.1f ? paddedLength / rawLength : 1;
            inflations.Add(inflation);
            if (inflation > 1.5)
            {
                ++worse;
                if (inflation > worstInflation)
                {
                    worstInflation = inflation;
                    worstFrom = a;
                    worstTo = b;
                }
            }
        }

        if (routed == 0)
        {
            Console.WriteLine("no routes completed");
            return 1;
        }

        inflations.Sort();
        Console.WriteLine();
        Console.WriteLine($"  {routed} routes completed (padding never fails a route - it only bends one)");
        Console.WriteLine($"  total length {totalRaw:f0}m -> {totalPadded:f0}m ({100 * (totalPadded / totalRaw - 1):f1}% longer)");
        Console.WriteLine($"  per-route inflation: median {inflations[inflations.Count / 2]:f2}x, "
            + $"90th percentile {inflations[(int)(inflations.Count * 0.9)]:f2}x, worst {inflations[^1]:f2}x");
        if (worse > 0)
            Console.WriteLine($"  {worse} route(s) more than 50% longer - worst from "
                + $"({worstFrom.X:f0}, {worstFrom.Y:f0}, {worstFrom.Z:f0}) to ({worstTo.X:f0}, {worstTo.Y:f0}, {worstTo.Z:f0})");
        Console.WriteLine();
        Console.WriteLine(inflations[^1] < 1.5
            ? "  healthy: no route was distorted"
            : "  check the worst routes - an obstacle may be sitting on something walkable");
        return 0;
    }

    /// <summary>Poly centres of the biggest connected walkable region, which is where a
    /// character actually spends its time.</summary>
    private static List<Vector3> LargestComponentCenters(DotRecast.Detour.DtNavMesh mesh)
    {
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

        var seen = new HashSet<long>();
        List<long> best = [];
        foreach (var start in all)
        {
            if (!seen.Add(start))
                continue;
            List<long> component = [start];
            var stack = new Stack<long>();
            stack.Push(start);
            while (stack.Count > 0)
            {
                var next = stack.Pop();
                mesh.GetTileAndPolyByRefUnsafe(next, out var tile, out var poly);
                for (int i = tile.polyLinks[poly.index]; i != DotRecast.Detour.DtNavMesh.DT_NULL_LINK; i = tile.links[i].next)
                {
                    var neighbour = tile.links[i].refs;
                    if (neighbour == 0 || !seen.Add(neighbour))
                        continue;
                    component.Add(neighbour);
                    stack.Push(neighbour);
                }
            }
            if (component.Count > best.Count)
                best = component;
        }

        var centers = new List<Vector3>();
        foreach (var polyRef in best)
        {
            mesh.GetTileAndPolyByRefUnsafe(polyRef, out var tile, out var poly);
            var sum = Vector3.Zero;
            for (int v = 0; v < poly.vertCount; ++v)
            {
                int vi = poly.verts[v] * 3;
                sum += new Vector3(tile.data.verts[vi], tile.data.verts[vi + 1], tile.data.verts[vi + 2]);
            }
            centers.Add(sum / poly.vertCount);
        }
        return centers;
    }

    private static List<Vector3> PolyCenters(DotRecast.Detour.DtNavMesh mesh)
    {
        var centers = new List<Vector3>();
        for (int i = 0; i < mesh.GetMaxTiles(); ++i)
        {
            var tile = mesh.GetTile(i);
            if (tile?.data?.header == null)
                continue;
            for (int p = 0; p < tile.data.header.polyCount; ++p)
            {
                var poly = tile.data.polys[p];
                if (poly.GetPolyType() != 0)
                    continue;
                var sum = Vector3.Zero;
                for (int v = 0; v < poly.vertCount; ++v)
                {
                    int vi = poly.verts[v] * 3;
                    sum += new Vector3(tile.data.verts[vi], tile.data.verts[vi + 1], tile.data.verts[vi + 2]);
                }
                centers.Add(sum / poly.vertCount);
            }
        }
        return centers;
    }

    private static float Length(List<Vector3> path)
    {
        float length = 0;
        for (int i = 1; i < path.Count; ++i)
            length += Vector3.Distance(path[i - 1], path[i]);
        return length;
    }
}
