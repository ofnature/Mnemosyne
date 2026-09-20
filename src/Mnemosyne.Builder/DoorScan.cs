using Lumina.Data.Files;
using Lumina.Data.Parsing.Layer;
using Vector3 = System.Numerics.Vector3;

namespace Mnemosyne.Builder;

// Scans a zone's LGB layers for door-like objects: explicit DoorRange trigger volumes,
// plus SharedGroup (sgb) instances whose asset path smells like a door/gate. The scan
// will miss things - the manual edit tools cover the rest.
/// <summary>A door-like object in the world: an explicit DoorRange volume, or a shared
/// group whose asset path smells like a door. HalfExtents is the volume to test/edit.</summary>
public sealed record DoorMarker(Vector3 Pos, Vector3 HalfExtents, string Label, string? AssetPath, uint InstanceId);

public static class DoorScan
{
    // Substrings that mark a door-like shared group. "_dor" is the abbreviation the asset
    // names actually use (sgbg_m5f1_a1_dor01 is the Yedlihmad doorway) - matching only the
    // full word "door" missed it, and with it the whole class this scanner exists to find.
    // Kept as "_dor" rather than "dor" so "corridor" and friends do not match.
    private static readonly string[] DoorHints =
        ["door", "_dor", "gate", "shutter", "fence", "wall_move", "hoba", "_gat", "_sht"];

    public static List<DoorMarker> Scan(string sqpackDir, string bgPath)
    {
        var markers = new List<DoorMarker>();
        try
        {
            var game = new Lumina.GameData(sqpackDir);
            var basePath = "bg/" + bgPath[..(bgPath.IndexOf("/level/", StringComparison.Ordinal) + 1)];
            foreach (var lgbName in new[] { "bg.lgb", "planmap.lgb", "planevent.lgb" })
            {
                LgbFile? lgb;
                try
                {
                    lgb = game.GetFile<LgbFile>(basePath + "level/" + lgbName);
                }
                catch
                {
                    continue;
                }
                if (lgb == null)
                    continue;
                foreach (var layer in lgb.Layers)
                {
                    foreach (var obj in layer.InstanceObjects)
                    {
                        var t = obj.Transform;
                        var pos = new Vector3(t.Translation.X, t.Translation.Y, t.Translation.Z);
                        var scale = new Vector3(MathF.Abs(t.Scale.X), MathF.Abs(t.Scale.Y), MathF.Abs(t.Scale.Z));
                        switch (obj.AssetType)
                        {
                            case LayerEntryType.DoorRange:
                                markers.Add(new(pos, Clamp(scale), "door range", null, obj.InstanceId));
                                break;
                            case LayerEntryType.SharedGroup when obj.Object is LayerCommon.SharedGroupInstanceObject sg
                                && sg.AssetPath is { Length: > 0 } asset
                                && (sg.InitialDoorState is DoorState.Open or DoorState.Closed
                                    || DoorHints.Any(h => asset.Contains(h, StringComparison.OrdinalIgnoreCase))):
                                markers.Add(new(pos, Clamp(scale * 3),
                                    $"{Path.GetFileNameWithoutExtension(asset)}{(sg.InitialDoorState is DoorState.Open or DoorState.Closed ? $" ({sg.InitialDoorState})" : "")}",
                                    asset, obj.InstanceId));
                                break;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"door scan failed: {ex.Message}");
        }
        return markers;
    }

    private static Vector3 Clamp(Vector3 v) => new(
        Math.Clamp(v.X, 2, 12), Math.Clamp(v.Y, 3, 12), Math.Clamp(v.Z, 2, 12));
}
