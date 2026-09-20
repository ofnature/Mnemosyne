using System.Security.Cryptography;

namespace Mnemosyne.Cli;

// Has vnavmesh changed its per-territory customizations since we vendored them?
//
// This outlives the seeding path it was written alongside. Seeds care about the version number
// matching; the mesh cares about the *content*. A customization is where vnavmesh records that
// a zone needs different partitioning, or a hand-authored link across a gap the rasterizer
// cannot see. Miss one and our mesh is not slightly different, it is disconnected somewhere -
// North Horn without its four island links, and nothing in any log to say so.
//
// So: compare what we vendored against the upstream clone, by content. Run it after updating
// the clone, the same way milestone 1's key-parity check runs after a vnavmesh release.
//
// usage: Mnemosyne.Cli customizations
public static class CustomizationDrift
{
    public static int Run()
    {
        var mine = Path.GetFullPath(Path.Combine("src", "Mnemosyne.Builder", "Customizations"));
        var theirs = Path.GetFullPath(Path.Combine("external", "ffxiv_navmesh", "vnavmesh", "Customizations"));

        if (!Directory.Exists(mine) || !Directory.Exists(theirs))
        {
            Console.WriteLine($"need both directories (run from the repo root):\n  {mine}\n  {theirs}");
            return 1;
        }

        var ours = Directory.GetFiles(mine, "*.cs").ToDictionary(f => Path.GetFileName(f)!, Hash);
        var upstream = Directory.GetFiles(theirs, "*.cs").ToDictionary(f => Path.GetFileName(f)!, Hash);

        var added = upstream.Keys.Except(ours.Keys).Order().ToList();
        var removed = ours.Keys.Except(upstream.Keys).Order().ToList();
        var changed = ours.Keys.Intersect(upstream.Keys).Where(k => ours[k] != upstream[k]).Order().ToList();

        Console.WriteLine($"vendored {ours.Count} customizations, upstream has {upstream.Count}");
        Console.WriteLine();

        foreach (var f in added)
            Console.WriteLine($"  MISSING   {f}  — upstream has a customization we never vendored");
        foreach (var f in changed)
            Console.WriteLine($"  CHANGED   {f}  — upstream edited it since we copied");
        foreach (var f in removed)
            Console.WriteLine($"  EXTRA     {f}  — we have one upstream dropped");

        if (added.Count + changed.Count + removed.Count == 0)
        {
            Console.WriteLine("  in sync");
            return 0;
        }

        Console.WriteLine();
        Console.WriteLine($"{added.Count + changed.Count + removed.Count} file(s) drifted. Re-copy them:");
        Console.WriteLine($"  cp {theirs}\\*.cs {mine}\\");
        Console.WriteLine("Then rebuild affected zones — a customization change does not invalidate");
        Console.WriteLine("anything on its own, so stale meshes keep serving until something rebuilds them.");
        return 1;
    }

    private static string Hash(string path)
    {
        // normalise line endings so a checkout difference is not reported as drift
        var text = File.ReadAllText(path).Replace("\r\n", "\n");
        return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
    }
}
