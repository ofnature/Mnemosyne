using Mnemosyne.Core;
using Navmesh;
using System.Numerics;

namespace Mnemosyne.Cli;

// "Can we path around that thing?" answered as a picture rather than a yes/no.
//
// A crossing test says whether one straight line is blocked; it cannot tell you whether the
// obstacle is a pillar you walk around or a wall that seals a whole area. This walks a grid
// around a point, pathfinds to every cell, and prints what it finds — so a pillar shows as a
// small island of '#' with clear floor either side, and a wall shows as a line you cannot get
// past at any offset.
//
// usage: Mnemosyne.Cli reachmap <zone-substring> <x> <y> <z> [radius] [step]
public static class ReachMap
{
    public static int Run(string[] args)
    {
        if (args.Length < 5 || !float.TryParse(args[2], out var x)
            || !float.TryParse(args[3], out var y) || !float.TryParse(args[4], out var z))
        {
            Console.WriteLine("usage: reachmap <zone-substring> <x> <y> <z> [radius] [step]");
            return 1;
        }
        var radius = args.Length > 5 && float.TryParse(args[5], out var r) ? r : 12f;
        var step = args.Length > 6 && float.TryParse(args[6], out var s) ? s : 1f;
        // Vertical snap. Towns are stacked - Ul'dah has walkways over plazas - so a generous
        // snap maps *other floors* and calls them unreachable, which is both true and useless.
        // Keep it tight enough to stay on one storey unless the caller says otherwise.
        var vertical = args.Length > 7 && float.TryParse(args[7], out var v) ? v : 2f;
        var center = new Vector3(x, y, z);

        if (Probe.ResolveZoneOrReport(args[1]) is not { } entry)
            return 1;
        Console.WriteLine($"zone: {entry.Key}");
        Console.WriteLine($"centre: ({x:f1}, {y:f1}, {z:f1})  radius {radius:f0}m  step {step:f1}m  vertical snap +-{vertical:f1}m");

        var navmesh = Probe.LoadServed(entry.Path, entry.Key);
        var pf = new MeshPathfinder(navmesh.Mesh);
        if (!pf.TryNearestGround(center, 10, out var origin))
        {
            Console.WriteLine("centre is not on the mesh");
            return 1;
        }

        int half = (int)MathF.Ceiling(radius / step);
        var rows = new List<string>();
        int reachable = 0, detoured = 0, cutOff = 0, noMesh = 0;
        var cutOffHeights = new List<float>();

        // +Z is south in FFXIV's coordinate space, so rows run north (top) to south (bottom)
        // and columns west (left) to east (right) - the same way the in-game map reads.
        for (int gz = -half; gz <= half; ++gz)
        {
            var row = new System.Text.StringBuilder();
            for (int gx = -half; gx <= half; ++gx)
            {
                if (gx == 0 && gz == 0)
                {
                    row.Append('@');
                    continue;
                }
                var probe = new Vector3(x + gx * step, y, z + gz * step);
                if (!pf.TryNearestGround(probe, vertical, out var cell))
                {
                    ++noMesh;
                    row.Append(' ');
                    continue;
                }
                var route = pf.FindWalkPath(origin, cell);
                if (route is not { Partial: false })
                {
                    ++cutOff;
                    cutOffHeights.Add(cell.Y - origin.Y);
                    row.Append('#');
                    continue;
                }
                var straight = Vector3.Distance(origin, cell);
                var detour = straight > 0.5f ? Length(route.Waypoints) / straight : 1;
                if (detour > 3)
                {
                    ++detoured;
                    row.Append('+');
                }
                else
                {
                    ++reachable;
                    row.Append('.');
                }
            }
            rows.Add(row.ToString());
        }

        Console.WriteLine();
        Console.WriteLine("        west" + new string(' ', Math.Max(0, half * 2 - 8)) + "east");
        for (int i = 0; i < rows.Count; ++i)
        {
            var label = i == 0 ? "north " : i == rows.Count - 1 ? "south " : "      ";
            Console.WriteLine($"  {label}{rows[i]}");
        }
        Console.WriteLine();
        Console.WriteLine("  @ you   . walkable & directly reachable   + reachable only via a >3x detour");
        Console.WriteLine("  # on the mesh but no route from here      (blank) no mesh");
        Console.WriteLine();
        Console.WriteLine($"  {reachable} direct, {detoured} long way round, {cutOff} cut off, {noMesh} unmeshed");
        if (cutOffHeights.Count > 0)
        {
            // Cut off *and* at your own height is the interesting case: same floor, no route.
            // Cut off well above or below is just another storey doing its job.
            var sameFloor = cutOffHeights.Count(h => MathF.Abs(h) <= 1f);
            Console.WriteLine($"  of those cut off: {sameFloor} at your own height (+-1m), "
                + $"{cutOffHeights.Count - sameFloor} on another level "
                + $"(height range {cutOffHeights.Min():f1}m to {cutOffHeights.Max():f1}m)");
        }
        // The map is local; this is the zone-wide question behind it. If the centre sits in a
        // small pocket while most of the mesh lives in a component it cannot reach, that is a
        // different (and much worse) problem than a few walled-off stalls.
        ReportComponents(navmesh.Mesh, origin);
        return 0;
    }

    private static void ReportComponents(DotRecast.Detour.DtNavMesh mesh, Vector3 origin)
    {
        var query = new DotRecast.Detour.DtNavMeshQuery(mesh);
        var filter = new DotRecast.Detour.DtQueryDefaultFilter();
        query.FindNearestPoly(origin.SystemToRecast(), new(2, 2, 2), filter, out var originRef, out _, out _);

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
        var components = new List<int>();
        int originComponent = -1, unreachableFlagged = 0;
        foreach (var start in all)
        {
            if (!seen.Add(start))
                continue;
            var size = 1;
            var stack = new Stack<long>();
            stack.Push(start);
            var contains = start == originRef;
            while (stack.Count > 0)
            {
                var next = stack.Pop();
                mesh.GetTileAndPolyByRefUnsafe(next, out var tile, out var poly);
                if ((poly.flags & global::Navmesh.Navmesh.FLAG_UNREACHABLE) != 0)
                    ++unreachableFlagged;
                for (int i = tile.polyLinks[poly.index]; i != DotRecast.Detour.DtNavMesh.DT_NULL_LINK; i = tile.links[i].next)
                {
                    var neighbour = tile.links[i].refs;
                    if (neighbour == 0 || !seen.Add(neighbour))
                        continue;
                    ++size;
                    if (neighbour == originRef)
                        contains = true;
                    stack.Push(neighbour);
                }
            }
            if (contains)
                originComponent = components.Count;
            components.Add(size);
        }

        var ordered = components.Select((size, index) => (size, index)).OrderByDescending(c => c.size).ToList();
        var rank = ordered.FindIndex(c => c.index == originComponent);
        Console.WriteLine();
        Console.WriteLine($"  zone-wide: {all.Count} walkable polys in {components.Count} disconnected components");
        Console.WriteLine($"  largest components: {string.Join(", ", ordered.Take(6).Select(c => c.size))}");
        Console.WriteLine(rank < 0
            ? "  you are not on any walkable poly"
            : $"  you are in component #{rank + 1} of {components.Count} ({components[originComponent]} polys, "
                + $"{100.0 * components[originComponent] / all.Count:f0}% of the zone)");
        Console.WriteLine($"  {unreachableFlagged} polys carry vnavmesh's FLAG_UNREACHABLE");
    }

    /// <summary>The same grid, but showing how much room there is rather than whether you can
    /// get there. A thin obstacle like a lamppost is invisible to a reachability map — you can
    /// always walk around it — yet it is exactly what a follower clips.</summary>
    public static int RunClearance(string[] args)
    {
        if (args.Length < 5 || !float.TryParse(args[2], out var x)
            || !float.TryParse(args[3], out var y) || !float.TryParse(args[4], out var z))
        {
            Console.WriteLine("usage: clearmap <zone-substring> <x> <y> <z> [radius] [step]");
            return 1;
        }
        var radius = args.Length > 5 && float.TryParse(args[5], out var r) ? r : 6f;
        var step = args.Length > 6 && float.TryParse(args[6], out var st) ? st : 0.5f;

        if (Probe.ResolveZoneOrReport(args[1]) is not { } entry)
            return 1;
        Console.WriteLine($"zone: {entry.Key}");
        Console.WriteLine($"centre: ({x:f1}, {y:f1}, {z:f1})  radius {radius:f0}m  step {step:f2}m");

        var navmesh = Probe.LoadServed(entry.Path, entry.Key);
        var query = new DotRecast.Detour.DtNavMeshQuery(navmesh.Mesh);
        var filter = new DotRecast.Detour.DtQueryDefaultFilter();

        int half = (int)MathF.Ceiling(radius / step);
        var rows = new List<string>();
        for (int gz = -half; gz <= half; ++gz)
        {
            var row = new System.Text.StringBuilder();
            for (int gx = -half; gx <= half; ++gx)
            {
                var p = new Vector3(x + gx * step, y, z + gz * step);
                var c = PathPadding.Clearance(query, filter, p);
                row.Append(c < 0 ? ' '
                    : c < 0.25f ? '0'
                    : c < 0.5f ? '1'
                    : c < 1.0f ? '2'
                    : c < 1.5f ? '3'
                    : '.');
            }
            rows.Add(row.ToString());
        }

        Console.WriteLine();
        foreach (var row in rows)
            Console.WriteLine("  " + row);
        Console.WriteLine();
        Console.WriteLine("  clearance to nearest obstacle:  0 = <0.25m   1 = <0.5m   2 = <1m   3 = <1.5m   . = 1.5m+   (blank) no mesh");
        return 0;
    }

    private static float Length(List<Vector3> path)
    {
        float length = 0;
        for (int i = 1; i < path.Count; ++i)
            length += Vector3.Distance(path[i - 1], path[i]);
        return length;
    }
}
