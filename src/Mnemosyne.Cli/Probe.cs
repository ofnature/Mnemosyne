using Mnemosyne.Core;
using System.Numerics;

namespace Mnemosyne.Cli;

// "The mesh is solid where the world is open" is the hardest class of navmesh bug to argue
// about, because every symptom is indirect: a follower wedges, a route detours 200 y, a
// consumer retries forever. This answers the direct question at a point — is there mesh
// here, can you cross it, and if not, how far does the detour go.
//
// usage: Mnemosyne.Cli probe <zone-substring> <x> <y> <z> [radius]
public static class Probe
{
    public static int Run(string[] args)
    {
        if (args.Length < 5 || !float.TryParse(args[2], out var x)
            || !float.TryParse(args[3], out var y) || !float.TryParse(args[4], out var z))
        {
            Console.WriteLine("usage: probe <zone-substring> <x> <y> <z> [radius]");
            return 1;
        }
        var radius = args.Length > 5 && float.TryParse(args[5], out var r) ? r : 15f;
        var point = new Vector3(x, y, z);

        if (ResolveZoneOrReport(args[1]) is not { } entry)
            return 1;
        Console.WriteLine($"zone: {entry.Key}");
        Console.WriteLine($"point: ({x:f1}, {y:f1}, {z:f1})  probe radius {radius:f0}m");
        Console.WriteLine();

        var navmesh = LoadServed(entry.Path, entry.Key);
        var pf = new MeshPathfinder(navmesh.Mesh);

        Console.WriteLine(pf.TryNearestGround(point, 20, out var ground)
            ? $"nearest walkable surface: ({ground.X:f1}, {ground.Y:f1}, {ground.Z:f1}), {Vector3.Distance(ground, point):f1}m away"
            : "nearest walkable surface: none within 20m");

        // Cross the point from eight directions. A passage the mesh has closed shows up as
        // opposing pairs that either fail outright or detour absurdly compared to the
        // straight line - and the detour ratio is what separates "blocked" from "just a wall".
        Console.WriteLine();
        Console.WriteLine($"  {"crossing",-10} {"result",-12} {"waypoints",-10} {"path",-9} {"detour",-8}");
        int blocked = 0, crossed = 0;
        for (int i = 0; i < 4; ++i)
        {
            var angle = i * MathF.PI / 4;
            var offset = new Vector3(MathF.Cos(angle) * radius, 0, MathF.Sin(angle) * radius);
            var a = Snap(pf, point + offset);
            var b = Snap(pf, point - offset);
            if (a == null || b == null)
            {
                Console.WriteLine($"  {Bearing(i),-10} {"offMesh",-12} {"-",-10} {"-",-9} {"-",-8}");
                continue;
            }

            var route = pf.FindWalkPath(a.Value, b.Value);
            var straight = Vector3.Distance(a.Value, b.Value);
            if (route is not { Partial: false })
            {
                ++blocked;
                Console.WriteLine($"  {Bearing(i),-10} {"noRoute",-12} {"-",-10} {"-",-9} {"-",-8}");
                continue;
            }
            var length = Length(route.Waypoints);
            var detour = straight > 0.1f ? length / straight : 1;
            if (detour > 3)
                ++blocked;
            else
                ++crossed;
            Console.WriteLine($"  {Bearing(i),-10} {"ok",-12} {route.Waypoints.Count,-10} {length,-8:f0}m {detour,-7:f1}x");
        }

        // What is actually there. When the mesh and the world disagree, the layout object is
        // usually the answer - a shared group whose door state seals the passage.
        if (ZoneNames.Lookup(entry.Key)?.Bg is { Length: > 0 } bg && GamePaths.FindSqpackDir() is { } sqpack)
        {
            var objects = Mnemosyne.Builder.LayoutQuery.Near(sqpack, bg, point, MathF.Max(radius, 6));
            Console.WriteLine();
            Console.WriteLine($"layout objects within {MathF.Max(radius, 6):f0}m: {objects.Count}");
            foreach (var o in objects.Take(12))
                Console.WriteLine($"    {o.Distance,5:f1}m  {o.Type,-16} {o.DoorState ?? "",-7} {Shorten(o.Asset)}  [{o.Layer}]");
            if (objects.Count > 12)
                Console.WriteLine($"    ... and {objects.Count - 12} more");
        }

        Console.WriteLine();
        Console.WriteLine(blocked == 0
            ? $"open: all {crossed} crossings pass straight through"
            : $"{blocked} of 4 crossings blocked or detouring >3x - the mesh closes this passage");
        return 0;
    }


    /// <summary>Load a zone the way a consumer receives it: with the zone's overrides applied.
    /// Without this the diagnostics report on the raw cache and disagree with the served mesh,
    /// which makes a carve look like it did nothing.</summary>

    /// <summary>Find a zone the way the service does: vnavmesh's cache first, then Mnemosyne's
    /// own built store. Without the fallback the diagnostics are blind to every zone only we
    /// have built — Gridania's cached files are all stale-version, so `solids` reported "no
    /// zone matches" for a city that was sitting in the built store the whole time.</summary>
    internal static (string Path, string Key)? ResolveZoneOrReport(string hint)
    {
        var found = ResolveZone(hint);
        if (found == null)
            Console.WriteLine($"no current-version zone matches '{hint}' (checked vnavmesh's cache and Mnemosyne's built store)");
        return found;
    }

    internal static (string Path, string Key)? ResolveZone(string hint)
    {
        var cached = MeshCache.Enumerate()
            .Where(e => e.IsSupported && e.Key.Contains(hint, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => new FileInfo(e.Path).Length)
            .FirstOrDefault();
        if (cached != null)
            return (cached.Path, cached.Key);

        var builtDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mnemosyne", "built");
        if (!Directory.Exists(builtDir))
            return null;
        var built = new DirectoryInfo(builtDir).GetFiles("*.navmesh")
            .Where(f => Path.GetFileNameWithoutExtension(f.Name).Contains(hint, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => f.Length)
            .FirstOrDefault();
        return built == null ? null : (built.FullName, Path.GetFileNameWithoutExtension(built.Name));
    }

    internal static global::Navmesh.Navmesh LoadServed(string path, string cacheKey)
    {
        var navmesh = MeshCache.Load(path);
        var overrides = OverrideStore.Load(cacheKey);
        if (!overrides.IsEmpty)
        {
            var applied = OverrideStore.Apply(navmesh.Mesh, overrides);
            Console.WriteLine($"applied {applied} override edits ({overrides.FlagEdits.Count} shapes, "
                + $"{overrides.Links.Count} links, {overrides.Obstacles.Count} obstacles)");
        }
        return navmesh;
    }

    private static string Shorten(string? asset) =>
        asset is not { Length: > 0 } ? "" : Path.GetFileNameWithoutExtension(asset);

    private static string Bearing(int i) => i switch { 0 => "E-W", 1 => "NE-SW", 2 => "N-S", _ => "NW-SE" };

    private static Vector3? Snap(MeshPathfinder pf, Vector3 p) => pf.TryNearestGround(p, 20, out var g) ? g : null;

    private static float Length(List<Vector3> path)
    {
        float length = 0;
        for (int i = 1; i < path.Count; ++i)
            length += Vector3.Distance(path[i - 1], path[i]);
        return length;
    }
}
