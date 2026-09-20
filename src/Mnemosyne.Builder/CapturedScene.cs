using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Layer;
using Mnemosyne.Protocol;
using Navmesh;
using System.Linq;
using System.Numerics;

namespace Mnemosyne.Builder;

// Turns Ariadne's live scene capture into the builder's SceneDefinition.
//
// Why this exists: LgbSceneReader reconstructs a zone from LGB files on disk, which means
// guessing at festival layers and shared-group states. The game process knows. A capture is
// the ground truth for the variant the player is actually standing in, so a mesh built from
// one matches what they can walk on — the gap offline building can only approximate.
//
// Collision geometry is not shipped: meshPaths/terrains are sqpack paths the builder reads
// itself, so a capture stays in the low megabytes instead of the hundreds.
public static class CapturedScene
{
    public static SceneDefinition ToSceneDefinition(SceneCaptureDto dto)
    {
        var scene = new SceneDefinition
        {
            TerritoryID = dto.TerritoryId,
            CFCID = dto.CfcId,
        };
        foreach (var layer in dto.FestivalLayers)
            scene.FestivalLayers.Add(layer);
        scene.ZoneSGs.AddRange(dto.ZoneSGs);
        scene.Terrains.AddRange(dto.Terrains);

        foreach (var shape in dto.AnalyticShapes)
            scene.AnalyticShapes[shape.Crc] = (Tr(shape.Transform), Vec(shape.BbMin), Vec(shape.BbMax));
        foreach (var mesh in dto.MeshPaths)
            scene.MeshPaths[mesh.Crc] = mesh.Path;
        foreach (var part in dto.BgParts)
            scene.BgParts.Add((part.Key, Tr(part.Transform), part.Crc, part.MatId, part.MatMask, part.Analytic));
        foreach (var collider in dto.Colliders)
            scene.Colliders.Add((collider.Key, Tr(collider.Transform), collider.Crc, collider.MatId,
                collider.MatMask, (ColliderType)collider.Type));
        foreach (var exit in dto.ExitRanges)
            scene.ExitRanges.Add((exit.Key, Tr(exit.Transform)));

        return scene;
    }

    /// <summary>Rebuild a scene through the full capture path and report what changed.
    /// Comparing DTOs only proves JSON is faithful; this proves the conversion is.</summary>
    public static List<string> RoundTripDiff(SceneDefinition scene, string cacheKey)
    {
        var back = ToSceneDefinition(ToDto(scene, cacheKey));
        var diffs = new List<string>();

        void Check(string what, object a, object b)
        {
            if (!Equals(a, b))
                diffs.Add($"{what}: {a} -> {b}");
        }

        Check("territoryId", scene.TerritoryID, back.TerritoryID);
        Check("cfcId", scene.CFCID, back.CFCID);
        Check("festivalLayers", scene.FestivalLayers.Count, back.FestivalLayers.Count);
        Check("zoneSGs", scene.ZoneSGs.Count, back.ZoneSGs.Count);
        Check("terrains", scene.Terrains.Count, back.Terrains.Count);
        Check("analyticShapes", scene.AnalyticShapes.Count, back.AnalyticShapes.Count);
        Check("meshPaths", scene.MeshPaths.Count, back.MeshPaths.Count);
        Check("bgParts", scene.BgParts.Count, back.BgParts.Count);
        Check("colliders", scene.Colliders.Count, back.Colliders.Count);
        Check("exitRanges", scene.ExitRanges.Count, back.ExitRanges.Count);

        for (int i = 0; i < Math.Min(scene.Terrains.Count, back.Terrains.Count); ++i)
            if (scene.Terrains[i] != back.Terrains[i])
                diffs.Add($"terrain[{i}]: '{scene.Terrains[i]}' -> '{back.Terrains[i]}'");
        for (int i = 0; i < Math.Min(scene.ZoneSGs.Count, back.ZoneSGs.Count); ++i)
            if (scene.ZoneSGs[i] != back.ZoneSGs[i])
                diffs.Add($"zoneSG[{i}]: {scene.ZoneSGs[i]} -> {back.ZoneSGs[i]}");
        if (!scene.FestivalLayers.SetEquals(back.FestivalLayers))
            diffs.Add("festivalLayers: contents differ");
        for (int i = 0; i < Math.Min(scene.ExitRanges.Count, back.ExitRanges.Count); ++i)
        {
            var (a, b) = (scene.ExitRanges[i], back.ExitRanges[i]);
            if (a.key != b.key || !Same(a.transform, b.transform))
                diffs.Add($"exitRange[{i}]: {Show(a.transform)} -> {Show(b.transform)}");
        }
        for (int i = 0; i < Math.Min(scene.BgParts.Count, back.BgParts.Count); ++i)
        {
            var (a, b) = (scene.BgParts[i], back.BgParts[i]);
            if (a.key != b.key || a.crc != b.crc || a.matId != b.matId || a.matMask != b.matMask || a.analytic != b.analytic)
                diffs.Add($"bgPart[{i}] identity: key/crc/mat/analytic differ");
            else if (!Same(a.transform, b.transform))
                diffs.Add($"bgPart[{i}] transform: {Show(a.transform)} -> {Show(b.transform)}");
        }
        for (int i = 0; i < Math.Min(scene.Colliders.Count, back.Colliders.Count); ++i)
        {
            var (a, b) = (scene.Colliders[i], back.Colliders[i]);
            if (a.key != b.key || a.crc != b.crc || a.matId != b.matId || a.matMask != b.matMask || a.type != b.type)
                diffs.Add($"collider[{i}] identity: key/crc/mat/type differ");
            else if (!Same(a.transform, b.transform))
                diffs.Add($"collider[{i}] transform: {Show(a.transform)} -> {Show(b.transform)}");
        }
        foreach (var (crc, shape) in scene.AnalyticShapes)
        {
            if (!back.AnalyticShapes.TryGetValue(crc, out var other))
                diffs.Add($"analyticShape {crc}: missing after round-trip");
            else if (!Same(shape.transform, other.transform) || shape.bbMin != other.bbMin || shape.bbMax != other.bbMax)
                diffs.Add($"analyticShape {crc}: {Show(shape.transform)} -> {Show(other.transform)}");
        }
        foreach (var (crc, path) in scene.MeshPaths)
            if (!back.MeshPaths.TryGetValue(crc, out var other) || other != path)
                diffs.Add($"meshPath {crc}: '{path}' -> '{(back.MeshPaths.GetValueOrDefault(crc) ?? "<missing>")}'");

        return diffs;
    }

    private static bool Same(Transform a, Transform b) =>
        a.Translation.Equals(b.Translation) && a.Rotation.Equals(b.Rotation)
        && a.Scale.Equals(b.Scale) && a.Type == b.Type;

    private static string Show(Transform t) => $"t{t.Translation} r{t.Rotation} s{t.Scale} type{t.Type}";

    /// <summary>Read a zone's scene from game files and hand it back in wire form. Lets a
    /// host without the FFXIVClientStructs types (the CLI) exercise the capture path.</summary>
    public static SceneCaptureDto CaptureOffline(Lumina.GameData game, string bgPath, string cacheKey)
        => ToDto(LgbSceneReader.Read(game, bgPath, 0), cacheKey);

    /// <summary>The reverse mapping. Not used to serve requests — the game process builds
    /// captures — but it lets an offline scene be pushed through the exact wire path the
    /// buildZone op takes, which is the only way to test that path without the game.</summary>
    public static SceneCaptureDto ToDto(SceneDefinition scene, string cacheKey) => new()
    {
        CacheKey = cacheKey,
        TerritoryId = scene.TerritoryID,
        CfcId = scene.CFCID,
        FestivalLayers = [.. scene.FestivalLayers],
        ZoneSGs = [.. scene.ZoneSGs],
        Terrains = [.. scene.Terrains],
        AnalyticShapes = [.. scene.AnalyticShapes.Select(kv => new AnalyticShapeDto
        {
            Crc = kv.Key,
            Transform = TrDto(kv.Value.transform),
            BbMin = [kv.Value.bbMin.X, kv.Value.bbMin.Y, kv.Value.bbMin.Z],
            BbMax = [kv.Value.bbMax.X, kv.Value.bbMax.Y, kv.Value.bbMax.Z],
        })],
        MeshPaths = [.. scene.MeshPaths.Select(kv => new MeshPathDto { Crc = kv.Key, Path = kv.Value })],
        BgParts = [.. scene.BgParts.Select(p => new BgPartDto
        {
            Key = p.key, Transform = TrDto(p.transform), Crc = p.crc,
            MatId = p.matId, MatMask = p.matMask, Analytic = p.analytic,
        })],
        Colliders = [.. scene.Colliders.Select(c => new ColliderDto
        {
            Key = c.key, Transform = TrDto(c.transform), Crc = c.crc,
            MatId = c.matId, MatMask = c.matMask, Type = (int)c.type,
        })],
        ExitRanges = [.. scene.ExitRanges.Select(e => new ExitRangeDto { Key = e.key, Transform = TrDto(e.transform) })],
    };

    private static TransformDto TrDto(Transform t) => new()
    {
        T = [t.Translation.X, t.Translation.Y, t.Translation.Z],
        R = [t.Rotation.X, t.Rotation.Y, t.Rotation.Z, t.Rotation.W],
        S = [t.Scale.X, t.Scale.Y, t.Scale.Z],
        Type = t.Type,
    };

    private static Vector3 Vec(float[]? v) => v is { Length: >= 3 } ? new Vector3(v[0], v[1], v[2]) : Vector3.Zero;

    private static Transform Tr(TransformDto? t) => new()
    {
        Translation = Vec(t?.T),
        // identity rather than a zero quaternion when a capture omits rotation: a zero
        // quaternion silently collapses every instance it touches
        Rotation = t?.R is { Length: >= 4 } r ? new Quaternion(r[0], r[1], r[2], r[3]) : Quaternion.Identity,
        Scale = t?.S is { Length: >= 3 } ? Vec(t.S) : Vector3.One,
        Type = t?.Type ?? 0,
    };
}
