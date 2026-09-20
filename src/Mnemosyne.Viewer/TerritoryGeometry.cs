using Lumina;
using Lumina.Data.Files;
using Lumina.Data.Parsing.Layer;
using Lumina.Models.Materials;
using Lumina.Models.Models;
using System.Text.Json;
using Matrix4x4 = System.Numerics.Matrix4x4;
using Vector3 = System.Numerics.Vector3;

namespace Mnemosyne.Viewer;

// One GPU-uploadable indexed chunk (raylib meshes use ushort indices, so <= 65536 verts each).
public sealed class MeshBatch
{
    public float[] Positions = [];
    public byte[] Colors = [];
    public ushort[] Indices = [];
}

// Real zone geometry (terrain plates + lgb static models) extracted from game data via
// Lumina, baked to flat gray indexed batches for use as a backdrop.
public sealed class TerritoryGeometry
{
    public List<MeshBatch> Batches = [];
    public int Models;
    public int Tris;

    private static readonly Vector3 LightDir = Vector3.Normalize(new(0.35f, 0.8f, 0.45f));
    private const byte BaseR = 96, BaseG = 99, BaseB = 108;
    private const float MinPropRadius = 4.0f; // world-space; smaller props are skipped
    private const long LgbTriBudget = 6_000_000; // largest instances first; rest dropped
    private const int MaxSgbDepth = 8;
    private static readonly (byte R, byte G, byte B) WaterTint = (60, 110, 150);

    private static readonly object GameLock = new();
    private static GameData? _game;
    private static string? _gameDir;

    public static string? FindSqpackDir() => Mnemosyne.Core.GamePaths.FindSqpackDir();

    public static GameData SharedGameData(string sqpackDir) => GetGameData(sqpackDir);

    private static GameData GetGameData(string sqpackDir)
    {
        lock (GameLock)
        {
            if (_game == null || _gameDir != sqpackDir)
            {
                _game = new GameData(sqpackDir);
                _gameDir = sqpackDir;
            }
            return _game;
        }
    }

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

        public void AddIndex(int i) => _idx.Add((ushort)i);

        public void Flush()
        {
            if (_idx.Count > 0)
                Batches.Add(new() { Positions = [.. _pos], Colors = [.. _col], Indices = [.. _idx] });
            _pos.Clear();
            _col.Clear();
            _idx.Clear();
        }
    }

    public static TerritoryGeometry Build(string sqpackDir, string bgPath)
    {
        var game = GetGameData(sqpackDir);
        var basePath = "bg/" + bgPath[..(bgPath.IndexOf("/level/", StringComparison.Ordinal) + 1)];

        var builder = new Builder();
        var modelCache = new Dictionary<string, Model?>();
        int models = 0, failed = 0, culled = 0;

        // terrain plates
        try
        {
            var tera = game.GetFile<TeraFile>(basePath + "bgplate/terrain.tera");
            if (tera != null)
            {
                for (int i = 0; i < tera.PlateCount; ++i)
                {
                    try
                    {
                        var mdl = game.GetFile<MdlFile>($"{basePath}bgplate/{i:D4}.mdl");
                        if (mdl == null)
                        {
                            ++failed;
                            continue;
                        }
                        var p = tera.GetPlatePosition(i);
                        AddModel(game, new Model(mdl), Matrix4x4.CreateTranslation(p.X, 0, p.Y), builder);
                        ++models;
                    }
                    catch (Exception ex)
                    {
                        if (failed < 3)
                            Console.WriteLine($"Backdrop: plate {i}: {ex.Message}");
                        ++failed;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Backdrop: terrain failed: {ex.Message}");
        }
        int terrainTris = builder.Tris;

        // static bg models from the layer group files: collect all instances first, then
        // emit largest-first under a global triangle budget so landmarks always survive.
        // SharedGroups (.sgb) are expanded recursively - most set dressing (trees, lamps,
        // dungeon set pieces, housing) lives inside them, not directly in the lgb.
        var trisCache = new Dictionary<string, int>();
        var instances = new List<(Model Model, Matrix4x4 M, float Radius, int Tris)>();
        int sharedGroups = 0;

        void CollectInstances(IEnumerable<LayerCommon.InstanceObject> objects, Matrix4x4 parent, int depth)
        {
            foreach (var obj in objects)
            {
                var t = obj.Transform;
                // scale, then XYZ rotation, then translation (row-vector order)
                var local = Matrix4x4.CreateScale(t.Scale.X, t.Scale.Y, t.Scale.Z)
                    * Matrix4x4.CreateRotationX(t.Rotation.X)
                    * Matrix4x4.CreateRotationY(t.Rotation.Y)
                    * Matrix4x4.CreateRotationZ(t.Rotation.Z)
                    * Matrix4x4.CreateTranslation(t.Translation.X, t.Translation.Y, t.Translation.Z);
                var world = local * parent;

                switch (obj.AssetType)
                {
                    case LayerEntryType.BG when obj.Object is LayerCommon.BGInstanceObject bgObj
                        && !string.IsNullOrWhiteSpace(bgObj.AssetPath):
                        try
                        {
                            if (!modelCache.TryGetValue(bgObj.AssetPath, out var model))
                            {
                                var mdl = game.GetFile<MdlFile>(bgObj.AssetPath);
                                model = mdl != null ? new Model(mdl) : null;
                                modelCache[bgObj.AssetPath] = model;
                                trisCache[bgObj.AssetPath] = model?.GetMeshesByType(Mesh.MeshType.Main).Sum(x => x.Indices.Length / 3) ?? 0;
                            }
                            if (model == null)
                            {
                                ++failed;
                                break;
                            }
                            // cull small clutter props - the backdrop needs recognizable structure.
                            // scale comes from the composed world matrix so nested groups scale too
                            var scale = MathF.Max(new Vector3(world.M11, world.M12, world.M13).Length(),
                                MathF.Max(new Vector3(world.M21, world.M22, world.M23).Length(),
                                    new Vector3(world.M31, world.M32, world.M33).Length()));
                            var worldRadius = model.File!.ModelHeader.Radius * scale;
                            if (worldRadius < MinPropRadius)
                            {
                                ++culled;
                                break;
                            }
                            instances.Add((model, world, worldRadius, trisCache[bgObj.AssetPath]));
                        }
                        catch (Exception ex)
                        {
                            if (failed < 3)
                                Console.WriteLine($"Backdrop: {bgObj.AssetPath}: {ex.Message}");
                            ++failed;
                        }
                        break;

                    case LayerEntryType.SharedGroup when depth < MaxSgbDepth
                        && obj.Object is LayerCommon.SharedGroupInstanceObject sg
                        && sg.AssetPath is { Length: > 0 } sgbPath
                        && sgbPath.EndsWith(".sgb", StringComparison.OrdinalIgnoreCase):
                        try
                        {
                            var sgb = game.GetFile<SgbFile>(sgbPath);
                            if (sgb?.LayerGroups == null)
                                break;
                            ++sharedGroups;
                            foreach (var group in sgb.LayerGroups)
                                foreach (var layer in group.Layers)
                                    CollectInstances(layer.InstanceObjects, world, depth + 1);
                        }
                        catch (Exception ex)
                        {
                            if (failed < 3)
                                Console.WriteLine($"Backdrop: {sgbPath}: {ex.Message}");
                            ++failed;
                        }
                        break;
                }
            }
        }

        foreach (var lgbName in new[] { "bg.lgb", "planmap.lgb", "planevent.lgb" })
        {
            try
            {
                var lgb = game.GetFile<LgbFile>(basePath + "level/" + lgbName);
                if (lgb == null)
                    continue;
                foreach (var layer in lgb.Layers)
                    CollectInstances(layer.InstanceObjects, Matrix4x4.Identity, 0);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Backdrop: {lgbName} failed: {ex.Message}");
            }
        }
        Console.WriteLine($"Backdrop: {instances.Count} instances ({sharedGroups} shared groups expanded)");

        instances.Sort((a, b) => b.Radius.CompareTo(a.Radius));
        long budget = LgbTriBudget;
        int dropped = 0;
        foreach (var inst in instances)
        {
            if (inst.Tris > budget)
            {
                ++dropped;
                continue;
            }
            budget -= inst.Tris;
            try
            {
                AddModel(game, inst.Model, inst.M, builder);
                ++models;
            }
            catch
            {
                ++failed;
            }
        }
        if (dropped > 0)
            Console.WriteLine($"Backdrop: dropped {dropped} of {instances.Count} instances to stay under {LgbTriBudget / 1000000}M tris");

        builder.Flush();
        Console.WriteLine($"Backdrop: terrain {terrainTris / 1000}k tris, lgb {(builder.Tris - terrainTris) / 1000}k tris");
        if (failed > 0 || culled > 0)
            Console.WriteLine($"Backdrop: skipped {failed} unloadable, culled {culled} small props");
        return new() { Batches = builder.Batches, Models = models, Tris = builder.Tris };
    }

    private static void AddModel(GameData game, Model model, Matrix4x4 m, Builder builder)
    {
        // water is a separate mesh type the game renders apart from Main; without it lakes,
        // rivers and the ocean are invisible holes in the backdrop
        foreach (var mesh in model.GetMeshesByType(Mesh.MeshType.Water))
            AddMeshGeometry(mesh, m, builder, WaterTint);

        foreach (var mesh in model.GetMeshesByType(Mesh.MeshType.Main))
            AddMeshGeometry(mesh, m, builder, MaterialTint(game, mesh));
    }

    private static void AddMeshGeometry(Mesh mesh, Matrix4x4 m, Builder builder, (byte R, byte G, byte B) tint)
    {
        var verts = mesh.Vertices;
        var idx = mesh.Indices;
        if (verts.Length == 0 || idx.Length == 0)
            return;

        if (verts.Length <= 65536)
        {
            builder.EnsureRoom(verts.Length);
            int baseVert = builder.CurrentVerts;
            foreach (var v in verts)
                builder.AddVertex(TransformPosition(v, m), Shade(v, m), tint);
            foreach (var i in idx)
                builder.AddIndex(baseVert + i);
            builder.Tris += idx.Length / 3;
        }
        else
        {
            // huge mesh: emit unindexed triangles across batches
            for (int i = 0; i + 2 < idx.Length; i += 3)
            {
                builder.EnsureRoom(3);
                int baseVert = builder.CurrentVerts;
                foreach (var vi in (ReadOnlySpan<ushort>)[idx[i], idx[i + 1], idx[i + 2]])
                {
                    var v = verts[vi];
                    builder.AddVertex(TransformPosition(v, m), Shade(v, m), tint);
                }
                builder.AddIndex(baseVert);
                builder.AddIndex(baseVert + 1);
                builder.AddIndex(baseVert + 2);
                ++builder.Tris;
            }
        }
    }

    // average color of the material's diffuse texture; gray fallback. Cached per material path.
    private static readonly Dictionary<string, (byte R, byte G, byte B)> TintCache = [];

    private static (byte R, byte G, byte B) MaterialTint(GameData game, Mesh mesh)
    {
        string key;
        Material mat;
        try
        {
            mat = mesh.Material;
            key = mat.MaterialPath ?? "";
        }
        catch
        {
            return (BaseR, BaseG, BaseB);
        }

        lock (TintCache)
        {
            if (TintCache.TryGetValue(key, out var cached))
                return cached;
        }

        (byte R, byte G, byte B) tint = (BaseR, BaseG, BaseB);
        try
        {
            mat.Update(game);
            var tex = mat.Textures.FirstOrDefault(t => t.TextureUsageSimple == Texture.Usage.Diffuse)
                ?? mat.Textures.FirstOrDefault(t => t.TexturePath.Contains("_d.", StringComparison.OrdinalIgnoreCase))
                ?? mat.Textures.FirstOrDefault();
            var texFile = tex?.GetTextureNc(game); // GetTexture() has an inverted null check upstream
            if (texFile != null)
            {
                // sample a small mip; BGRA order
                var mip = Math.Clamp(texFile.Header.MipCount - 1, 0, 2);
                var data = texFile.TextureBuffer.Filter(mip: mip, format: TexFile.TextureFormat.B8G8R8A8).RawData;
                long r = 0, g = 0, b = 0, n = 0;
                int stride = Math.Max(1, data.Length / 4 / 2048) * 4;
                for (int i = 0; i + 3 < data.Length; i += stride)
                {
                    if (data[i + 3] < 32)
                        continue;
                    b += data[i]; g += data[i + 1]; r += data[i + 2]; ++n;
                }
                if (n > 16)
                {
                    // lift toward mid-gray a little so the backdrop stays muted next to the navmesh
                    tint = ((byte)((r / n * 3 + BaseR) / 4), (byte)((g / n * 3 + BaseG) / 4), (byte)((b / n * 3 + BaseB) / 4));
                }
            }
        }
        catch
        {
            // missing/broken material or texture - keep gray
        }

        lock (TintCache)
            TintCache[key] = tint;
        return tint;
    }

    private static Vector3 TransformPosition(in Vertex v, in Matrix4x4 m)
    {
        var p = v.Position!.Value;
        return Vector3.Transform(new Vector3(p.X, p.Y, p.Z), m);
    }

    private static float Shade(in Vertex v, in Matrix4x4 m)
    {
        if (v.Normal is not { } n)
            return 1.0f;
        var wn = Vector3.TransformNormal(new Vector3(n.X, n.Y, n.Z), m);
        float len = wn.Length();
        return len > 1e-6f ? 0.5f + 0.5f * MathF.Abs(Vector3.Dot(wn / len, LightDir)) : 1.0f;
    }
}
