using DotRecast.Detour;
using Lumina;
using Lumina.Data.Files;
using Lumina.Data.Parsing.Layer;
using Mnemosyne.Core;
using Navmesh;
using System.Numerics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mnemosyne.Cli;

// Replay a dungeon route file leg by leg against the mesh the service would serve, and say
// for every leg that does not simply walk what it is: something our data already fixes,
// something it knows about but does not route yet, something the route file does by hand, or
// something nobody explains - a mesh bug, or a crossing we have no data for.
//
// Built for The Ghimlyt Dark: its two drops showed up as the only two legs that stop short, each
// right after an AutoMoveFor. Run over every imported route, the same replay is a table of what
// Mnemosyne covers per dungeon, before a character goes in.
//
// For a leg that stops short, each layer of data is tried in turn:
//   walk         the mesh alone completes it (a detour over 3x the straight line + 10 y is flagged)
//   link         the zone's override links complete it - what the service serves today
//   transition   a listed ride/slide/lift (the `transitions` spec) completes it, as a one-way hop
//                from its start to its end; the spec's later findPath opt-in would route this
//   evidence     a crossing characters were seen to make (evidence LinkCandidates) completes it
//   by hand      the route file crosses it itself: the leg starts at an AutoMoveFor / Jump step
//   interaction  the route interacts with something on the way (a lever lift, a door it opens)
//   UNEXPLAINED  none of the above
// Legs whose waypoints lie on collision from a `*navimesh*` layer are flagged too: that is
// designer navmesh scaffolding, and The Ghimlyt Dark's last drop has a 50 x 30 board of it that
// the mesh treats as floor.
//
// Reads Theseus route files (Steps) and AutoDuty ones (Actions, territory from the file name).
// Runs against this machine's meshes: a dungeon never entered here has none and is listed so.
//
// usage: Mnemosyne.Cli trace [route-file-or-directory] [--all]
//        (default: %APPDATA%\XIVLauncher\pluginConfigs\Theseus\paths; --all lists walking legs too)
public static class Trace
{
    private const float SnapRange = 5f;          // the service's own endpoint snap (MeshPathfinder.SnapExtents)
    private const float LandingRange = 10f;      // the transitions spec's fromSnap/toSnap
    private const float GateReach = 8f;          // a door or barrier this close to where a route stops is the suspect
    private static readonly string[] HandCrossings = ["AutoMoveFor", "Jump", "JumpTo"];

    private sealed record Step(int Index, string Verb, Vector3 Pos, string[] Args);

    // Steps: the ones with a position (legs run between them); Verbs: every step, for context -
    // an Interactable or DutySpecificCode between two positions is often the whole story
    private sealed record RouteFile(string Path, uint Territory, string Name, List<Step> Steps, List<string> Verbs);

    private sealed record Box(Matrix4x4 ToLocal, Vector3 HalfExtent, uint InstanceId);

    // something in the layout that opens and closes: a shared group with a door state, or a boss
    // arena's barrier (EObj 2002735 / 2002872, in 100 of 111 dungeons)
    private sealed record Gate(Vector3 Pos, string What);

    private static readonly uint[] ArenaBarriers = [2002735, 2002872];

    private sealed class Tally
    {
        public int Legs, Walk, Detour, Link, Transition, Evidence, ByHand, Interaction, Unexplained, OffMesh, OnScaffold;
    }

    public static int Run(string[] args)
    {
        var target = args.Length > 1 && !args[1].StartsWith("--") ? args[1]
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XIVLauncher", "pluginConfigs", "Theseus", "paths");
        var all = args.Contains("--all");
        var files = Directory.Exists(target) ? Directory.GetFiles(target, "*.json").OrderBy(f => f).ToArray()
            : File.Exists(target) ? [target] : [];
        if (files.Length == 0)
        {
            Console.WriteLine($"no route files at {target}");
            return 1;
        }
        if (GamePaths.FindSqpackDir() is not { } sqpack)
        {
            Console.WriteLine("no game install found");
            return 1;
        }
        var game = new GameData(sqpack);
        var territories = game.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>()!;
        var meshes = ServedMeshes();

        var summary = new List<(string Name, string Mesh, Tally T)>();
        foreach (var file in files)
        {
            if (ReadRoute(file) is not { } route)
                continue;
            var bg = territories.GetRowOrDefault(route.Territory)?.Bg.ExtractText();
            var bgKey = bg?.Replace('/', '_');
            Console.WriteLine();
            Console.WriteLine($"== {route.Name} (territory {route.Territory})  {Path.GetFileName(file)}");
            if (bg is not { Length: > 0 } || bgKey == null)
            {
                Console.WriteLine("   no bg path for this territory");
                continue;
            }
            if (!meshes.TryGetValue(bgKey, out var mesh))
            {
                Console.WriteLine("   no mesh on this machine - enter the duty once, or build it");
                summary.Add((route.Name, "none", new Tally()));
                continue;
            }
            var tally = TraceRoute(game, bg, route, mesh.Path, mesh.Key, mesh.Tier, all);
            summary.Add((route.Name, mesh.Tier, tally));
        }

        if (summary.Count > 1)
        {
            Console.WriteLine();
            Console.WriteLine($"{"route",-40} {"mesh",-8} {"legs",5} {"walk",5} {"detour",6} {"link",5} {"trans",5} {"evid",5} {"hand",5} {"inter",5} {"UNEXPL",6} {"offmsh",6} {"scaff",5}");
            foreach (var (name, tier, t) in summary)
                Console.WriteLine($"{Trim(name, 40),-40} {tier,-8} {t.Legs,5} {t.Walk,5} {t.Detour,6} {t.Link,5} {t.Transition,5} {t.Evidence,5} {t.ByHand,5} {t.Interaction,5} {t.Unexplained,6} {t.OffMesh,6} {t.OnScaffold,5}");
            var traced = summary.Where(s => s.Mesh != "none").ToList();
            Console.WriteLine();
            Console.WriteLine($"{summary.Count} routes, {traced.Count} with a mesh here: {traced.Sum(s => s.T.Legs)} legs, "
                + $"{traced.Sum(s => s.T.Unexplained)} unexplained, {traced.Sum(s => s.T.OffMesh)} off mesh, "
                + $"{traced.Count(s => s.T.Unexplained == 0 && s.T.OffMesh == 0)} routes fully accounted for");
        }
        return 0;
    }

    private static Tally TraceRoute(GameData game, string bg, RouteFile route, string meshPath, string key, string tier, bool all)
    {
        var navmesh = MeshCache.Load(meshPath);
        var overrides = OverrideStore.Load(key);
        if (!overrides.IsEmpty)
            OverrideStore.Apply(navmesh.Mesh, overrides);
        var pf = new MeshPathfinder(navmesh.Mesh);
        var query = new DtNavMeshQuery(navmesh.Mesh);
        var filter = new DtQueryDefaultFilter();

        // the transitions a findPath opt-in would route: carried kinds, one way, snapped at both ends
        var (paths, triggers, pops) = Transitions.Read(game, bg);
        var carried = new List<(OverrideLink Link, string Label)>();
        foreach (var t in Transitions.Classify(paths, triggers, pops, query).Where(t => t.Kind is "ride" or "slide" or "lift"))
        {
            // land where the game puts you: the spawn marker by the end, else the nearest mesh
            if (!pf.TryNearestGround(V(t.Landing ?? t.End), LandingRange, out var b))
                continue;
            // board where a character does: in a trigger circle. The path's own start can sit on a
            // pad the route cannot reach (Xelphatol's shuttle 1 starts 0.6 y from the boarding
            // spot, on a separate island)
            foreach (var enter in t.StartTriggers.Select(x => V(x.Pos)).DefaultIfEmpty(V(t.Start)))
                if (pf.TryNearestGround(enter, SnapRange, out var a))
                    carried.Add((OneWay(a, b), $"{t.Kind} cp:{t.InstanceIds[0]}"));
        }
        var evidence = EvidenceStore.Load(key).LinkCandidates
            .Select(e => (Link: OneWay(V(e.From), V(e.To)), Label: $"seen {e.Count}x"))
            .ToList();
        var (scaffold, gates) = ReadLayout(game, bg);

        Console.WriteLine($"   mesh: {tier} {key}  override links {overrides.Links.Count}, carried transitions {carried.Count}, "
            + $"evidence crossings {evidence.Count}, navimesh-layer boxes {scaffold.Count}");

        var links = overrides.Links;
        var withTransitions = links.Concat(carried.Select(c => c.Link)).ToList();
        var withEvidence = withTransitions.Concat(evidence.Select(e => e.Link)).ToList();

        var tally = new Tally();
        for (int i = 1; i < route.Steps.Count; ++i)
        {
            var (from, to) = (route.Steps[i - 1], route.Steps[i]);
            var straight = Vector3.Distance(from.Pos, to.Pos);
            if (straight < 0.5f)
                continue;
            ++tally.Legs;
            var head = $"   {from.Index,3}->{to.Index,-3} {Trim(to.Verb, 14),-14} {Fmt(from.Pos)} -> {Fmt(to.Pos)}";

            var startOn = OnMesh(query, filter, from.Pos);
            var endOn = OnMesh(query, filter, to.Pos);
            if (!startOn || !endOn)
            {
                ++tally.OffMesh;
                var which = !startOn && !endOn ? "both ends" : !startOn ? "start" : "target";
                var why = !startOn && HandCrossings.Contains(route.Steps[Math.Max(0, i - 2)].Verb) ? " (mid-crossing: the step before was a hand crossing)" : "";
                Console.WriteLine($"{head}  OFF MESH: {which} not within {SnapRange} y of walkable mesh{why}");
                continue;
            }

            var direct = pf.FindWalkPath(from.Pos, to.Pos);
            string verdict;
            List<Vector3>? walked = direct?.Waypoints;
            if (direct is { Partial: false })
            {
                var length = Length(direct.Waypoints);
                if (length > 3 * straight + 10)
                {
                    ++tally.Detour;
                    verdict = $"DETOUR {length:f0} y for {straight:f0} y straight";
                }
                else
                {
                    ++tally.Walk;
                    if (!all && !OnScaffold(scaffold, direct.Waypoints, out _))
                        continue;
                    verdict = $"walk {length:f0} y";
                }
            }
            else if (links.Count > 0 && pf.FindWalkPath(from.Pos, to.Pos, links) is { Partial: false } viaLink)
            {
                ++tally.Link;
                walked = viaLink.Waypoints;
                verdict = $"fixed by override link ({Length(viaLink.Waypoints):f0} y)";
            }
            else if (carried.Count > 0 && pf.FindWalkPath(from.Pos, to.Pos, withTransitions) is { Partial: false } viaTransition)
            {
                ++tally.Transition;
                walked = viaTransition.Waypoints;
                verdict = $"covered by transition {string.Join(", ", Used(carried, viaTransition.Waypoints))}";
            }
            else if (evidence.Count > 0 && pf.FindWalkPath(from.Pos, to.Pos, withEvidence) is { Partial: false } viaEvidence)
            {
                ++tally.Evidence;
                walked = viaEvidence.Waypoints;
                verdict = $"covered by field evidence ({string.Join(", ", Used(evidence, viaEvidence.Waypoints))})";
            }
            else
            {
                var end = direct?.Waypoints is { Count: > 0 } w ? w[^1] : from.Pos;
                var gap = $"stops {Horizontal(end, to.Pos):f0} y short across, {to.Pos.Y - end.Y:+0;-0} y, at {Fmt(end)}";
                if (HandCrossings.Contains(from.Verb))
                {
                    ++tally.ByHand;
                    verdict = $"by hand ({from.Verb} at step {from.Index}): {gap}";
                }
                else if (route.Verbs.Skip(from.Index + 1).Take(to.Index - from.Index - 1).Contains("Interactable"))
                {
                    // a lever, a lift, a door the route opens: the crossing is an interaction the
                    // mesh cannot know about (the spec's deferred `interact` kind)
                    ++tally.Interaction;
                    verdict = $"after an interaction ({string.Join(" ", route.Verbs.Skip(from.Index + 1).Take(to.Index - from.Index - 1))}): {gap}";
                }
                else
                {
                    ++tally.Unexplained;
                    var between = route.Verbs.Skip(from.Index + 1).Take(to.Index - from.Index - 1).ToList();
                    verdict = $"UNEXPLAINED: {gap}"
                        + (between.Count > 0 ? $"; steps between: {string.Join(" ", between)}" : $"; leg starts at {from.Verb}");
                    if (gates.Where(g => Vector3.Distance(g.Pos, end) <= GateReach).MinBy(g => Vector3.Distance(g.Pos, end)) is { } gate)
                        verdict += $"; {gate.What} {Vector3.Distance(gate.Pos, end):f0} y from where it stops";
                    if (FurthestHop(pf, from.Pos, to.Pos, carried.Concat(evidence).ToList(), end) is { } hop)
                        verdict += $"; {hop.Label} gets to {Fmt(hop.Stop)}, {Horizontal(hop.Stop, to.Pos):f0} y across and {to.Pos.Y - hop.Stop.Y:+0;-0} y from the target";
                }
            }

            if (walked != null && OnScaffold(scaffold, walked, out var box))
            {
                ++tally.OnScaffold;
                verdict += $"  [on navimesh-layer collision, instance {box}]";
            }
            Console.WriteLine($"{head}  {verdict}");
        }

        Console.WriteLine($"   {tally.Legs} legs: {tally.Walk} walk, {tally.Detour} detour, {tally.Link} link, {tally.Transition} transition, "
            + $"{tally.Evidence} evidence, {tally.ByHand} by hand, {tally.Interaction} after interaction, {tally.Unexplained} UNEXPLAINED, {tally.OffMesh} off mesh"
            + (tally.OnScaffold > 0 ? $"; {tally.OnScaffold} on navimesh-layer collision" : ""));
        return tally;
    }

    /// <summary>The mesh the service would load for each bg key, in its order: a live capture,
    /// then vnavmesh's cache, then Mnemosyne's offline build. Largest file within a tier.</summary>
    private static Dictionary<string, (string Path, string Key, string Tier)> ServedMeshes()
    {
        var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mnemosyne");
        var found = new Dictionary<string, (string Path, string Key, string Tier)>();
        foreach (var (dir, tier) in new (string?, string)[] { (Path.Combine(appData, "captured"), "captured"), (null, "vnav"), (Path.Combine(appData, "built"), "built") })
        {
            var inTier = new Dictionary<string, MeshCacheEntry>();
            foreach (var e in MeshCache.Enumerate(dir).Where(e => e.IsSupported))
            {
                var bgKey = OverrideStore.BgKey(e.Key);
                if (!inTier.TryGetValue(bgKey, out var have) || new FileInfo(e.Path).Length > new FileInfo(have.Path).Length)
                    inTier[bgKey] = e;
            }
            foreach (var (bgKey, e) in inTier)
                found.TryAdd(bgKey, (e.Path, e.Key, tier));
        }
        return found;
    }

    private static RouteFile? ReadRoute(string file)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var root = doc.RootElement;
            var steps = new List<Step>();
            if (root.TryGetProperty("Steps", out var theseus))
            {
                int index = 0;
                var verbs = new List<string>();
                foreach (var s in theseus.EnumerateArray())
                {
                    verbs.Add(s.GetProperty("RawVerb").GetString() ?? "");
                    var p = s.GetProperty("Position");
                    if (p.TryGetProperty("IsSet", out var set) && set.GetBoolean())
                        steps.Add(new Step(index, s.GetProperty("RawVerb").GetString() ?? "", Pos(p), Strings(s, "Arguments")));
                    ++index;
                }
                return new RouteFile(file, root.GetProperty("TerritoryId").GetUInt32(),
                    root.TryGetProperty("Name", out var n) ? n.GetString() ?? "" : Path.GetFileNameWithoutExtension(file), steps, verbs);
            }
            if (root.TryGetProperty("Actions", out var autoDuty))
            {
                // AutoDuty names its files "(1174) The Ghimlyt Dark.json"
                var m = Regex.Match(Path.GetFileName(file), @"^\((\d+)\)\s*(.*)\.json$");
                if (!m.Success)
                    return null;
                int index = 0;
                var verbs = new List<string>();
                foreach (var a in autoDuty.EnumerateArray())
                {
                    verbs.Add(a.GetProperty("Name").GetString() ?? "");
                    var pos = Pos(a.GetProperty("Position"));
                    if (pos != Vector3.Zero)
                        steps.Add(new Step(index, a.GetProperty("Name").GetString() ?? "", pos, Strings(a, "Arguments")));
                    ++index;
                }
                return new RouteFile(file, uint.Parse(m.Groups[1].Value), m.Groups[2].Value, steps, verbs);
            }
        }
        catch (Exception e) when (e is IOException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            Console.WriteLine($"skipping {Path.GetFileName(file)}: {e.Message}");
        }
        return null;
    }

    /// <summary>Collision boxes in layers named like navimesh (designer scaffolding for the
    /// game's own navigation, which the mesh builder takes as solid), and the gates: doors and
    /// arena barriers, which a mesh can have baked shut.</summary>
    private static (List<Box> Scaffold, List<Gate> Gates) ReadLayout(GameData game, string bg)
    {
        var boxes = new List<Box>();
        var gates = new List<Gate>();
        var basePath = "bg/" + bg[..(bg.IndexOf("/level/", StringComparison.Ordinal) + 1)];
        foreach (var lgbName in new[] { "bg.lgb", "planmap.lgb", "planevent.lgb" })
        {
            LgbFile? lgb;
            try { lgb = game.GetFile<LgbFile>(basePath + "level/" + lgbName); }
            catch { continue; }
            if (lgb == null)
                continue;
            foreach (var layer in lgb.Layers)
                foreach (var obj in layer.InstanceObjects)
                {
                    var at = new Vector3(obj.Transform.Translation.X, obj.Transform.Translation.Y, obj.Transform.Translation.Z);
                    if (obj.Object is LayerCommon.SharedGroupInstanceObject sg && sg.InitialDoorState is DoorState.Open or DoorState.Closed)
                        gates.Add(new Gate(at, $"door {Path.GetFileNameWithoutExtension(sg.AssetPath)} ({sg.InitialDoorState} at start)"));
                    else if (obj.Object is LayerCommon.EventInstanceObject ev && ArenaBarriers.Contains(ev.ParentData.BaseId))
                        gates.Add(new Gate(at, $"arena barrier EObj {ev.ParentData.BaseId}"));
                    if (obj.Object is not LayerCommon.CollisionBoxInstanceObject
                        || !layer.Name.Contains("navimesh", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var tr = obj.Transform;
                    // shapes are unit (+-1) scaled; test in the unscaled frame against the scale
                    var placed = Matrix4x4.CreateRotationX(tr.Rotation.X) * Matrix4x4.CreateRotationY(tr.Rotation.Y)
                        * Matrix4x4.CreateRotationZ(tr.Rotation.Z) * Matrix4x4.CreateTranslation(tr.Translation.X, tr.Translation.Y, tr.Translation.Z);
                    if (Matrix4x4.Invert(placed, out var toLocal))
                        boxes.Add(new Box(toLocal, new Vector3(tr.Scale.X, tr.Scale.Y, tr.Scale.Z), obj.InstanceId));
                }
        }
        return (boxes, gates);
    }

    private static bool OnScaffold(List<Box> boxes, List<Vector3> waypoints, out uint instance)
    {
        instance = 0;
        foreach (var b in boxes)
            foreach (var w in waypoints)
            {
                var l = Vector3.Transform(w, b.ToLocal);
                // a yalm of margin: a board has no thickness, and the mesh sits on top of it
                if (MathF.Abs(l.X) <= b.HalfExtent.X + 1 && MathF.Abs(l.Y) <= b.HalfExtent.Y + 1 && MathF.Abs(l.Z) <= b.HalfExtent.Z + 1)
                {
                    instance = b.InstanceId;
                    return true;
                }
            }
        return false;
    }

    /// <summary>For a leg nothing completes: of the hops whose entry the start can walk to, the
    /// one whose landing gets closest to the target, and where walking on from it stops. Says
    /// "the ride works, the landing is cut off" instead of just "stops short".</summary>
    private static (string Label, Vector3 Stop)? FurthestHop(MeshPathfinder pf, Vector3 from, Vector3 to,
        List<(OverrideLink Link, string Label)> hops, Vector3 directStop)
    {
        (string Label, Vector3 Stop)? best = null;
        var bestLeft = Vector3.Distance(directStop, to) - 10; // must beat the plain walk by 10 y
        foreach (var (link, label) in hops)
        {
            if (pf.FindWalkPath(from, V(link.From)) is not { Partial: false })
                continue;
            var stop = pf.FindWalkPath(V(link.To), to) is { Waypoints.Count: > 0 } on ? on.Waypoints[^1] : V(link.To);
            var left = Vector3.Distance(stop, to);
            if (left < bestLeft)
                (best, bestLeft) = ((label, stop), left);
        }
        return best;
    }

    /// <summary>Which of these one-way links a stitched route went through: its landing is one
    /// of the route's waypoints (the stitch starts the next leg there).</summary>
    private static IEnumerable<string> Used(List<(OverrideLink Link, string Label)> links, List<Vector3> waypoints) =>
        links.Where(l => waypoints.Any(w => Vector3.Distance(w, V(l.Link.To)) < 1.5f)).Select(l => l.Label).Distinct().DefaultIfEmpty("(unidentified)");

    private static bool OnMesh(DtNavMeshQuery query, IDtQueryFilter filter, Vector3 p)
    {
        query.FindNearestPoly(p.SystemToRecast(), new(SnapRange, SnapRange, SnapRange), filter, out var poly, out _, out _);
        return poly != 0;
    }

    private static OverrideLink OneWay(Vector3 a, Vector3 b) => new() { From = [a.X, a.Y, a.Z], To = [b.X, b.Y, b.Z], Bidirectional = false };

    private static Vector3 Pos(JsonElement p) => new(p.GetProperty("X").GetSingle(), p.GetProperty("Y").GetSingle(), p.GetProperty("Z").GetSingle());

    private static string[] Strings(JsonElement e, string name) =>
        e.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array
            ? [.. a.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : x.ToString())]
            : [];

    private static float Length(List<Vector3> path)
    {
        float length = 0;
        for (int i = 1; i < path.Count; ++i)
            length += Vector3.Distance(path[i - 1], path[i]);
        return length;
    }

    private static float Horizontal(Vector3 a, Vector3 b) => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));
    private static Vector3 V(float[] a) => new(a[0], a[1], a[2]);
    private static string Fmt(Vector3 v) => $"({v.X:f0}, {v.Y:f0}, {v.Z:f0})";
    private static string Trim(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "~";
}
