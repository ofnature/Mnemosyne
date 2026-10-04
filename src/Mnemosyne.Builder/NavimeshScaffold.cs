using Lumina;
using Lumina.Data.Files;
using Lumina.Data.Parsing.Layer;
using Navmesh;
using System.Numerics;

namespace Mnemosyne.Builder;

// Collision the designers placed for the game's own navigation, not for players.
//
// Some zones carry a layer named like "LVD_Navimesh" whose collision boxes shape the game's own
// navmesh. Characters do not collide with them, but the builder rasterized them as solid, so
// they became walkable floor nobody can stand on. The case that proved it is The Ghimlyt Dark's
// drop into the last arena: a 50 x 30 board at 30 degrees over the arena became a 5,000 m2
// island, and a character's reported crossing (2026-09-30) leaves from on top of the board and
// lands underneath its high end, passing about 14 y below the surface. Across 111 dungeons,
// 54 boxes in 10 duties sit in such layers.
//
// Only collision boxes placed directly in those layers are dropped. Shared groups in them (The
// Ghimlyt Dark has "sgpl_w_lvd_collison_only") are left alone: nothing has shown yet what they
// are for.
//
// A live capture carries no layer names, so the boxes are found in the zone's layout files and
// matched to the scene's colliders by position and scale. The offline reader goes through the
// same match, so both kinds of build drop the same colliders.
public static class NavimeshScaffold
{
    private const float Match = 0.05f;

    /// <summary>Remove scaffold colliders from the scene. Returns how many were removed.</summary>
    public static int Remove(GameData game, SceneDefinition scene, string bgPath, IReadOnlyCollection<string>? alsoLayers = null)
    {
        var boxes = Find(game, bgPath, alsoLayers);
        if (boxes.Count == 0)
            return 0;
        return scene.Colliders.RemoveAll(c => boxes.Any(b =>
            Vector3.Distance(b.Pos, c.transform.Translation) <= Match
            && Vector3.Distance(b.Scale, c.transform.Scale) <= Match));
    }

    /// <summary>Position and scale of every collision box placed directly in a layer whose name
    /// contains "navimesh".</summary>
    public static List<(Vector3 Pos, Vector3 Scale)> Find(GameData game, string bgPath, IReadOnlyCollection<string>? alsoLayers = null)
    {
        var found = new List<(Vector3, Vector3)>();
        var basePath = "bg/" + bgPath[..(bgPath.IndexOf("/level/", StringComparison.Ordinal) + 1)];
        foreach (var lgbName in new[] { "bg.lgb", "planmap.lgb", "planevent.lgb", "planlive.lgb" })
        {
            LgbFile? lgb;
            try { lgb = game.GetFile<LgbFile>(basePath + "level/" + lgbName); }
            catch { continue; }
            if (lgb == null)
                continue;
            foreach (var layer in lgb.Layers)
            {
                if (!layer.Name.Contains("navimesh", StringComparison.OrdinalIgnoreCase)
                    && !(alsoLayers?.Contains(layer.Name, StringComparer.OrdinalIgnoreCase) ?? false))
                    continue;
                foreach (var obj in layer.InstanceObjects)
                {
                    if (obj.Object is not LayerCommon.CollisionBoxInstanceObject)
                        continue;
                    var t = obj.Transform;
                    found.Add((new Vector3(t.Translation.X, t.Translation.Y, t.Translation.Z), new Vector3(t.Scale.X, t.Scale.Y, t.Scale.Z)));
                }
            }
        }
        return found;
    }
}
