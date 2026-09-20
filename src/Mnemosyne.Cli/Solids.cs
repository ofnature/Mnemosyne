using Lumina;
using Mnemosyne.Builder;
using Mnemosyne.Core;
using System.Numerics;

namespace Mnemosyne.Cli;

// Report (and optionally carve) obstacles the navmesh runs straight through.
//
// usage: Mnemosyne.Cli solids <zone-substring> [--record] [--near x,y,z]
//   --record  write block overrides for what it finds. Overrides bake into served meshes, so
//             consumers get the carved mesh without a rebuild.
//   --near    only report clusters within 15m of a point, for chasing one reported snag.
public static class Solids
{
    public static int Run(string[] args)
    {
        var record = args.Contains("--record");
        Vector3? near = null;
        var nearArg = args.SkipWhile(a => a != "--near").Skip(1).FirstOrDefault();
        if (nearArg?.Split(',') is { Length: 3 } parts
            && float.TryParse(parts[0], out var nx) && float.TryParse(parts[1], out var ny) && float.TryParse(parts[2], out var nz))
            near = new Vector3(nx, ny, nz);

        var sqpack = GamePaths.FindSqpackDir();
        if (sqpack == null)
        {
            Console.WriteLine("no game install found");
            return 1;
        }

        var hint = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : "";
        if (Probe.ResolveZoneOrReport(hint) is not { } entry)
            return 1;
        var info = ZoneNames.Lookup(entry.Key);
        if (info?.Bg is not { Length: > 0 } bg)
        {
            Console.WriteLine($"no bg path known for '{entry.Key}'");
            return 1;
        }
        Console.WriteLine($"zone: {info.Name} ({entry.Key})");
        Console.WriteLine("comparing collision against the navmesh...");

        var navmesh = MeshCache.Load(entry.Path);
        // A dense town turns up ~2000 of these, most of them knee-high props. Carving every
        // one is both slow and risky, so default to obstacles tall enough that a character
        // is stopped rather than nudged; the caller can lower it deliberately.
        var minHeight = 1.5f;
        var heightArg = args.SkipWhile(a => a != "--min-height").Skip(1).FirstOrDefault();
        if (heightArg != null && float.TryParse(heightArg, out var mh))
            minHeight = mh;

        var clusters = SolidAudit.Find(new GameData(sqpack), bg, navmesh.Mesh);
        var beforeFilter = clusters.Count;
        clusters = [.. clusters.Where(c => c.Height >= minHeight)];
        if (near is { } focus)
            clusters = [.. clusters.Where(c => Vector3.Distance(c.Pos, focus) <= 15f)];
        Console.WriteLine($"{beforeFilter} obstacles found, {clusters.Count} at least {minHeight:f1}m tall"
            + (near != null ? " and within 15m" : ""));

        Console.WriteLine();
        if (clusters.Count == 0)
        {
            Console.WriteLine("no walkable mesh found inside solid geometry");
            return 0;
        }
        Console.WriteLine("obstacles with walkable mesh running through them:");
        Console.WriteLine();
        Console.WriteLine($"    {"position",-28} {"radius",-8} {"height",-8} {"faces",-7} asset");
        foreach (var c in clusters.Take(near != null ? 40 : 25))
            Console.WriteLine($"    ({c.Pos.X,7:f1}, {c.Pos.Y,6:f1}, {c.Pos.Z,7:f1})  {c.Radius,6:f2}m  {c.Height,6:f2}m  {c.Faces,5}   {Shorten(c.Asset)}");
        if (clusters.Count > 25 && near == null)
            Console.WriteLine($"    ... and {clusters.Count - 25} more");

        if (!record)
        {
            Console.WriteLine();
            Console.WriteLine("re-run with --record to carve these as block overrides (they bake into served meshes)");
            return 0;
        }

        // Recorded as obstacles, not as block shapes. Block overrides work at whole-poly
        // granularity - measured: a 0.66 m sphere on the Limsa lamppost affected 0 polys,
        // because plaza polygons are metres across and the shape test uses the poly centre.
        // Blanking the containing polygon would take a chunk of plaza with it. An obstacle
        // instead tells the padding pass to route around the post, at any mesh granularity.
        var overrides = OverrideStore.Load(entry.Key);
        var added = 0;
        foreach (var c in clusters)
        {
            // re-runnable after a patch without stacking duplicates on the same lamppost
            if (overrides.Obstacles.Any(o =>
                Vector3.Distance(new Vector3(o.Center[0], o.Center[1], o.Center[2]), c.Pos) < MathF.Max(c.Radius, 0.5f)))
                continue;
            overrides.Obstacles.Add(new ObstacleShape
            {
                Center = [c.Pos.X, c.Pos.Y, c.Pos.Z],
                Radius = c.Radius,
                Height = c.Height,
                Note = $"solid audit: {Shorten(c.Asset)} ({c.Faces} faces)",
            });
            ++added;
        }
        OverrideStore.Save(entry.Key, overrides);
        Console.WriteLine();
        Console.WriteLine($"recorded {added} obstacles ({overrides.Obstacles.Count} total for this zone)");
        Console.WriteLine("routes are padded around these from the next findPath; no rebuild needed");
        return 0;
    }

    private static string Shorten(string asset) =>
        asset.StartsWith('<') ? asset : Path.GetFileNameWithoutExtension(asset);
}
