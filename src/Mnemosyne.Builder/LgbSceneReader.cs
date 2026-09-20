using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using Lumina;
using Lumina.Data;
using Navmesh;
using System.Numerics;

namespace Mnemosyne.Builder;

// Offline replacement for vnavmesh's SceneDefinition.FillFromLayout: reconstructs the
// collision scene by parsing LVB/LGB/SGB files RAW with FFXIVClientStructs' file-format
// structs - the game's own layout, including analytic collider data, material ids, and
// recursive SharedGroup (sgb) expansion that Lumina leaves unparsed.
// Remaining gaps vs in-game extraction: layer filters with Match/NoMatch conditions are
// included as-if-active, festival layers are skipped (baseline build), and shared groups
// are always instantiated in their default state (doors bake as authored).
public static unsafe class LgbSceneReader
{
    private const int MaxSgbDepth = 8;

    private sealed class Context
    {
        public required GameData Game;
        public required SceneDefinition Scene;
        public ulong NextKey = 1;
        public uint NextCrc = 1;
        public readonly Dictionary<string, uint> PathToCrc = [];
        public int FestivalLayers;
        public int FilteredLayers;
        public int SharedGroups;

        public uint CrcFor(string path)
        {
            if (!PathToCrc.TryGetValue(path, out var crc))
            {
                crc = NextCrc++;
                PathToCrc[path] = crc;
                Scene.MeshPaths[crc] = path;
            }
            return crc;
        }
    }

    public static SceneDefinition Read(GameData game, string bgPath, uint territoryId)
    {
        var scene = new SceneDefinition { TerritoryID = territoryId };
        var ctx = new Context { Game = game, Scene = scene };
        var basePath = "bg/" + bgPath[..(bgPath.IndexOf("/level/", StringComparison.Ordinal) + 1)];

        // the LVB is the authoritative entry point: terrain path + the list of lgb files
        var lgbPaths = new List<string>();
        var lvbBytes = TryGetFile(game, "bg/" + bgPath + ".lvb");
        if (lvbBytes != null)
        {
            fixed (byte* p = lvbBytes)
            {
                foreach (var section in ((FileHeader*)p)->Sections)
                {
                    if (section->Magic != 0x314E4353u) // SCN1
                        continue;
                    var sceneHeader = section->Data<FileSceneHeader>();
                    var general = sceneHeader->General;
                    if (general != null && general->PathTerrain.HasValue)
                    {
                        var terrainPath = general->PathTerrain.ToString();
                        if (terrainPath.Length > 0)
                            scene.Terrains.Add(terrainPath + "/collision");
                    }
                    foreach (var resOffset in sceneHeader->LayerGroupResourceOffsets)
                    {
                        var pathPtr = sceneHeader->LayerGroupResource(resOffset);
                        var path = ReadCString(pathPtr);
                        if (path.EndsWith(".lgb", StringComparison.OrdinalIgnoreCase))
                            lgbPaths.Add(path);
                    }
                    break;
                }
            }
        }
        if (lgbPaths.Count == 0) // fallback: conventional names
            lgbPaths.AddRange(new[] { "bg.lgb", "planmap.lgb", "planevent.lgb", "planlive.lgb" }.Select(n => basePath + "level/" + n));

        foreach (var lgbPath in lgbPaths)
        {
            var bytes = TryGetFile(game, lgbPath);
            if (bytes is not { Length: > 0x20 })
                continue;
            fixed (byte* p = bytes)
            {
                foreach (var section in ((FileHeader*)p)->Sections)
                {
                    if (section->Magic == 0x3150474Cu) // LGP1
                        ProcessLayerGroup(ctx, section->Data<FileLayerGroupHeader>(), null, 0);
                }
            }
        }

        Console.WriteLine($"layers: {ctx.FestivalLayers} festival skipped, {ctx.FilteredLayers} conditionally-filtered included, {ctx.SharedGroups} shared groups expanded");
        return scene;
    }

    private static byte[]? TryGetFile(GameData game, string path)
    {
        try
        {
            return game.GetFile<FileResource>(path)?.Data;
        }
        catch
        {
            return null;
        }
    }

    private static void ProcessLayerGroup(Context ctx, FileLayerGroupHeader* header, Matrix4x4? parent, int depth)
    {
        foreach (var layerOffset in header->LayerOffsets)
        {
            var layer = header->Layer(layerOffset);
            if (*(uint*)((byte*)layer + 0x18) != 0) // GameMain.Festival at 0x18
            {
                ++ctx.FestivalLayers;
                continue; // baseline build: festival layers off
            }
            var filter = layer->Filter;
            if (filter != null && filter->Operation != FileLayerGroupLayerFilter.Op.None)
                ++ctx.FilteredLayers; // included as-if-active; we don't know the layer-set key

            foreach (var instanceOffset in layer->InstanceOffsets)
                ProcessInstance(ctx, layer->Instance(instanceOffset), parent, depth);
        }
    }

    private static void ProcessInstance(Context ctx, FileLayerGroupInstance* inst, Matrix4x4? parent, int depth)
    {
        switch (inst->Type)
        {
            case InstanceType.BgPart:
            {
                var bg = (FileLayerGroupInstanceBgPart*)inst;
                var matId = ((ulong)bg->MaterialIdHigh << 32) | bg->MaterialIdLow;
                var matMask = ((ulong)bg->MaterialMaskHigh << 32) | bg->MaterialMaskLow;
                if (bg->ColliderType == FileLayerGroupInstanceBgPart.Collider.Analytic && bg->ColliderAnalyticData != null)
                {
                    var analytic = bg->ColliderAnalyticData;
                    var shapeTransform = MakeTransform(analytic->Transform, null);
                    shapeTransform.Type = (int)analytic->ColliderType; // SceneExtractor reads the type from here
                    uint crc = ctx.NextCrc++;
                    ctx.Scene.AnalyticShapes[crc] = (shapeTransform, analytic->Bounds.Min, analytic->Bounds.Max);
                    ctx.Scene.BgParts.Add((ctx.NextKey++, MakeTransform(inst->Transform, parent),
                        crc, matId | analytic->MaterialId, matMask | analytic->MaterialMask, true));
                }
                else if (bg->ColliderType == FileLayerGroupInstanceBgPart.Collider.Mesh && bg->PathPcb.HasValue)
                {
                    var pcbPath = bg->PathPcb.ToString();
                    if (pcbPath.Length > 0)
                        ctx.Scene.BgParts.Add((ctx.NextKey++, MakeTransform(inst->Transform, parent), ctx.CrcFor(pcbPath), matId, matMask, false));
                }
                break;
            }

            case InstanceType.CollisionBox:
            {
                var cb = (FileLayerGroupInstanceCollisionBox*)inst;
                if (!cb->ActiveByDefault)
                    break; // inactive colliders under normal conditions
                var matId = ((ulong)cb->MaterialIdHigh << 32) | cb->MaterialIdLow;
                var matMask = ((ulong)cb->MaterialMaskHigh << 32) | cb->MaterialMaskLow;
                uint crc = 0;
                if (cb->ColliderType == FFXIVClientStructs.FFXIV.Client.LayoutEngine.Layer.ColliderType.Mesh)
                {
                    if (!cb->Path.HasValue)
                        break;
                    var pcbPath = cb->Path.ToString();
                    if (pcbPath.Length == 0)
                        break;
                    crc = ctx.CrcFor(pcbPath);
                }
                ctx.Scene.Colliders.Add((ctx.NextKey++, MakeTransform(inst->Transform, parent), crc, matId, matMask, cb->ColliderType));
                break;
            }

            case InstanceType.ExitRange:
                ctx.Scene.ExitRanges.Add((ctx.NextKey++, MakeTransform(inst->Transform, parent)));
                break;

            case InstanceType.SharedGroup:
            {
                if (depth >= MaxSgbDepth)
                    break;
                var sg = (FileLayerGroupInstanceSharedGroup*)inst;
                if (!sg->Path.HasValue)
                    break;
                var sgbPath = sg->Path.ToString();
                if (!sgbPath.EndsWith(".sgb", StringComparison.OrdinalIgnoreCase))
                    break;
                var bytes = TryGetFile(ctx.Game, sgbPath);
                if (bytes is not { Length: > 0x20 })
                    break;
                ++ctx.SharedGroups;
                var world = ComposeLocal(inst->Transform);
                if (parent is { } pm)
                    world *= pm;
                fixed (byte* p = bytes)
                {
                    foreach (var section in ((FileHeader*)p)->Sections)
                    {
                        if (section->Magic != 0x314E4353u) // SCN1
                            continue;
                        var sceneHeader = section->Data<FileSceneHeader>();
                        foreach (ref var group in sceneHeader->EmbeddedLayerGroups)
                            ProcessLayerGroup(ctx, (FileLayerGroupHeader*)System.Runtime.CompilerServices.Unsafe.AsPointer(ref group), world, depth + 1);
                    }
                }
                break;
            }
        }
    }

    private static Matrix4x4 ComposeLocal(in FileLayerGroupTransform t)
    {
        // matches the game's S * Rx * Ry * Rz * T composition (verified against rendered zones)
        var m = Matrix4x4.CreateScale(t.Scale)
            * Matrix4x4.CreateRotationX(t.Rotation.X) * Matrix4x4.CreateRotationY(t.Rotation.Y) * Matrix4x4.CreateRotationZ(t.Rotation.Z);
        m.Translation = t.Translation;
        return m;
    }

    private static Transform MakeTransform(in FileLayerGroupTransform t, Matrix4x4? parent)
    {
        var world = ComposeLocal(t);
        if (parent is { } pm)
            world *= pm;
        if (!Matrix4x4.Decompose(world, out var scale, out var rotation, out var translation))
        {
            // sheared transform (non-uniform scale under rotation) - fall back to a best-effort TRS
            translation = world.Translation;
            rotation = Quaternion.CreateFromRotationMatrix(world);
            scale = new(new Vector3(world.M11, world.M12, world.M13).Length());
        }
        return new Transform { Translation = translation, Rotation = rotation, Scale = scale };
    }

    private static string ReadCString(byte* p)
    {
        if (p == null)
            return "";
        int len = 0;
        while (p[len] != 0)
            ++len;
        return System.Text.Encoding.UTF8.GetString(p, len);
    }
}
