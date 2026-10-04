using DotRecast.Detour;
using Mnemosyne.Core;
using Navmesh;
using System.Numerics;

namespace Mnemosyne.Cli;

// Raising a zone's step height (AgentMaxClimb) joins more than the stairs it was raised for.
// Eulmore went to 1.0 y for the Canopy's spiral stairs (Z0820Eulmore), and the same allowance joined a
// floor to the side of a Mainstay ramp through three thin triangles climbing 1 y in 0.7 m - a step no
// character can take. Grakur stalled there four times (2026-10-02). The rule since then: whenever a
// customization raises AgentMaxClimb, comb the zone with this before shipping it.
//
// It builds the zone twice from game files, at the base step height and at the raised one, and finds
// the steep "riser" polys that only the raised build has. Each cluster is then judged by the base mesh:
//   ledge shortcut   the base mesh already walks from the cluster's bottom to its top by a short way
//                    round. The raised step is almost certainly one the character cannot take: prune it
//                    (an override block box around the cluster).
//   new connection   the base mesh has no way, or only a long one. This is what the raise was for (a
//                    stair), or a step to check in game.
//
// usage: Mnemosyne.Cli climbcomb <zone-substring-or-.navmesh> [--climb=1.0] [--base=0.5] [--detour=30]
public static class ClimbComb
{
    private const float RiserRise = 0.6f;     // a poly climbing at least this...
    private const float RiserSlope = 0.5f;    // ...at 1 in 2 or steeper is a riser (a ramp is gentler)
    private const float ClusterReach = 3f;

    public static int Run(string[] args)
    {
        string? Flag(string name) => args.FirstOrDefault(a => a.StartsWith(name + "="))?[(name.Length + 1)..];
        if (args.Length < 2 || Probe.ResolveForBuild(args[1]) is not { } entry)
        {
            Console.WriteLine("usage: climbcomb <zone-substring-or-.navmesh> [--climb=1.0] [--base=0.5] [--detour=30]");
            return 1;
        }
        if (ZoneNames.Lookup(entry.Key)?.Bg is not { Length: > 0 } bg || GamePaths.FindSqpackDir() is not { } sqpack)
        {
            Console.WriteLine("no bg path or game install for this zone");
            return 1;
        }
        var game = new Lumina.GameData(sqpack);
        var territory = Mnemosyne.Builder.ZoneBuilder.TerritoryIdFor(game, bg);
        var customized = global::Navmesh.NavmeshCustomizationRegistry.ForTerritory(territory).Settings.AgentMaxClimb;
        var climb = float.TryParse(Flag("--climb"), out var c) ? c : customized;
        var baseClimb = float.TryParse(Flag("--base"), out var b) ? b : new global::Navmesh.NavmeshSettings().AgentMaxClimb;
        var detourLimit = float.TryParse(Flag("--detour"), out var d) ? d : 30f;
        if (climb <= baseClimb)
        {
            Console.WriteLine($"step height {climb} is not above the base {baseClimb}: nothing to comb");
            return 0;
        }

        Console.WriteLine($"zone: {entry.Key} (territory {territory}) - combing step height {climb} against {baseClimb}");
        var flyable = Mnemosyne.Builder.ZoneBuilder.IsFlyable(game, bg);
        var baseMesh = Mnemosyne.Builder.ZoneBuilder.Build(game, bg, territory, flyable, null, s => s.AgentMaxClimb = baseClimb).Mesh;
        var raisedMesh = Mnemosyne.Builder.ZoneBuilder.Build(game, bg, territory, flyable, null, s => s.AgentMaxClimb = climb).Mesh;

        var baseQuery = new DtNavMeshQuery(baseMesh);
        var filter = new DtQueryDefaultFilter();
        // New risers are the raised build's risers with no riser of the base build beside them.
        // (Not "no base ground nearby": a ramp's broad slope passes within a few tenths of the
        // notch Grakur stalled at, and that test let it through.)
        var risers = ClimbRisers.Added(baseMesh, raisedMesh)
            .Select(r => (r.Center, Low: r.Bottom.Y, High: r.Top.Y, r.Bottom, r.Top))
            .ToList();
        var regions = global::Navmesh.NavmeshCustomizationRegistry.ForTerritory(territory).ClimbRegions;
        bool InRegion(Vector3 p) => regions.Any(r => MathF.Abs(p.X - r.Center.X) <= r.HalfExtent.X
            && MathF.Abs(p.Y - r.Center.Y) <= r.HalfExtent.Y && MathF.Abs(p.Z - r.Center.Z) <= r.HalfExtent.Z);
        if (regions.Length > 0)
            Console.WriteLine($"the customization keeps the raise to {regions.Length} region(s); risers outside are blocked at build time (marked 'blocked')");

        // cluster
        var clusters = new List<List<int>>();
        var seen = new bool[risers.Count];
        for (int i = 0; i < risers.Count; ++i)
        {
            if (seen[i])
                continue;
            var group = new List<int> { i };
            seen[i] = true;
            for (int k = 0; k < group.Count; ++k)
                for (int j = 0; j < risers.Count; ++j)
                    if (!seen[j] && Vector3.Distance(risers[group[k]].Center, risers[j].Center) <= ClusterReach)
                    {
                        seen[j] = true;
                        group.Add(j);
                    }
            clusters.Add(group);
        }

        var basePf = new MeshPathfinder(baseMesh);
        Console.WriteLine();
        Console.WriteLine($"{risers.Count} riser polys only the raised build has, in {clusters.Count} clusters:");
        Console.WriteLine($"  {"verdict",-16} {"centre",-24} {"rise",5} {"polys",5}  base mesh bottom -> top          override box (center / half-extent)");
        int shortcuts = 0;
        foreach (var group in clusters.OrderBy(g => risers[g[0]].Center.X))
        {
            var members = group.Select(i => risers[i]).ToList();
            var bottom = members.MinBy(m => m.Low).Bottom;
            var top = members.MaxBy(m => m.High).Top;
            var centre = members.Aggregate(Vector3.Zero, (a, m) => a + m.Center) / members.Count;
            // Both ends must be ground the base mesh already had, found tightly: a loose snap lets
            // a riser's top land on the floor beside it and "walk round in 0 m". When the top (or
            // bottom) had no ground before, the riser is part of something new - a staircase.
            var baseBottom = Ground(baseQuery, filter, bottom);
            var baseTop = Ground(baseQuery, filter, top);
            var route = baseBottom is { } bb && baseTop is { } bt ? basePf.FindWalkPath(bb, bt) : null;
            var length = route is { Partial: false } ? route.Waypoints.Skip(1).Zip(route.Waypoints, Vector3.Distance).Sum() : float.PositiveInfinity;
            var shortcut = length <= detourLimit;
            if (shortcut)
                ++shortcuts;
            var min = new Vector3(members.Min(m => m.Center.X), members.Min(m => m.Center.Y), members.Min(m => m.Center.Z));
            var max = new Vector3(members.Max(m => m.Center.X), members.Max(m => m.Center.Y), members.Max(m => m.Center.Z));
            var boxCentre = (min + max) / 2;
            var half = (max - min) / 2 + new Vector3(0.3f);
            var regionNote = regions.Length == 0 ? "" : InRegion(centre) ? "  kept (in a climb region)" : "  blocked";
            Console.WriteLine($"  {(shortcut ? "ledge shortcut" : "new connection"),-16} ({centre.X,6:f1},{centre.Y,6:f1},{centre.Z,6:f1}) {top.Y - bottom.Y,5:f1} {members.Count,5}  "
                + $"{(shortcut ? $"walks round in {length,4:f0} m" : baseBottom == null || baseTop == null ? "new ground (no floor there before)" : route is { Partial: false } ? $"long way round, {length:f0} m" : "no way round"),-34}  "
                + $"[{boxCentre.X:f2}, {boxCentre.Y:f2}, {boxCentre.Z:f2}] / [{half.X:f2}, {half.Y:f2}, {half.Z:f2}]{regionNote}");
        }
        Console.WriteLine();
        Console.WriteLine($"{shortcuts} ledge shortcut(s) to prune; {clusters.Count - shortcuts} new connection(s) - the stairs the raise was for, or steps to check in game");
        return 0;
    }

    private static Vector3? Ground(DtNavMeshQuery query, IDtQueryFilter filter, Vector3 p)
    {
        query.FindNearestPoly(p.SystemToRecast(), new(0.75f, 0.5f, 0.75f), filter, out var poly, out var at, out _);
        return poly == 0 ? null : at.RecastToSystem();
    }

    private static IEnumerable<(Vector3 Center, List<Vector3> Verts)> Polys(DtNavMesh mesh)
    {
        for (int t = 0; t < mesh.GetMaxTiles(); ++t)
        {
            var tile = mesh.GetTile(t);
            var data = tile?.data;
            if (data?.header == null)
                continue;
            for (int p = 0; p < data.header.polyCount; ++p)
            {
                var poly = data.polys[p];
                if (poly.GetPolyType() != 0)
                    continue;
                var verts = new List<Vector3>(poly.vertCount);
                for (int i = 0; i < poly.vertCount; ++i)
                {
                    var vi = poly.verts[i] * 3;
                    verts.Add(new Vector3(data.verts[vi], data.verts[vi + 1], data.verts[vi + 2]));
                }
                yield return (verts.Aggregate(Vector3.Zero, (a, v) => a + v) / verts.Count, verts);
            }
        }
    }
}
