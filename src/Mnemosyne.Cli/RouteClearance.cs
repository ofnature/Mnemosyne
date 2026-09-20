using DotRecast.Detour;
using Mnemosyne.Core;
using Navmesh;
using System.Numerics;

namespace Mnemosyne.Cli;

// Why a route that "looks fine" gets you stuck.
//
// Recast's default edge cost is pure distance, so the cheapest path clips the corner of every
// obstacle it rounds. On paper that is optimal; in game the character has a collision radius
// (~0.5 y) and a turning arc, so a waypoint 0.3 y from a pillar means the body is inside the
// pillar before the turn completes. The symptom is "it swings too close and I get stuck",
// and no amount of trimming waypoints fixes it, because the corner-clipping is the route.
//
// This measures it: distance from every waypoint to the nearest mesh boundary.
//
// usage: Mnemosyne.Cli route <zone-substring> <x1> <y1> <z1> <x2> <y2> <z2>
public static class RouteClearance
{
    /// <summary>Roughly a player's collision radius. Waypoints closer than this to a wall are
    /// asking the follower to drive through geometry.</summary>
    private const float BodyRadius = 0.5f;

    public static int Run(string[] args)
    {
        if (args.Length < 8
            || !float.TryParse(args[2], out var x1) || !float.TryParse(args[3], out var y1) || !float.TryParse(args[4], out var z1)
            || !float.TryParse(args[5], out var x2) || !float.TryParse(args[6], out var y2) || !float.TryParse(args[7], out var z2))
        {
            Console.WriteLine("usage: route <zone-substring> <x1> <y1> <z1> <x2> <y2> <z2>");
            return 1;
        }

        if (Probe.ResolveZoneOrReport(args[1]) is not { } entry)
            return 1;
        Console.WriteLine($"zone: {entry.Key}");

        var navmesh = Probe.LoadServed(entry.Path, entry.Key);
        var pf = new MeshPathfinder(navmesh.Mesh);
        var from = new Vector3(x1, y1, z1);
        var to = new Vector3(x2, y2, z2);

        // with the zone's override links, so this reports the route the service would serve
        var route = pf.FindWalkPath(from, to, OverrideStore.Load(entry.Key).Links);
        if (route == null)
        {
            Console.WriteLine("no route");
            return 1;
        }
        Console.WriteLine($"route: {route.Waypoints.Count} waypoints, {Length(route.Waypoints):f1}m{(route.Partial ? " (PARTIAL - does not reach the goal)" : "")}"
            + (route.Partial ? " (partial)" : ""));
        Console.WriteLine();

        var query = new DtNavMeshQuery(navmesh.Mesh);
        var filter = new DtQueryDefaultFilter();

        Console.WriteLine($"  {"#",-4} {"position",-28} {"clearance",-11} ");
        var tight = 0;
        var clearances = new List<float>();
        for (int i = 0; i < route.Waypoints.Count; ++i)
        {
            var w = route.Waypoints[i];
            var clearance = Clearance(query, filter, w);
            clearances.Add(clearance);
            var flag = clearance < BodyRadius ? "  <-- inside body radius"
                : clearance < 1.0f ? "  <-- tight" : "";
            if (clearance < BodyRadius)
                ++tight;
            Console.WriteLine($"  {i,-4} ({w.X,7:f1}, {w.Y,6:f1}, {w.Z,7:f1})    {clearance,6:f2}m{flag}");
        }

        // Waypoints are only the corners. The straight runs between them are where a body
        // actually travels, so sample along each leg too - a leg can graze a wall with both
        // endpoints comfortably clear.
        var legMin = LegMinimum(query, filter, route.Waypoints, out var legWorst);

        Console.WriteLine();
        Console.WriteLine($"  waypoint clearance: min {clearances.Min():f2}m, median {Median(clearances):f2}m");
        if (legMin < float.MaxValue)
            Console.WriteLine($"  along the legs:     min {legMin:f2}m at ({legWorst.X:f1}, {legWorst.Y:f1}, {legWorst.Z:f1})");
        Console.WriteLine();
        var worst = MathF.Min(clearances.Min(), legMin);
        Console.WriteLine(worst < BodyRadius
            ? $"  this route passes within {worst:f2}m of a wall - closer than a body ({BodyRadius:f2}m). "
                + $"{tight} waypoint(s) are inside the body radius; a follower will clip geometry here."
            : $"  clears geometry by {worst:f2}m throughout");

        // ---- and the same route with obstacles padded ------------------------------------
        var pad = args.Length > 8 && float.TryParse(args[8], out var p) ? p : PathPadding.DefaultPad;
        var padded = new List<Vector3>(route.Waypoints);
        var zoneOverrides = OverrideStore.Load(entry.Key);
        var movedCount = PathPadding.Apply(navmesh.Mesh, padded, pad, zoneOverrides.Obstacles);

        Console.WriteLine();
        Console.WriteLine($"  --- padded to {pad:f2}m ---");
        // Padding both moves waypoints and inserts new ones to bend legs away from walls, so
        // the two routes no longer line up index for index - report the padded route on its
        // own terms and mark which points the original never had.
        var originals = route.Waypoints.ToHashSet();
        var paddedClearances = padded.Select(w => PathPadding.Clearance(query, filter, zoneOverrides.Obstacles, w)).ToList();
        for (int i = 0; i < padded.Count; ++i)
        {
            var note = originals.Contains(padded[i])
                ? (i == 0 || i == padded.Count - 1 ? "endpoint, fixed" : "unchanged")
                : "moved or inserted";
            Console.WriteLine($"  {i,-4} ({padded[i].X,7:f1}, {padded[i].Y,6:f1}, {padded[i].Z,7:f1})    "
                + $"{paddedClearances[i],5:f2}m   {note}");
        }

        var paddedLegMin = LegMinimum(query, filter, padded, out var paddedWorstAt);
        // Endpoints are the caller's choice and never moved, so a goal against a wall would
        // otherwise dominate the summary and hide what padding actually achieved.
        var interiorBefore = clearances.Skip(1).SkipLast(1).DefaultIfEmpty(-1).Min();
        var interiorAfter = paddedClearances.Skip(1).SkipLast(1).DefaultIfEmpty(-1).Min();

        Console.WriteLine();
        Console.WriteLine($"  waypoints {route.Waypoints.Count} -> {padded.Count} ({movedCount} moved or inserted), "
            + $"path length {Length(route.Waypoints):f1}m -> {Length(padded):f1}m");
        Console.WriteLine($"  interior waypoints: min clearance {interiorBefore:f2}m -> {interiorAfter:f2}m");
        Console.WriteLine($"  along the legs:     min {legMin:f2}m -> {paddedLegMin:f2}m"
            + $" at ({paddedWorstAt.X:f1}, {paddedWorstAt.Y:f1}, {paddedWorstAt.Z:f1})");
        var endpointClearance = MathF.Min(paddedClearances[0], paddedClearances[^1]);
        if (endpointClearance < pad)
            Console.WriteLine($"  note: an endpoint sits {endpointClearance:f2}m from a wall. Endpoints are never"
                + " moved, so the approach leg still grazes - use a goal tolerance to stop short of it.");
        return 0;
    }

    /// <summary>Worst clearance anywhere along the legs, not just at the corners - a leg can
    /// graze a wall with both its endpoints comfortably clear.</summary>
    private static float LegMinimum(DtNavMeshQuery query, IDtQueryFilter filter, List<Vector3> path, out Vector3 worstAt)
    {
        var min = float.MaxValue;
        worstAt = path.Count > 0 ? path[0] : default;
        for (int i = 1; i < path.Count; ++i)
        {
            var steps = Math.Max(2, (int)(Vector3.Distance(path[i - 1], path[i]) / 0.5f));
            for (int s = 1; s < steps; ++s)
            {
                var p = Vector3.Lerp(path[i - 1], path[i], s / (float)steps);
                var c = Clearance(query, filter, p);
                if (c >= 0 && c < min)
                {
                    min = c;
                    worstAt = p;
                }
            }
        }
        return min;
    }

    private static float Clearance(DtNavMeshQuery query, IDtQueryFilter filter, Vector3 p)
    {
        query.FindNearestPoly(p.SystemToRecast(), new(2, 4, 2), filter, out var polyRef, out _, out _);
        if (polyRef == 0)
            return -1;
        // maxRadius bounds the search; 8m is far more clearance than any route needs
        return query.FindDistanceToWall(polyRef, p.SystemToRecast(), 8f, filter, out var dist, out _, out _).Succeeded()
            ? dist
            : -1;
    }

    private static float Median(List<float> values)
    {
        var sorted = values.Where(v => v >= 0).Order().ToList();
        return sorted.Count == 0 ? -1 : sorted[sorted.Count / 2];
    }

    private static float Length(List<Vector3> path)
    {
        float length = 0;
        for (int i = 1; i < path.Count; ++i)
            length += Vector3.Distance(path[i - 1], path[i]);
        return length;
    }
}
