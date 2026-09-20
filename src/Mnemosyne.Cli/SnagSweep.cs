using DotRecast.Detour;
using Mnemosyne.Core;
using Navmesh;
using System.Numerics;

namespace Mnemosyne.Cli;

// Find the places a follower will snag, without being told where they are.
//
// The symptom is always reported the same way — "I get stuck near the market" — and pinning
// it down by hand means walking the zone. Instead: route between many sampled points across
// the zone's main walkable area, measure clearance along every route, and cluster the tight
// spots. A place that shows up in many different routes at under a body's width is a
// chokepoint the whole zone funnels through, which is exactly what snags.
//
// usage: Mnemosyne.Cli snags <zone-substring> [routes] [clearance-threshold]
public static class SnagSweep
{
    private const float ClusterRadius = 4f;

    public static int Run(string[] args)
    {
        var routeCount = args.Length > 2 && int.TryParse(args[2], out var n) ? n : 200;
        var threshold = args.Length > 3 && float.TryParse(args[3], out var t) ? t : PathPadding.DefaultPad;

        if (Probe.ResolveZoneOrReport(args.Length > 1 ? args[1] : "") is not { } entry)
            return 1;
        Console.WriteLine($"zone: {entry.Key}");

        var navmesh = Probe.LoadServed(entry.Path, entry.Key);
        var pf = new MeshPathfinder(navmesh.Mesh);
        var query = new DtNavMeshQuery(navmesh.Mesh);
        var filter = new DtQueryDefaultFilter();

        var centers = PolyCenters(navmesh.Mesh);
        if (centers.Count < 2)
        {
            Console.WriteLine("not enough walkable mesh to sweep");
            return 1;
        }
        Console.WriteLine($"{centers.Count} walkable polys; sampling {routeCount} routes, flagging clearance under {threshold:f2}m");

        // Deterministic spread rather than random sampling: the same zone should give the same
        // report every run, or nobody can tell whether a fix helped.
        var hits = new List<(Vector3 Pos, float Clearance)>();
        var routed = 0;
        for (int i = 0; i < routeCount; ++i)
        {
            var a = centers[(int)((long)i * 7919 % centers.Count)];
            var b = centers[(int)((long)i * 104729 % centers.Count)];
            if (Vector3.Distance(a, b) < 10)
                continue;
            var route = pf.FindWalkPath(a, b);
            if (route is not { Partial: false } || route.Waypoints.Count < 2)
                continue;
            ++routed;

            for (int w = 1; w < route.Waypoints.Count; ++w)
            {
                var p0 = route.Waypoints[w - 1];
                var p1 = route.Waypoints[w];
                var steps = Math.Max(2, (int)(Vector3.Distance(p0, p1) / 1.0f));
                for (int s = 0; s <= steps; ++s)
                {
                    var p = Vector3.Lerp(p0, p1, s / (float)steps);
                    var c = PathPadding.Clearance(query, filter, p);
                    if (c >= 0 && c < threshold)
                        hits.Add((p, c));
                }
            }
        }

        Console.WriteLine($"{routed} routes completed, {hits.Count} tight samples");
        if (hits.Count == 0)
        {
            Console.WriteLine("no chokepoints under the threshold");
            return 0;
        }

        // Cluster: one bad corner produces dozens of samples, and a list of dozens is useless.
        var clusters = new List<(Vector3 Pos, float Worst, int Count)>();
        foreach (var (pos, clearance) in hits.OrderBy(h => h.Clearance))
        {
            var existing = clusters.FindIndex(c => Vector3.Distance(c.Pos, pos) < ClusterRadius);
            if (existing >= 0)
                clusters[existing] = (clusters[existing].Pos, clusters[existing].Worst, clusters[existing].Count + 1);
            else
                clusters.Add((pos, clearance, 1));
        }

        Console.WriteLine();
        Console.WriteLine($"  {clusters.Count} chokepoints, worst first (hits = how many sampled routes squeeze through):");
        Console.WriteLine();
        foreach (var (pos, worst, count) in clusters.OrderBy(c => c.Worst).ThenByDescending(c => c.Count).Take(15))
        {
            var padded = new List<Vector3> { pos + new Vector3(2, 0, 0), pos, pos - new Vector3(2, 0, 0) };
            PathPadding.Apply(navmesh.Mesh, padded, threshold);
            var after = PathPadding.Clearance(query, filter, padded[1]);
            // epsilon, or a 0.997m result prints as "1.00m but genuinely narrow" and reads
            // like the tool contradicting itself
            var verdict = after >= threshold - 0.01f ? "padding clears it"
                : after > worst + 0.05f ? $"padding helps ({after:f2}m) but the gap is genuinely narrow"
                : "padding cannot help - the gap is narrower than a body";
            Console.WriteLine($"    ({pos.X,8:f1}, {pos.Y,6:f1}, {pos.Z,8:f1})  {worst,5:f2}m  {count,4} hits   {verdict}");
        }
        return 0;
    }

    private static List<Vector3> PolyCenters(DtNavMesh mesh)
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
}
