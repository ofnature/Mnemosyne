using DotRecast.Detour;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mnemosyne.Core;

// Mesh edits as world-space shapes (never poly refs - those churn every rebuild), stored
// per zone in %APPDATA%\Mnemosyne\overrides\<bg-key>.json and applied to the DtNavMesh
// right after load. One override file covers all cache-key variants of a zone.
public sealed class OverrideShape
{
    public string Kind { get; set; } = "sphere"; // sphere | box
    public float[] Center { get; set; } = [0, 0, 0];
    public float[] Extent { get; set; } = [3, 3, 3]; // box half-extents; sphere reads [0] as radius
    public bool Block { get; set; } = true;          // false = force walkable
    public string? Note { get; set; }
    public string? AssetPath { get; set; }           // door-derived edits: source object
    public uint InstanceId { get; set; }
}

public sealed class OverrideLink
{
    public float[] From { get; set; } = [0, 0, 0];
    public float[] To { get; set; } = [0, 0, 0];
    public bool Bidirectional { get; set; } = true;
    public string? Note { get; set; }
}

/// <summary>An obstacle the navmesh never carved: a lamppost, a bollard, a low wall whose
/// collision Recast stepped over. Recorded as a shape the *path* avoids rather than mesh to
/// blank, because block overrides work at whole-poly granularity — a 0.66 m post cannot be
/// cut out of a plaza polygon metres across without blanking the plaza with it.</summary>
public sealed class ObstacleShape
{
    public float[] Center { get; set; } = [0, 0, 0];
    public float Radius { get; set; } = 0.5f;
    public float Height { get; set; } = 2f;
    public string? Note { get; set; }
}

public sealed class ZoneOverrides
{
    public int Version { get; set; } = 1;
    public List<OverrideShape> FlagEdits { get; set; } = [];
    public List<OverrideLink> Links { get; set; } = [];
    public float[]? PruneSeed { get; set; } // declarative: flood-fill from here, block the unreached
    /// <summary>Solid objects the mesh runs through; routes are padded around these.</summary>
    public List<ObstacleShape> Obstacles { get; set; } = [];

    [JsonIgnore]
    public bool IsEmpty => FlagEdits.Count == 0 && Links.Count == 0 && PruneSeed == null && Obstacles.Count == 0;
}

public static class OverrideStore
{
    public const int WalkableFlags = 1; // vnavmesh's default walkable poly flags
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string Directory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mnemosyne", "overrides");

    // strip the hash/festival suffixes: overrides apply to every variant of a zone
    public static string BgKey(string cacheKey)
    {
        var sep = cacheKey.IndexOf("__", StringComparison.Ordinal);
        return sep > 0 ? cacheKey[..sep] : cacheKey;
    }

    private static string PathFor(string cacheKey) => Path.Combine(Directory, BgKey(cacheKey) + ".json");

    public static ZoneOverrides Load(string cacheKey)
    {
        try
        {
            var path = PathFor(cacheKey);
            if (File.Exists(path))
                return JsonSerializer.Deserialize<ZoneOverrides>(File.ReadAllText(path), JsonOptions) ?? new();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"overrides: failed to load for '{cacheKey}': {ex.Message}");
        }
        return new();
    }

    public static void Save(string cacheKey, ZoneOverrides overrides)
    {
        var path = PathFor(cacheKey);
        if (overrides.IsEmpty)
        {
            File.Delete(path);
            return;
        }
        System.IO.Directory.CreateDirectory(Directory);
        File.WriteAllText(path, JsonSerializer.Serialize(overrides, JsonOptions));
    }

    // apply flag edits + prune to a freshly loaded mesh; returns affected poly count
    public static int Apply(DtNavMesh mesh, ZoneOverrides overrides)
    {
        int affected = 0;
        foreach (var shape in overrides.FlagEdits)
            affected += ApplyShape(mesh, shape);
        if (overrides.PruneSeed is { Length: 3 } seed)
            affected += Prune(mesh, new Vector3(seed[0], seed[1], seed[2]));
        return affected;
    }

    private static int ApplyShape(DtNavMesh mesh, OverrideShape shape)
    {
        var center = new Vector3(shape.Center[0], shape.Center[1], shape.Center[2]);
        var extent = new Vector3(shape.Extent[0], shape.Extent.Length > 1 ? shape.Extent[1] : shape.Extent[0], shape.Extent.Length > 2 ? shape.Extent[2] : shape.Extent[0]);
        bool sphere = shape.Kind == "sphere";
        float radiusSq = extent.X * extent.X;
        int flags = shape.Block ? 0 : WalkableFlags;

        int affected = 0;
        for (int t = 0; t < mesh.GetMaxTiles(); ++t)
        {
            var tile = mesh.GetTile(t);
            var data = tile?.data;
            if (data?.header == null)
                continue;
            long refBase = mesh.GetPolyRefBase(tile);
            for (int p = 0; p < data.header.polyCount; ++p)
            {
                var poly = data.polys[p];
                if (poly.GetPolyType() != 0)
                    continue;
                var c = PolyCenter(data, poly);
                bool inside = sphere
                    ? Vector3.DistanceSquared(c, center) <= radiusSq
                    : MathF.Abs(c.X - center.X) <= extent.X && MathF.Abs(c.Y - center.Y) <= extent.Y && MathF.Abs(c.Z - center.Z) <= extent.Z;
                if (!inside)
                    continue;
                mesh.SetPolyFlags(refBase | (uint)p, flags);
                ++affected;
            }
        }
        return affected;
    }

    // block everything not reachable from the seed point (MQ2Nav-style prune)
    private static int Prune(DtNavMesh mesh, Vector3 seed)
    {
        var query = new DtNavMeshQuery(mesh);
        var filter = new DtQueryDefaultFilter();
        query.FindNearestPoly(new DotRecast.Core.Numerics.RcVec3f(seed.X, seed.Y, seed.Z), new(5, 5, 5), filter, out var seedRef, out _, out _);
        if (seedRef == 0)
            return 0;

        // Dijkstra flood over the connected component
        var reachable = new List<long>();
        var parents = new List<long>();
        var costs = new List<float>();
        query.FindPolysAroundCircle(seedRef, new(seed.X, seed.Y, seed.Z), 1e9f, filter, ref reachable, ref parents, ref costs);
        var reached = new HashSet<long>(reachable) { seedRef };

        int affected = 0;
        for (int t = 0; t < mesh.GetMaxTiles(); ++t)
        {
            var tile = mesh.GetTile(t);
            var data = tile?.data;
            if (data?.header == null)
                continue;
            long refBase = mesh.GetPolyRefBase(tile);
            for (int p = 0; p < data.header.polyCount; ++p)
            {
                long polyRef = refBase | (uint)p;
                if (reached.Contains(polyRef))
                    continue;
                mesh.GetPolyFlags(polyRef, out var flags);
                if (flags == 0)
                    continue; // already blocked
                mesh.SetPolyFlags(polyRef, 0);
                ++affected;
            }
        }
        return affected;
    }

    private static Vector3 PolyCenter(DtMeshData data, DtPoly poly)
    {
        var sum = Vector3.Zero;
        for (int i = 0; i < poly.vertCount; ++i)
        {
            int vi = poly.verts[i] * 3;
            sum += new Vector3(data.verts[vi], data.verts[vi + 1], data.verts[vi + 2]);
        }
        return sum / poly.vertCount;
    }
}
