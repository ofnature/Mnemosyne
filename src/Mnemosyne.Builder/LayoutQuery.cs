using Lumina.Data.Files;
using Lumina.Data.Parsing.Layer;
using Vector3 = System.Numerics.Vector3;

namespace Mnemosyne.Builder;

// "What is this thing blocking me?" — every layout object near a point, with its type,
// asset and shared-group state. DoorScan answers the narrower question of what looks like a
// door; this answers what is actually there, which is what you want when the mesh disagrees
// with the world and you have no idea why.
public static class LayoutQuery
{
    public sealed record LayoutObject(Vector3 Pos, float Distance, string Type, string? Asset,
        string? DoorState, uint InstanceId, string Layer);

    public static List<LayoutObject> Near(string sqpackDir, string bgPath, Vector3 point, float radius)
    {
        var found = new List<LayoutObject>();
        var game = new Lumina.GameData(sqpackDir);
        var basePath = "bg/" + bgPath[..(bgPath.IndexOf("/level/", StringComparison.Ordinal) + 1)];

        foreach (var lgbName in new[] { "bg.lgb", "planmap.lgb", "planevent.lgb", "planlive.lgb" })
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
                    var distance = Vector3.Distance(pos, point);
                    if (distance > radius)
                        continue;

                    string? asset = obj.Object switch
                    {
                        LayerCommon.SharedGroupInstanceObject sg => sg.AssetPath,
                        LayerCommon.BGInstanceObject bg => bg.AssetPath,
                        LayerCommon.CollisionBoxInstanceObject => "(collision box)",
                        _ => null,
                    };
                    string? doorState = obj.Object is LayerCommon.SharedGroupInstanceObject sgd
                        && sgd.InitialDoorState is DoorState.Open or DoorState.Closed
                        ? sgd.InitialDoorState.ToString()
                        : null;

                    found.Add(new LayoutObject(pos, distance, obj.AssetType.ToString(), asset, doorState,
                        obj.InstanceId, $"{lgbName}/{layer.Name}"));
                }
            }
        }
        return [.. found.OrderBy(o => o.Distance)];
    }
}
