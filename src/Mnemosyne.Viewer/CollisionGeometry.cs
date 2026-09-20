using Mnemosyne.Builder;
using Navmesh;
using Vector3 = System.Numerics.Vector3;

namespace Mnemosyne.Viewer;

// The collision scene Recast actually rasterized: PCB collision meshes and analytic
// colliders, transformed exactly the way NavmeshRasterizer does. Visual models can differ
// wildly from collision (decorative geometry has none; invisible walls have nothing to
// see), so this is the view that explains a navmesh hole or a phantom wall.
// Colours follow the rasterizer's own flag rules.
public sealed class CollisionGeometry
{
    public List<MeshBatch> Batches = [];
    public int Instances;
    public int Tris;

    private static readonly Vector3 LightDir = Vector3.Normalize(new(0.35f, 0.8f, 0.45f));

    // matches the rasterizer's interpretation of the primitive flags
    private static readonly (byte R, byte G, byte B) Walkable = (110, 190, 130); // rasterized as ground
    private static readonly (byte R, byte G, byte B) Unwalkable = (200, 80, 70); // too steep / forced off
    private static readonly (byte R, byte G, byte B) FlyThrough = (90, 150, 220); // solid for walking, not for the fly volume
    private static readonly (byte R, byte G, byte B) Unlandable = (215, 165, 70); // can fly past, can't land

    public static CollisionGeometry Build(string sqpackDir, string bgPath)
    {
        var game = TerritoryGeometry.SharedGameData(sqpackDir);
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

        var scene = LgbSceneReader.Read(game, bgPath, 0);
        var extractor = new SceneExtractor(scene);

        var builder = new Builder();
        int instances = 0;
        foreach (var (_, mesh) in extractor.Meshes)
        {
            foreach (var instance in mesh.Instances)
            {
                ++instances;
                foreach (var part in mesh.Parts)
                {
                    // world-space vertices first (same TransformCoordinate the rasterizer uses)
                    var world = new Vector3[part.Vertices.Count];
                    for (int i = 0; i < part.Vertices.Count; ++i)
                        world[i] = instance.WorldTransform.TransformCoordinate(part.Vertices[i]);

                    foreach (var prim in part.Primitives)
                    {
                        var flags = (prim.Flags | instance.ForceSetPrimFlags) & ~instance.ForceClearPrimFlags;
                        var tint = flags.HasFlag(SceneExtractor.PrimitiveFlags.ForceUnwalkable) ? Unwalkable
                            : flags.HasFlag(SceneExtractor.PrimitiveFlags.FlyThrough) ? FlyThrough
                            : flags.HasFlag(SceneExtractor.PrimitiveFlags.Unlandable) && !flags.HasFlag(SceneExtractor.PrimitiveFlags.ForceWalkable) ? Unlandable
                            : Walkable;

                        var a = world[prim.V1];
                        var b = world[prim.V2];
                        var c = world[prim.V3];
                        var normal = Vector3.Cross(b - a, c - a);
                        float len = normal.Length();
                        float shade = len > 1e-6f ? 0.55f + 0.45f * MathF.Abs(Vector3.Dot(normal / len, LightDir)) : 1f;

                        builder.EnsureRoom(3);
                        int baseVert = builder.CurrentVerts;
                        foreach (var v in (ReadOnlySpan<Vector3>)[a, b, c])
                            builder.AddVertex(v, shade, tint);
                        builder.AddIndex(baseVert);
                        builder.AddIndex(baseVert + 1);
                        builder.AddIndex(baseVert + 2);
                    }
                }
            }
        }
        builder.Flush();
        Console.WriteLine($"Collision: {extractor.Meshes.Count} meshes, {instances} instances, {builder.Tris} tris");
        return new() { Batches = builder.Batches, Instances = instances, Tris = builder.Tris };
    }

    // same batching rules as TerritoryGeometry (raylib meshes cap at 65536 verts)
    private sealed class Builder
    {
        public readonly List<MeshBatch> Batches = [];
        public int Tris;
        private readonly List<float> _pos = [];
        private readonly List<byte> _col = [];
        private readonly List<ushort> _idx = [];

        public int CurrentVerts => _pos.Count / 3;

        public void EnsureRoom(int verts)
        {
            if (CurrentVerts + verts > 65536)
                Flush();
        }

        public void AddVertex(Vector3 p, float shade, (byte R, byte G, byte B) tint)
        {
            _pos.Add(p.X); _pos.Add(p.Y); _pos.Add(p.Z);
            _col.Add((byte)(tint.R * shade)); _col.Add((byte)(tint.G * shade)); _col.Add((byte)(tint.B * shade)); _col.Add(255);
        }

        public void AddIndex(int i)
        {
            _idx.Add((ushort)i);
            if (_idx.Count % 3 == 0)
                ++Tris;
        }

        public void Flush()
        {
            if (_idx.Count > 0)
                Batches.Add(new() { Positions = [.. _pos], Colors = [.. _col], Indices = [.. _idx] });
            _pos.Clear();
            _col.Clear();
            _idx.Clear();
        }
    }
}
