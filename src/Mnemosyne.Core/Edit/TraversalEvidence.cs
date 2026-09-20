using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mnemosyne.Core;

// What execution learned that the mesh does not know, accumulated per zone.
//
// Two kinds of report, from the protocol doc's reportTraversal:
//   mode "direct" + success  -> the character got there where the mesh said no-path. That is
//                               an off-mesh-link candidate: the world is more connected than
//                               the mesh believes.
//   success: false           -> a planned leg failed. That is a block/cost candidate: the
//                               mesh is more optimistic than the world.
//
// Nothing here is applied automatically. It is evidence, surfaced in the viewer's edit mode,
// because a bad auto-applied link is worse than no link — it silently reroutes everything.
public sealed class TraversalReport
{
    public float[] From { get; set; } = [0, 0, 0];
    public float[] To { get; set; } = [0, 0, 0];
    public string Mode { get; set; } = "walk"; // walk | fly | direct
    public bool Success { get; set; }
    public string? Note { get; set; }
    public int Count { get; set; } = 1;      // times this same traversal was reported
    public string? FirstSeenUtc { get; set; }
    public string? LastSeenUtc { get; set; }
}

public sealed class ZoneEvidence
{
    public int Version { get; set; } = 1;
    /// <summary>Traversals that worked where the mesh said no — off-mesh-link candidates.</summary>
    public List<TraversalReport> LinkCandidates { get; set; } = [];
    /// <summary>Planned legs that failed — block or cost-paint candidates.</summary>
    public List<TraversalReport> BlockCandidates { get; set; } = [];

    public bool IsEmpty => LinkCandidates.Count == 0 && BlockCandidates.Count == 0;
    public int Count => LinkCandidates.Count + BlockCandidates.Count;
}

public static class EvidenceStore
{
    /// <summary>Reports within this distance of an existing one are the same traversal.
    /// Followers re-report the same doorway from slightly different spots every attempt;
    /// without merging, one bad doorway becomes a hundred indistinguishable rows.</summary>
    private const float MergeDistance = 5f;

    public static string Directory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mnemosyne", "evidence");

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly object Lock = new();

    // keyed by bg prefix like the OverrideStore, so evidence survives a zone's cache-key churn
    private static string PathFor(string cacheKey) =>
        Path.Combine(Directory, OverrideStore.BgKey(cacheKey) + ".json");

    public static ZoneEvidence Load(string cacheKey)
    {
        lock (Lock)
            return LoadUnlocked(cacheKey);
    }

    private static ZoneEvidence LoadUnlocked(string cacheKey)
    {
        var path = PathFor(cacheKey);
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<ZoneEvidence>(File.ReadAllText(path)) ?? new ZoneEvidence()
                : new ZoneEvidence();
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            return new ZoneEvidence();
        }
    }

    /// <summary>Record one report, merging it into a nearby identical one. Returns the total
    /// number of times this traversal has now been seen.</summary>
    public static int Record(string cacheKey, TraversalReport report)
    {
        lock (Lock)
        {
            var evidence = LoadUnlocked(cacheKey);
            var bucket = report is { Mode: "direct", Success: true } ? evidence.LinkCandidates
                : !report.Success ? evidence.BlockCandidates
                : null;
            if (bucket == null)
                return 0; // a successful planned leg teaches nothing; drop it

            var now = DateTime.UtcNow.ToString("O");
            var existing = bucket.FirstOrDefault(r => r.Mode == report.Mode
                && Near(r.From, report.From) && Near(r.To, report.To));
            if (existing != null)
            {
                ++existing.Count;
                existing.LastSeenUtc = now;
                existing.Note ??= report.Note;
            }
            else
            {
                report.FirstSeenUtc = now;
                report.LastSeenUtc = now;
                bucket.Add(report);
                existing = report;
            }

            Save(cacheKey, evidence);
            return existing.Count;
        }
    }

    private static bool Near(float[] a, float[] b)
    {
        if (a.Length < 3 || b.Length < 3)
            return false;
        var dx = a[0] - b[0];
        var dy = a[1] - b[1];
        var dz = a[2] - b[2];
        return dx * dx + dy * dy + dz * dz <= MergeDistance * MergeDistance;
    }

    private static void Save(string cacheKey, ZoneEvidence evidence)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var path = PathFor(cacheKey);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(evidence, Json));
        File.Move(temp, path, true);
    }
}
