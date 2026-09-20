using Lumina;
using Lumina.Data.Files;
using Lumina.Data.Parsing.Layer;
using Mnemosyne.Core;
using System.Numerics;

namespace Mnemosyne.Cli;

// "Does this thing have an id?" - every event object in a zone with its EObj row, name and
// shared-group asset, plus any shared group or model whose path matches a filter. Interactive
// props (lifts, levers, doors you click) are EventObjects: the BaseId is the EObj sheet row the
// game and plugins key off, and the instance id is this particular placement.
//
// usage: Mnemosyne.Cli layout <zone-name-or-bg-substring> [asset-filter] [--near x,y,z,radius]
public static class LayoutList
{
    public static int Run(string[] args)
    {
        if (args.Length > 1 && args[1] == "--scan-dungeons")
            return ScanDungeons(args.Length > 2 ? args[2] : "way");
        if (args.Length < 2)
        {
            Console.WriteLine("usage: layout <zone-name-or-bg-substring> [asset-filter]");
            return 1;
        }
        var zone = ZoneNames.All.FirstOrDefault(z =>
            z.Key.Contains(args[1], StringComparison.OrdinalIgnoreCase)
            || (z.Value.Name?.Contains(args[1], StringComparison.OrdinalIgnoreCase) ?? false));
        if (zone.Value?.Bg is not { Length: > 0 } bg || GamePaths.FindSqpackDir() is not { } sqpack)
        {
            Console.WriteLine($"no zone or game install for '{args[1]}'");
            return 1;
        }
        var nearAt = Array.IndexOf(args, "--near");
        float[]? near = nearAt >= 0 && nearAt + 1 < args.Length ? args[nearAt + 1].Split(',').Select(float.Parse).ToArray() : null;
        var filter = args.Length > 2 && args[2] != "--near" ? args[2] : null;
        Console.WriteLine($"zone: {zone.Value.Name} ({bg})");

        var game = new GameData(sqpack);
        var eobj = game.GetExcelSheet<Lumina.Excel.Sheets.EObj>();
        var names = game.GetExcelSheet<Lumina.Excel.Sheets.EObjName>();
        var basePath = "bg/" + bg[..(bg.IndexOf("/level/", StringComparison.Ordinal) + 1)];

        foreach (var lgbName in new[] { "bg.lgb", "planmap.lgb", "planevent.lgb", "planlive.lgb" })
        {
            LgbFile? lgb;
            try { lgb = game.GetFile<LgbFile>(basePath + "level/" + lgbName); }
            catch { continue; }
            if (lgb == null)
                continue;

            foreach (var layer in lgb.Layers)
                foreach (var obj in layer.InstanceObjects)
                {
                    var t = obj.Transform.Translation;
                    var pos = new Vector3(t.X, t.Y, t.Z);
                    // --near lists everything in range regardless of type, since an unfamiliar
                    // prop is exactly the thing whose asset name you cannot guess a filter for
                    if (near != null)
                    {
                        var d = Vector3.Distance(pos, new Vector3(near[0], near[1], near[2]));
                        if (d > near[3])
                            continue;
                        if (obj.Object is not LayerCommon.EventInstanceObject)
                        {
                            var asset = obj.Object switch
                            {
                                LayerCommon.SharedGroupInstanceObject g => g.AssetPath,
                                LayerCommon.BGInstanceObject b => b.AssetPath,
                                _ => "",
                            };
                            if (filter == null || asset.Contains(filter, StringComparison.OrdinalIgnoreCase))
                                Console.WriteLine($"  {obj.AssetType,-12} {Fmt(pos)}  {d,5:f1}m  instance {obj.InstanceId,-9} {asset}  [{lgbName}/{layer.Name}]");
                            Detail(obj);
                            continue;
                        }
                    }
                    switch (obj.Object)
                    {
                        case LayerCommon.EventInstanceObject ev:
                        {
                            var id = ev.ParentData.BaseId;
                            var row = eobj?.GetRowOrDefault(id);
                            var name = names?.GetRowOrDefault(id)?.Singular.ExtractText();
                            var sgb = row?.SgbPath.ValueNullable?.SgbPath.ExtractText();
                            if (filter != null && !(sgb ?? "").Contains(filter, StringComparison.OrdinalIgnoreCase)
                                && !(name ?? "").Contains(filter, StringComparison.OrdinalIgnoreCase))
                                continue;
                            Console.WriteLine($"  EventObject  {Fmt(pos)}  EObj {id,-8} data {row?.Data.RowId,-8} instance {obj.InstanceId,-9} "
                                + $"\"{name}\"  {sgb}  [{lgbName}/{layer.Name}]");
                            break;
                        }
                        case LayerCommon.SharedGroupInstanceObject sg when filter != null
                            && sg.AssetPath.Contains(filter, StringComparison.OrdinalIgnoreCase):
                            Console.WriteLine($"  SharedGroup  {Fmt(pos)}  instance {obj.InstanceId,-9} {sg.AssetPath}  [{lgbName}/{layer.Name}]");
                            break;
                        case LayerCommon.BGInstanceObject m when filter != null
                            && m.AssetPath.Contains(filter, StringComparison.OrdinalIgnoreCase):
                            Console.WriteLine($"  BG model     {Fmt(pos)}  instance {obj.InstanceId,-9} {m.AssetPath}  [{lgbName}/{layer.Name}]");
                            break;
                    }
                }
        }
        return 0;
    }

    /// <summary>Which dungeons carry layers whose name matches a pattern, and how many objects
    /// those layers hold. Built for one question: how many duties ship the designers' own
    /// route markers (LVD_ID_way_Basedata in Mistwake), which a duty solver could follow
    /// instead of discovering the route.</summary>
    private static int ScanDungeons(string pattern)
    {
        if (GamePaths.FindSqpackDir() is not { } sqpack)
            return 1;
        var game = new GameData(sqpack);
        var territories = game.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>()!;
        var dungeons = ZoneNames.All.Where(z => z.Value.Bg?.Contains("/dun/") == true)
            .GroupBy(z => z.Value.Bg).Select(g => g.First()).OrderBy(z => z.Value.Bg).ToList();
        int with = 0;
        foreach (var (_, zone) in dungeons)
        {
            var basePath = "bg/" + zone.Bg![..(zone.Bg!.IndexOf("/level/", StringComparison.Ordinal) + 1)];
            var hits = new List<string>();
            var layerNames = new List<string>();
            // "eobj:2000700,2000139" matches event objects by EObj id instead of layer names:
            // generic props (shortcut, exit, arena walls) are shared across every duty
            var eobjIds = pattern.StartsWith("eobj:") ? pattern[5..].Split(',').Select(uint.Parse).ToHashSet() : null;
            foreach (var lgbName in new[] { "planmap.lgb", "planevent.lgb" })
            {
                LgbFile? lgb;
                try { lgb = game.GetFile<LgbFile>(basePath + "level/" + lgbName); }
                catch { continue; }
                if (lgb == null)
                    continue;
                foreach (var layer in lgb.Layers)
                {
                    layerNames.Add(layer.Name);
                    if (pattern.StartsWith("type:"))
                    {
                        foreach (var o in layer.InstanceObjects)
                            if (o.AssetType.ToString().Equals(pattern[5..], StringComparison.OrdinalIgnoreCase))
                                hits.Add(pattern[5..]);
                    }
                    else if (eobjIds != null)
                    {
                        foreach (var o in layer.InstanceObjects)
                            if (o.Object is LayerCommon.EventInstanceObject e && eobjIds.Contains(e.ParentData.BaseId))
                                hits.Add(e.ParentData.BaseId.ToString());
                    }
                    else if (layer.Name.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                        hits.Add($"{layer.Name}({layer.InstanceObjects.Length})");
                }
            }
            if (hits.Count > 0)
                ++with;
            var expansion = zone.Bg!.Split('/')[0];
            Console.WriteLine($"  {(hits.Count > 0 ? "YES" : " - ")}  {expansion,-6} {zone.Name,-38} {string.Join(" ", hits.GroupBy(h => h).Select(g => g.Count() > 1 ? $"{g.Key}x{g.Count()}" : g.Key))}");
        }
        Console.WriteLine();
        Console.WriteLine($"{with} of {dungeons.Count} dungeons have a layer matching '{pattern}'");
        return 0;
    }

    /// <summary>The shape of the things a gimmick is made of: a trigger volume's size, and a
    /// client path's control points in world space - for a ride, those are the route and the
    /// landing spot.</summary>
    private static void Detail(LayerCommon.InstanceObject obj)
    {
        var tr = obj.Transform;
        var world = Matrix4x4.CreateScale(tr.Scale.X, tr.Scale.Y, tr.Scale.Z)
            * Matrix4x4.CreateFromYawPitchRoll(tr.Rotation.Y, tr.Rotation.X, tr.Rotation.Z)
            * Matrix4x4.CreateTranslation(tr.Translation.X, tr.Translation.Y, tr.Translation.Z);
        switch (obj.Object)
        {
            case LayerCommon.EventRangeInstanceObject er:
                Console.WriteLine($"               trigger {er.ParentData.TriggerBoxShape}, scale ({tr.Scale.X:f1}, {tr.Scale.Y:f1}, {tr.Scale.Z:f1}), "
                    + $"rotation y {tr.Rotation.Y * 180 / MathF.PI:f0} deg");
                break;
            case LayerCommon.ClientPathInstanceObject cp:
                var points = cp.ParentData.ControlPointsArray ?? [];
                Console.WriteLine($"               {points.Length} control points (ring={cp.Ring}), rotation y {tr.Rotation.Y * 180 / MathF.PI:f0} deg, scale {tr.Scale.X:f1}:");
                var prev = (Vector3?)null;
                float length = 0;
                foreach (var c in points)
                {
                    var local = new Vector3(c.Translation.X, c.Translation.Y, c.Translation.Z);
                    var w = Vector3.Transform(local, world);
                    if (prev is { } pv)
                        length += Vector3.Distance(pv, w);
                    prev = w;
                    Console.WriteLine($"                 #{c.PointId,-3} world {Fmt(w)}   local {Fmt(local)}");
                }
                Console.WriteLine($"               path length through control points: {length:f1} y");
                break;
        }
    }

    private static string Fmt(Vector3 v) => $"({v.X,7:f1}, {v.Y,7:f1}, {v.Z,7:f1})";
}
