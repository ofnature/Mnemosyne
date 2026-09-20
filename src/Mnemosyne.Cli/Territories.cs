using Lumina;
using Mnemosyne.Core;

namespace Mnemosyne.Cli;

// List territories straight from the game's own sheet, not from Mnemosyne's bundled snapshot.
//
// zone_names.json is generated once and goes stale every patch, which matters more than it
// sounds: the builder resolves a cacheKey to a bg path through that file, so a zone added
// after the snapshot cannot be built at all - it just reports "not buildable" and looks like
// a different failure entirely.
//
// usage: Mnemosyne.Cli territories <name-or-bg-substring>
public static class Territories
{
    public static int Run(string[] args)
    {
        var filter = args.Length > 1 && !args[1].StartsWith("--") ? args[1] : "";
        var uncachedOnly = args.Contains("--uncached");
        var flyableOnly = args.Contains("--flyable");

        // Every zone key vnavmesh (or we) already have a mesh for, by bg prefix. A zone with
        // any cached variant is not a clean test of the build-on-entry path.
        var haveMesh = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in new[]
                 {
                     MeshCache.DefaultDirectory,
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mnemosyne", "built"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mnemosyne", "captured"),
                 })
        {
            if (!Directory.Exists(dir))
                continue;
            foreach (var f in Directory.GetFiles(dir, "*.navmesh"))
                haveMesh.Add(Path.GetFileNameWithoutExtension(f).Split("__")[0]);
        }
        var sqpack = GamePaths.FindSqpackDir();
        if (sqpack == null)
        {
            Console.WriteLine("no game install found");
            return 1;
        }

        var game = new GameData(sqpack);
        var sheet = game.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>();
        if (sheet == null)
        {
            Console.WriteLine("TerritoryType sheet unavailable");
            return 1;
        }

        var snapshot = ZoneNames.All;
        var rows = new List<(uint Id, string Bg, string Place, bool Known)>();
        foreach (var row in sheet)
        {
            var bg = row.Bg.ToString();
            if (bg.Length == 0)
                continue;
            var place = row.PlaceName.ValueNullable?.Name.ToString() ?? "";
            if (filter.Length > 0
                && !bg.Contains(filter, StringComparison.OrdinalIgnoreCase)
                && !place.Contains(filter, StringComparison.OrdinalIgnoreCase))
                continue;
            var bgKey = bg.Replace('/', '_');
            if (uncachedOnly && haveMesh.Contains(bgKey))
                continue;
            // same rule the builder uses to decide a zone gets a flying volume
            if (flyableOnly && row.TerritoryIntendedUse.RowId is not (1 or 49 or 47))
                continue;
            rows.Add((row.RowId, bg, place, snapshot.ContainsKey(bgKey)));
        }

        Console.WriteLine($"{rows.Count} territories matching '{filter}'"
            + (uncachedOnly ? ", with no mesh anywhere" : "") + (flyableOnly ? ", flyable" : ""));
        Console.WriteLine();
        Console.WriteLine($"  {"id",-6} {"place",-34} {"in snapshot",-12} bg");
        foreach (var (id, bg, place, known) in rows.OrderBy(r => r.Id))
            Console.WriteLine($"  {id,-6} {Trim(place, 34),-34} {(known ? "yes" : "NO"),-12} {bg}");

        var missing = rows.Count(r => !r.Known);
        if (missing > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"{missing} of these are missing from zone_names.json - Mnemosyne cannot resolve a bg");
            Console.WriteLine("path for them, so it cannot build them offline. Regenerate the snapshot to fix.");
        }
        return 0;
    }

    private static string Trim(string s, int width) => s.Length <= width ? s : s[..(width - 1)] + "…";
}
