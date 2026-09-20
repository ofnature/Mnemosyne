using DotRecast.Detour;
using Lumina;
using Navmesh;
using System.Numerics;

namespace Mnemosyne.Builder;

// Find the obstacles the navmesh forgot to carve.
//
// The worst snags are not holes in the mesh — they are the opposite. A lamppost or a low wall
// whose collision Recast stepped over leaves walkable mesh running straight through a solid
// object. The route looks perfect, the follower drives into it, and the character shuffles
// against the geometry. Reported from the game as "there is a wall and the lamp post that are
// green and pathable, extremely bottish looking when I snag", and confirmed at Limsa Lominsa
// Upper Decks: zero unmeshed cells in a 16x16 m box that contains both.
//
// Path padding cannot help here by construction — it pushes away from mesh *boundaries*, and
// an obstacle inside walkable mesh has no boundary. The geometry has to be carved.
//
// Method: every near-vertical collision triangle is a face you would walk into. If walkable
// mesh sits at that triangle's own footprint, and the triangle rises through the space a body
// occupies above that floor, the mesh is inside a solid.
public static class SolidAudit
{
    /// <summary>A collision face standing in walkable mesh.</summary>
    public sealed record SolidHit(Vector3 Pos, float MeshY, float SpanBottom, float SpanTop, string Asset);

    /// <summary>Clustered hits — one lamppost produces dozens of triangles, and a list of
    /// dozens of triangles is not a work item.</summary>
    public sealed record SolidCluster(Vector3 Pos, float Radius, float Height, int Faces, string Asset);

    /// <summary>How far above the floor a body occupies. A face that only rises 10 cm is a
    /// kerb you step over, not an obstacle you snag on.</summary>
    private const float BodyBottom = 0.25f;
    private const float BodyTop = 1.6f;

    /// <summary>|normal.Y| below this is a wall rather than a floor or a ramp — roughly 60
    /// degrees, the same neighbourhood as Recast's walkable slope limit.</summary>
    private const float VerticalNormal = 0.5f;

    public static List<SolidCluster> Find(GameData game, string bgPath, DtNavMesh navmesh, float clusterRadius = 1.5f)
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

        var query = new DtNavMeshQuery(navmesh);
        var filter = new DtQueryDefaultFilter();
        var hits = new List<SolidHit>();

        foreach (var (asset, mesh) in extractor.Meshes)
        {
            // Terrain and bounding planes are the ground itself; of course the mesh sits on
            // them. Only placed geometry can be an uncarved obstacle.
            if (mesh.MeshType is SceneExtractor.MeshType.Terrain or SceneExtractor.MeshType.AnalyticPlane)
                continue;

            foreach (var instance in mesh.Instances)
            {
                var bounds = instance.WorldBounds;
                if (bounds.Max.Y - bounds.Min.Y < BodyBottom)
                    continue; // too flat to walk into

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
                        var flags = (prim.Flags & ~instance.ForceClearPrimFlags) | instance.ForceSetPrimFlags;
                        if (flags.HasFlag(SceneExtractor.PrimitiveFlags.FlyThrough))
                            continue; // deliberately non-blocking
                        if (flags.HasFlag(SceneExtractor.PrimitiveFlags.ForceWalkable))
                            continue; // authored as walkable on purpose

                        var a = world[prim.V1];
                        var b = world[prim.V2];
                        var c = world[prim.V3];
                        var normal = Vector3.Cross(b - a, c - a);
                        if (normal.LengthSquared() < 1e-8f)
                            continue;
                        normal = Vector3.Normalize(normal);
                        if (MathF.Abs(normal.Y) >= VerticalNormal)
                            continue; // a floor or a ramp, not a wall

                        var centre = (a + b + c) / 3;
                        var bottom = MathF.Min(a.Y, MathF.Min(b.Y, c.Y));
                        var top = MathF.Max(a.Y, MathF.Max(b.Y, c.Y));

                        // Walkable mesh at this face's own footprint? A tight XZ extent is the
                        // whole point: mesh *beside* a wall is correct, mesh *in* it is not.
                        query.FindNearestPoly(centre.SystemToRecast(), new(0.25f, 2.5f, 0.25f), filter,
                            out var polyRef, out var onMesh, out _);
                        if (polyRef == 0)
                            continue;
                        var meshY = onMesh.Y;

                        // and does the face rise through the space a body would occupy there?
                        if (top < meshY + BodyBottom || bottom > meshY + BodyTop)
                            continue;

                        hits.Add(new SolidHit(new Vector3(centre.X, meshY, centre.Z), meshY, bottom, top, asset));
                    }
                }
            }
        }

        return Cluster(hits, clusterRadius);
    }

    private static List<SolidCluster> Cluster(List<SolidHit> hits, float radius)
    {
        var clusters = new List<(Vector3 Sum, Vector3 Min, Vector3 Max, float Top, int Count, string Asset)>();
        foreach (var hit in hits)
        {
            var index = clusters.FindIndex(c =>
                Vector3.Distance(new Vector3(c.Sum.X / c.Count, c.Sum.Y / c.Count, c.Sum.Z / c.Count), hit.Pos) < radius
                && c.Asset == hit.Asset);
            if (index >= 0)
            {
                var c = clusters[index];
                clusters[index] = (c.Sum + hit.Pos, Vector3.Min(c.Min, hit.Pos), Vector3.Max(c.Max, hit.Pos),
                    MathF.Max(c.Top, hit.SpanTop), c.Count + 1, c.Asset);
            }
            else
            {
                clusters.Add((hit.Pos, hit.Pos, hit.Pos, hit.SpanTop, 1, hit.Asset));
            }
        }

        return [.. clusters.Select(c =>
        {
            var centre = c.Sum / c.Count;
            var extent = MathF.Max(c.Max.X - c.Min.X, c.Max.Z - c.Min.Z) * 0.5f;
            return new SolidCluster(centre, MathF.Max(extent, 0.4f), c.Top - centre.Y, c.Count, c.Asset);
        }).OrderByDescending(c => c.Faces)];
    }
}
