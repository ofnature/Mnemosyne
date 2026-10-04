using Lumina;
using Navmesh;
using System.Numerics;

namespace Mnemosyne.Builder;

// "Is there anything solid between these two points?" asked of the zone's real collision.
//
// Seam links are proposed from the mesh alone: two islands at the same height, a small gap apart.
// That describes a crack the rasterizer left in continuous ground - and it describes a wall between
// two rooms exactly as well. The first one tried in game, at Camp Dragonhead, was masonry: the
// follower walked the link to its near end and pushed into stone (2026-09-29). The mesh had been
// right to keep the two pieces apart.
//
// So a candidate is checked against the geometry the mesh was built from. Three horizontal rays at
// body height (the floor itself is ignored - a kerb you step over is not a wall) run between the
// two ends; any collision triangle on any of them means something a body walks into.
//
// Everything that is rasterized as solid for walking counts, which includes FlyThrough triangles:
// the rasterizer only lets those through when it is building a flight volume.
public sealed class CollisionProbe
{
    /// <summary>Heights above the higher end at which the gap is crossed. 0.5 clears the step
    /// height (0.5 y climb), 1.8 is about the top of a body.</summary>
    public static readonly float[] BodyHeights = [0.6f, 1.2f, 1.8f];

    private readonly List<(Vector3 A, Vector3 B, Vector3 C, string Asset)> _tris = [];

    /// <summary>Load the collision near a set of points - only instances whose bounds come
    /// within <paramref name="margin"/> of one are expanded, so a zone-sized scene costs the few
    /// objects that matter.</summary>
    public CollisionProbe(GameData game, string bgPath, IReadOnlyList<Vector3> near, float margin = 4f)
    {
        SceneExtractor.FileReader = path =>
        {
            try
            {
                return game.GetFile<Lumina.Data.FileResource>(path)?.Data;
            }
            catch
            {
                return null;
            }
        };
        NavmeshCustomization.FlyingSupportedResolver = _ => false;

        var scene = LgbSceneReader.Read(game, bgPath, 0);
        var extractor = new SceneExtractor(scene);

        foreach (var (asset, mesh) in extractor.Meshes)
        {
            foreach (var instance in mesh.Instances)
            {
                var b = instance.WorldBounds;
                if (!near.Any(p => p.X >= b.Min.X - margin && p.X <= b.Max.X + margin
                    && p.Y >= b.Min.Y - margin && p.Y <= b.Max.Y + margin
                    && p.Z >= b.Min.Z - margin && p.Z <= b.Max.Z + margin))
                    continue;

                foreach (var part in mesh.Parts)
                {
                    var world = new Vector3[part.Vertices.Count];
                    for (int i = 0; i < part.Vertices.Count; ++i)
                    {
                        var w = instance.WorldTransform.TransformCoordinate(part.Vertices[i]);
                        world[i] = new Vector3(w.X, w.Y, w.Z);
                    }
                    foreach (var prim in part.Primitives)
                    {
                        var (a, bb, c) = (world[prim.V1], world[prim.V2], world[prim.V3]);
                        // only triangles near a point of interest; terrain parts are huge
                        if (!near.Any(p => NearTriangle(p, a, bb, c, margin)))
                            continue;
                        _tris.Add((a, bb, c, asset));
                    }
                }
            }
        }
    }

    public int TriangleCount => _tris.Count;

    /// <summary>The first collision triangle crossing the gap at body height, or null when the
    /// way is clear at every height.</summary>
    public (float Height, Vector3 Hit, string Asset)? Blocker(Vector3 from, Vector3 to)
    {
        var floor = MathF.Max(from.Y, to.Y);
        foreach (var h in BodyHeights)
        {
            var a = new Vector3(from.X, floor + h, from.Z);
            var b = new Vector3(to.X, floor + h, to.Z);
            var dir = b - a;
            foreach (var (t0, t1, t2, asset) in _tris)
            {
                if (SegmentHits(a, dir, t0, t1, t2, out var t))
                    return (h, a + dir * t, asset);
            }
        }
        return null;
    }

    private static bool NearTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c, float margin)
    {
        var min = Vector3.Min(a, Vector3.Min(b, c)) - new Vector3(margin);
        var max = Vector3.Max(a, Vector3.Max(b, c)) + new Vector3(margin);
        return p.X >= min.X && p.X <= max.X && p.Y >= min.Y && p.Y <= max.Y && p.Z >= min.Z && p.Z <= max.Z;
    }

    // Möller–Trumbore, restricted to the segment (t in [0, 1]), double-sided.
    private static bool SegmentHits(Vector3 origin, Vector3 dir, Vector3 v0, Vector3 v1, Vector3 v2, out float t)
    {
        t = 0;
        var e1 = v1 - v0;
        var e2 = v2 - v0;
        var p = Vector3.Cross(dir, e2);
        var det = Vector3.Dot(e1, p);
        if (MathF.Abs(det) < 1e-9f)
            return false;
        var inv = 1 / det;
        var s = origin - v0;
        var u = Vector3.Dot(s, p) * inv;
        if (u < 0 || u > 1)
            return false;
        var q = Vector3.Cross(s, e1);
        var v = Vector3.Dot(dir, q) * inv;
        if (v < 0 || u + v > 1)
            return false;
        t = Vector3.Dot(e2, q) * inv;
        return t >= 0 && t <= 1;
    }
}
