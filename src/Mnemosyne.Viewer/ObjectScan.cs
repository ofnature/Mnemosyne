using Lumina;
using Lumina.Data.Files;
using Lumina.Data.Parsing.Layer;
using Lumina.Excel.Sheets;
using Vector3 = System.Numerics.Vector3;

namespace Mnemosyne.Viewer;

public enum MarkerKind { Aetheryte, EventNpc, BattleNpc, EventObject, Treasure, PopRange, ExitRange }

public sealed record ObjectMarker(MarkerKind Kind, Vector3 Pos, string Label);

// Scans a zone's LGB layers for gameplay objects worth seeing on the map: aetherytes,
// NPCs, interactables, treasure, spawn points and zone exits. Names come from the game's
// own sheets via BaseId where a sheet exists; otherwise the id is shown.
// SharedGroups are expanded (like the backdrop) since a lot of EObjs live inside them.
public static class ObjectScan
{
    private const int MaxSgbDepth = 6;

    public static List<ObjectMarker> Scan(string sqpackDir, string bgPath)
    {
        var markers = new List<ObjectMarker>();
        try
        {
            var game = TerritoryGeometry.SharedGameData(sqpackDir);
            var basePath = "bg/" + bgPath[..(bgPath.IndexOf("/level/", StringComparison.Ordinal) + 1)];

            var eobjNames = TryGetSheet<EObjName>(game);
            var enpcNames = TryGetSheet<ENpcResident>(game);
            var aetherytes = TryGetSheet<Aetheryte>(game);
            var placeNames = TryGetSheet<PlaceName>(game);

            string Named(uint id, Func<uint, string?> lookup, string fallback)
            {
                var name = lookup(id);
                return string.IsNullOrWhiteSpace(name) ? $"{fallback} #{id}" : name!;
            }

            void Collect(IEnumerable<LayerCommon.InstanceObject> objects, System.Numerics.Matrix4x4 parent, int depth)
            {
                foreach (var obj in objects)
                {
                    var t = obj.Transform;
                    var local = System.Numerics.Matrix4x4.CreateScale(t.Scale.X, t.Scale.Y, t.Scale.Z)
                        * System.Numerics.Matrix4x4.CreateRotationX(t.Rotation.X)
                        * System.Numerics.Matrix4x4.CreateRotationY(t.Rotation.Y)
                        * System.Numerics.Matrix4x4.CreateRotationZ(t.Rotation.Z)
                        * System.Numerics.Matrix4x4.CreateTranslation(t.Translation.X, t.Translation.Y, t.Translation.Z);
                    var world = local * parent;
                    var pos = world.Translation;

                    switch (obj.AssetType)
                    {
                        case LayerEntryType.Aetheryte when obj.Object is LayerCommon.AetheryteInstanceObject ae:
                        {
                            var id = ae.ParentData.BaseId;
                            var label = "Aetheryte";
                            if (aetherytes?.GetRowOrDefault(id) is { } row && placeNames != null
                                && placeNames.GetRowOrDefault(row.PlaceName.RowId) is { } pn && pn.Name.ToString() is { Length: > 0 } nm)
                                label = $"Aetheryte: {nm}";
                            markers.Add(new(MarkerKind.Aetheryte, pos, label));
                            break;
                        }
                        case LayerEntryType.EventNPC when obj.Object is LayerCommon.ENPCInstanceObject npc:
                            markers.Add(new(MarkerKind.EventNpc, pos,
                                Named(npc.ParentData.ParentData.BaseId, id => enpcNames?.GetRowOrDefault(id)?.Singular.ToString(), "NPC")));
                            break;
                        // Lumina keeps BNPCInstanceObject internal, so its NameId is out of
                        // reach - the spawn position is the useful part anyway
                        case LayerEntryType.BattleNPC:
                            markers.Add(new(MarkerKind.BattleNpc, pos, "Enemy spawn"));
                            break;
                        case LayerEntryType.EventObject when obj.Object is LayerCommon.EventInstanceObject eobj:
                            markers.Add(new(MarkerKind.EventObject, pos,
                                Named(eobj.ParentData.BaseId, id => eobjNames?.GetRowOrDefault(id)?.Singular.ToString(), "Object")));
                            break;
                        case LayerEntryType.Treasure:
                            markers.Add(new(MarkerKind.Treasure, pos, "Treasure"));
                            break;
                        case LayerEntryType.PopRange:
                            markers.Add(new(MarkerKind.PopRange, pos, "Spawn"));
                            break;
                        case LayerEntryType.ExitRange:
                            markers.Add(new(MarkerKind.ExitRange, pos, "Zone exit"));
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
                                foreach (var group in sgb.LayerGroups)
                                    foreach (var layer in group.Layers)
                                        Collect(layer.InstanceObjects, world, depth + 1);
                            }
                            catch
                            {
                                // unreadable shared group - skip
                            }
                            break;
                    }
                }
            }

            foreach (var lgbName in new[] { "bg.lgb", "planmap.lgb", "planevent.lgb", "planlive.lgb" })
            {
                try
                {
                    var lgb = game.GetFile<LgbFile>(basePath + "level/" + lgbName);
                    if (lgb == null)
                        continue;
                    foreach (var layer in lgb.Layers)
                        Collect(layer.InstanceObjects, System.Numerics.Matrix4x4.Identity, 0);
                }
                catch
                {
                    // missing/corrupt layer file - skip
                }
            }
            Console.WriteLine($"Objects: {markers.Count} markers ("
                + string.Join(", ", markers.GroupBy(m => m.Kind).OrderByDescending(g => g.Count()).Select(g => $"{g.Count()} {g.Key}")) + ")");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"object scan failed: {ex.Message}");
        }
        return markers;
    }

    private static Lumina.Excel.ExcelSheet<T>? TryGetSheet<T>(GameData game) where T : struct, Lumina.Excel.IExcelRow<T>
    {
        try
        {
            return game.GetExcelSheet<T>();
        }
        catch
        {
            return null; // sheet layout changed - markers fall back to ids
        }
    }
}
