using DotRecast.Detour;
using Lumina;
using Lumina.Data.Files;
using Lumina.Data.Parsing.Layer;
using Mnemosyne.Core;
using Navmesh;
using System.Numerics;
using System.Text.Json;

namespace Mnemosyne.Cli;

// Every scripted path in every dungeon, classified by what it does to a character on the route.
//
// A ClientPath is the game moving something along fixed points. Some of those carry the player -
// Xelphatol's wind shuttles, The Burn's slide into the Scorpion's Den - and some are things that
// come down onto the route and knock you over: The Burn's boulders. Both are invisible to a navmesh
// (one is a crossing the mesh cannot walk, the other is timed, not geometry), and both are laid out
// in the zone files, so a duty solver can know them before the run.
//
// What tells them apart is how they start:
//   - a small trigger volume at the start means something you step into to be carried
//     (ride when roughly level, slide when it descends, lift when it climbs);
//   - no trigger at the start, and a descent of several yalms, means something that starts above
//     the route and ends on it - a hazard crossing;
//   - anything else (level, untriggered) is ambient: NPC walk loops, effects;
//   - a descending, untriggered path that ends off the walkable mesh is "unclear" (only when a
//     mesh is available to say so) - it comes down, but not onto anywhere a character stands.
// Whether the start has walkable ground under it does not separate them - a boulder in The Burn
// starts on a meshed cliff ledge. Parallel paths with the same ends (a slide has four lanes) are
// merged into one transition.
//
// Where a mesh for the zone is on this machine, each end is also checked against it.
//
// usage: Mnemosyne.Cli transitions [zone-name-or-bg-substring] [--out=<dir>]
//        (no zone = all dungeons)
public static class Transitions
{
    private const float TriggerReachXZ = 4f;
    private const float TriggerReachY = 4f;
    private const float SteepEnough = 5f;
    private const float SameLane = 5f;
    private const float MinCarry = 8f;
    private const float LandingReach = 6f;

    public sealed record Trigger(float[] Pos, string Shape, float[] Scale, uint InstanceId);

    // Landing: a spawn marker (PopRange) near the path's end. A ride hands the character over
    // there, not at the last control point - Xelphatol's second shuttle ends 2.8 y above a sealed
    // 15 m2 pad, and its marker sits on the walkway beside it, which walks on to the boss.
    public sealed record Transition(string Kind, int Lanes, float[] Start, float[] End, float Length, float Climb,
        List<uint> InstanceIds, string Layer, List<Trigger> StartTriggers, bool? StartOnMesh, bool? EndOnMesh,
        float[]? Landing = null);

    public sealed record DutyTransitions(string Name, string Bg, string? Mesh, List<Transition> Transitions);

    internal sealed record RawPath(Vector3[] Points, uint InstanceId, string Layer);

    public static int Run(string[] args)
    {
        if (GamePaths.FindSqpackDir() is not { } sqpack)
        {
            Console.WriteLine("no game install found");
            return 1;
        }
        var hint = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : null;
        var outDir = args.FirstOrDefault(a => a.StartsWith("--out="))?[6..] ?? Path.Combine("scratch", "transitions");
        Directory.CreateDirectory(outDir);

        var zones = ZoneNames.All
            .Where(z => z.Value.Bg is { Length: > 0 } bg && (hint != null
                ? z.Key.Contains(hint, StringComparison.OrdinalIgnoreCase) || z.Value.Name.Contains(hint, StringComparison.OrdinalIgnoreCase)
                : bg.Contains("/dun/")))
            .GroupBy(z => z.Value.Bg).Select(g => g.First())
            .OrderBy(z => z.Value.Bg)
            .ToList();

        var game = new GameData(sqpack);
        var meshes = LocalMeshes();
        var json = new JsonSerializerOptions { WriteIndented = true };
        var totals = new Dictionary<string, int>();
        var dutiesWith = new Dictionary<string, int>();
        int carried = 0, landed = 0;

        Console.WriteLine($"{"duty",-38} {"ride",5} {"slide",6} {"lift",5} {"hazard",7} {"unclear",8} {"boss",5} {"ambient",8}  mesh");
        foreach (var (key, zone) in zones)
        {
            var (paths, triggers, pops) = Read(game, zone.Bg);
            var mesh = meshes.GetValueOrDefault(key);
            DtNavMeshQuery? query = null;
            if (mesh != null)
            {
                try { query = new DtNavMeshQuery(MeshCache.Load(mesh).Mesh); }
                catch { mesh = null; }
            }

            var transitions = Classify(paths, triggers, pops, query);
            var duty = new DutyTransitions(zone.Name, zone.Bg, mesh == null ? null : Path.GetFileName(mesh), transitions);
            File.WriteAllText(Path.Combine(outDir, key + ".json"), JsonSerializer.Serialize(duty, json));

            var counts = transitions.GroupBy(t => t.Kind).ToDictionary(g => g.Key, g => g.Count());
            carried += transitions.Count(t => t.Kind is "ride" or "slide" or "lift");
            landed += transitions.Count(t => t.Kind is "ride" or "slide" or "lift" && t.Landing != null);
            foreach (var (kind, n) in counts)
            {
                totals[kind] = totals.GetValueOrDefault(kind) + n;
                dutiesWith[kind] = dutiesWith.GetValueOrDefault(kind) + 1;
            }
            string C(string k) => counts.TryGetValue(k, out var n) ? n.ToString() : "-";
            Console.WriteLine($"{Trim(zone.Name, 38),-38} {C("ride"),5} {C("slide"),6} {C("lift"),5} {C("hazard"),7} {C("unclear"),8} {C("boss"),5} {C("ambient"),8}  {(mesh == null ? "" : "yes")}");
        }

        Console.WriteLine();
        Console.WriteLine($"{zones.Count} duties. transitions by kind (total, duties that have one):");
        foreach (var kind in new[] { "ride", "slide", "lift", "hazard", "unclear", "boss", "ambient" })
            Console.WriteLine($"  {kind,-8} {totals.GetValueOrDefault(kind),5}  in {dutiesWith.GetValueOrDefault(kind)} duties");
        Console.WriteLine($"  carried (ride/slide/lift) with a spawn marker within {LandingReach} y of the end: {landed} of {carried}");
        Console.WriteLine($"written: {Path.GetFullPath(outDir)}");
        return 0;
    }

    internal static List<Transition> Classify(List<RawPath> paths, List<(Vector3 Pos, Trigger T)> triggers, List<Vector3> pops, DtNavMeshQuery? query)
    {
        var result = new List<Transition>();
        foreach (var p in paths)
        {
            var start = p.Points[0];
            var end = p.Points[^1];
            var length = 0f;
            for (int i = 1; i < p.Points.Length; ++i)
                length += Vector3.Distance(p.Points[i - 1], p.Points[i]);
            var climb = end.Y - start.Y;

            var atStart = triggers
                .Where(t => Horizontal(t.Pos, start) <= TriggerReachXZ && MathF.Abs(t.Pos.Y - start.Y) <= TriggerReachY)
                .Select(t => t.T).ToList();

            var kind = atStart.Count > 0
                ? climb <= -SteepEnough ? "slide" : climb >= SteepEnough ? "lift" : "ride"
                : climb <= -SteepEnough ? "hazard" : "ambient";
            // Boss arenas are dense with trigger boxes and short scripted paths (Tender Valley's
            // third boss has two dozen), which the rules above would read as rides. They are the
            // fight's own mechanics - the boss handler's business, not the route's.
            if (p.Layer.Contains("boss", StringComparison.OrdinalIgnoreCase))
                kind = "boss";
            // nothing carries a character a couple of yalms: a "ride" that short is a marker
            else if (kind is "ride" or "slide" or "lift" && length < MinCarry)
                kind = "ambient";

            var startOnMesh = OnMesh(query, start);
            var endOnMesh = OnMesh(query, end);
            // a hazard has to come down somewhere a character can stand; one that ends in the air
            // or off the walkable mesh is scenery (an entrance fly-in, a falling effect)
            if (kind == "hazard" && endOnMesh == false)
                kind = "unclear";
            // parallel lanes of one transition: same kind, both ends close
            var lane = result.FindIndex(r => r.Kind == kind
                && Vector3.Distance(V(r.Start), start) <= SameLane && Vector3.Distance(V(r.End), end) <= SameLane);
            if (lane >= 0)
            {
                var r = result[lane];
                r.InstanceIds.Add(p.InstanceId);
                foreach (var t in atStart.Where(t => r.StartTriggers.All(x => x.InstanceId != t.InstanceId)))
                    r.StartTriggers.Add(t);
                result[lane] = r with { Lanes = r.Lanes + 1 };
                continue;
            }

            var landing = pops.Where(q => Vector3.Distance(q, end) <= LandingReach).OrderBy(q => Vector3.Distance(q, end)).Select(A).FirstOrDefault();
            result.Add(new Transition(kind, 1, A(start), A(end), MathF.Round(length, 1), MathF.Round(climb, 1),
                [p.InstanceId], p.Layer, atStart, startOnMesh, endOnMesh, landing));
        }
        return [.. result.OrderBy(t => t.Kind == "ambient").ThenBy(t => t.Kind).ThenBy(t => t.Start[0])];
    }

    internal static (List<RawPath> Paths, List<(Vector3, Trigger)> Triggers, List<Vector3> Pops) Read(GameData game, string bg)
    {
        var paths = new List<RawPath>();
        var triggers = new List<(Vector3, Trigger)>();
        var pops = new List<Vector3>();
        var basePath = "bg/" + bg[..(bg.IndexOf("/level/", StringComparison.Ordinal) + 1)];
        foreach (var lgbName in new[] { "bg.lgb", "planmap.lgb", "planevent.lgb", "planlive.lgb" })
        {
            LgbFile? lgb;
            try { lgb = game.GetFile<LgbFile>(basePath + "level/" + lgbName); }
            catch { continue; }
            if (lgb == null)
                continue;
            foreach (var layer in lgb.Layers)
            {
                foreach (var obj in layer.InstanceObjects)
                {
                    var tr = obj.Transform;
                    var pos = new Vector3(tr.Translation.X, tr.Translation.Y, tr.Translation.Z);
                    if (obj.AssetType.ToString() == "PopRange")
                        pops.Add(pos);
                    switch (obj.Object)
                    {
                        case LayerCommon.ClientPathInstanceObject cp:
                            var world = World(tr);
                            var points = (cp.ParentData.ControlPointsArray ?? [])
                                .Select(c => Vector3.Transform(new Vector3(c.Translation.X, c.Translation.Y, c.Translation.Z), world))
                                .ToArray();
                            if (points.Length >= 2)
                                paths.Add(new RawPath(points, obj.InstanceId, $"{lgbName}/{layer.Name}"));
                            break;
                        case LayerCommon.EventRangeInstanceObject er:
                            triggers.Add((pos, new Trigger(A(pos), er.ParentData.TriggerBoxShape.ToString().Replace("TriggerBoxShape", ""),
                                [MathF.Round(tr.Scale.X, 2), MathF.Round(tr.Scale.Y, 2), MathF.Round(tr.Scale.Z, 2)], obj.InstanceId)));
                            break;
                    }
                }
            }
        }
        return (paths, triggers, pops);
    }

    /// <summary>Meshes for zones on this machine, by bg key: vnavmesh's cache and Mnemosyne's
    /// stores, current mesh version only, the largest file per zone.</summary>
    private static Dictionary<string, string> LocalMeshes()
    {
        var dirs = new List<string?> { null };
        var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mnemosyne");
        dirs.Add(Path.Combine(appData, "captured"));
        dirs.Add(Path.Combine(appData, "built"));
        var found = new Dictionary<string, string>();
        foreach (var dir in dirs)
        {
            foreach (var e in MeshCache.Enumerate(dir).Where(e => e.IsSupported))
            {
                var bgKey = OverrideStore.BgKey(e.Key);
                if (!found.TryGetValue(bgKey, out var have) || new FileInfo(e.Path).Length > new FileInfo(have).Length)
                    found[bgKey] = e.Path;
            }
        }
        return found;
    }

    private static bool? OnMesh(DtNavMeshQuery? query, Vector3 p)
    {
        if (query == null)
            return null;
        // 5 y vertically: a ride carries you a few yalms above the floor it sets you down on
        query.FindNearestPoly(p.SystemToRecast(), new(2, 5, 2), new DtQueryDefaultFilter(), out var poly, out _, out _);
        return poly != 0;
    }

    // S * Rx * Ry * Rz * T, as LgbSceneReader. Yaw-pitch-roll order agrees only while X and Z are
    // zero; Xelphatol's second shuttle is stored as (-180, 79, -180) and came out mirrored.
    public static Matrix4x4 World(Lumina.Data.Parsing.Common.Transformation tr) =>
        Matrix4x4.CreateScale(tr.Scale.X, tr.Scale.Y, tr.Scale.Z)
        * Matrix4x4.CreateRotationX(tr.Rotation.X) * Matrix4x4.CreateRotationY(tr.Rotation.Y) * Matrix4x4.CreateRotationZ(tr.Rotation.Z)
        * Matrix4x4.CreateTranslation(tr.Translation.X, tr.Translation.Y, tr.Translation.Z);

    private static float Horizontal(Vector3 a, Vector3 b) => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));
    private static float[] A(Vector3 v) => [MathF.Round(v.X, 1), MathF.Round(v.Y, 1), MathF.Round(v.Z, 1)];
    private static Vector3 V(float[] a) => new(a[0], a[1], a[2]);
    private static string Trim(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "â€¦";
}
