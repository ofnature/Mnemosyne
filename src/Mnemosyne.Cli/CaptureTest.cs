using Lumina;
using Mnemosyne.Builder;
using Mnemosyne.Core;
using Mnemosyne.Protocol;
using System.Text.Json;

namespace Mnemosyne.Cli;

// Proves the buildZone path without the game running.
//
// A live capture can only come from the game process, so the risky part — the DTO mapping
// and the wire round-trip — would otherwise go untested until a player walks into a zone and
// something silently comes out wrong. Instead: read a scene offline, push it through the
// exact serialize/deserialize/convert path buildZone takes, and build from the far end. If
// the mesh matches the one built directly from the same scene, the capture path is sound.
public static class CaptureTest
{
    public static int Run(string zoneHint)
    {
        var sqpack = GamePaths.FindSqpackDir();
        if (sqpack == null)
        {
            Console.WriteLine("no game install found");
            return 1;
        }

        var match = ZoneNames.All.FirstOrDefault(z =>
            z.Key.Contains(zoneHint, StringComparison.OrdinalIgnoreCase)
            || z.Value.Name.Contains(zoneHint, StringComparison.OrdinalIgnoreCase));
        if (match.Key == null)
        {
            Console.WriteLine($"no zone matches '{zoneHint}'");
            return 1;
        }
        Console.WriteLine($"zone: {match.Value.Name} ({match.Key})");

        var game = new GameData(sqpack);
        var dto = CapturedScene.CaptureOffline(game, match.Value.Bg, match.Key);
        Console.WriteLine($"offline scene: {dto.BgParts.Length} bg parts, {dto.Colliders.Length} colliders, "
            + $"{dto.MeshPaths.Length} meshes, {dto.AnalyticShapes.Length} analytic shapes, "
            + $"{dto.Terrains.Length} terrains, {dto.ExitRanges.Length} exit ranges");

        // the exact path a capture takes: DTO -> one JSON line -> DTO
        var json = JsonSerializer.Serialize(dto, MnemosynePipe.JsonOptions);
        Console.WriteLine($"wire form: {json.Length / 1024.0:f0} KB on one line");
        var back = JsonSerializer.Deserialize<SceneCaptureDto>(json, MnemosynePipe.JsonOptions)!;

        int fails = 0;
        fails += Compare("territory", dto.TerritoryId, back.TerritoryId);
        fails += Compare("cfc", dto.CfcId, back.CfcId);
        fails += Compare("festival layers", dto.FestivalLayers.Length, back.FestivalLayers.Length);
        fails += Compare("zone SGs", dto.ZoneSGs.Length, back.ZoneSGs.Length);
        fails += Compare("terrains", dto.Terrains.Length, back.Terrains.Length);
        fails += Compare("analytic shapes", dto.AnalyticShapes.Length, back.AnalyticShapes.Length);
        fails += Compare("mesh paths", dto.MeshPaths.Length, back.MeshPaths.Length);
        fails += Compare("bg parts", dto.BgParts.Length, back.BgParts.Length);
        fails += Compare("colliders", dto.Colliders.Length, back.Colliders.Length);
        fails += Compare("exit ranges", dto.ExitRanges.Length, back.ExitRanges.Length);

        // Counts matching proves nothing about placement, so compare every element bit for
        // bit. Exact equality, not a tolerance: a float that drifts in the last place still
        // moves a rasterization sample across a cell boundary, and that is exactly the kind
        // of "identical except for 60 polys" difference that is hardest to explain later.
        int moved = 0;
        for (int i = 0; i < Math.Min(dto.BgParts.Length, back.BgParts.Length); ++i)
        {
            var (a, b) = (dto.BgParts[i], back.BgParts[i]);
            if (a.Key != b.Key || a.Crc != b.Crc || a.MatId != b.MatId || a.MatMask != b.MatMask
                || a.Analytic != b.Analytic || !Exact(a.Transform, b.Transform))
                ++moved;
        }
        fails += Compare("bg parts identical", 0, moved);

        int colliderDiff = 0;
        for (int i = 0; i < Math.Min(dto.Colliders.Length, back.Colliders.Length); ++i)
        {
            var (a, b) = (dto.Colliders[i], back.Colliders[i]);
            if (a.Key != b.Key || a.Crc != b.Crc || a.MatId != b.MatId || a.MatMask != b.MatMask
                || a.Type != b.Type || !Exact(a.Transform, b.Transform))
                ++colliderDiff;
        }
        fails += Compare("colliders identical", 0, colliderDiff);

        int shapeDiff = 0;
        for (int i = 0; i < Math.Min(dto.AnalyticShapes.Length, back.AnalyticShapes.Length); ++i)
        {
            var (a, b) = (dto.AnalyticShapes[i], back.AnalyticShapes[i]);
            if (a.Crc != b.Crc || !Exact(a.Transform, b.Transform)
                || !Exact(a.BbMin, b.BbMin) || !Exact(a.BbMax, b.BbMax))
                ++shapeDiff;
        }
        fails += Compare("analytic shapes identical", 0, shapeDiff);

        var pathDiff = dto.MeshPaths.Length - dto.MeshPaths.Zip(back.MeshPaths)
            .Count(p => p.First.Crc == p.Second.Crc && p.First.Path == p.Second.Path);
        fails += Compare("mesh paths identical", 0, pathDiff);
        fails += Compare("terrains identical", 0, dto.Terrains.Length - dto.Terrains.Zip(back.Terrains).Count(t => t.First == t.Second));

        int exitDiff = 0;
        for (int i = 0; i < Math.Min(dto.ExitRanges.Length, back.ExitRanges.Length); ++i)
        {
            var (a, b) = (dto.ExitRanges[i], back.ExitRanges[i]);
            if (a.Key != b.Key || !Exact(a.Transform, b.Transform))
                ++exitDiff;
        }
        fails += Compare("exit ranges identical", 0, exitDiff);

        // DTO equality only proves JSON is faithful. The conversion back into the builder's
        // own scene type is a separate chance to lose something, so check that too.
        var diffs = ZoneBuilder.CaptureRoundTripDiff(game, match.Value.Bg, match.Key);
        fails += Compare("scene survives DTO -> SceneDefinition", 0, diffs.Count);
        foreach (var d in diffs.Take(8))
            Console.WriteLine($"         {d}");
        if (diffs.Count > 8)
            Console.WriteLine($"         ... and {diffs.Count - 8} more");

        // Decisive A/B: one freshly-read scene, built as-is and after a round-trip, in one
        // process. Anything that survives this is not the conversion's doing.
        var (abDirect, abCaptured) = ZoneBuilder.CaptureBuildAB(game, match.Value.Bg, match.Key);
        fails += Compare("same scene builds the same either way", abDirect, abCaptured);

        if (fails > 0)
        {
            Console.WriteLine($"\ncapture round-trip FAILED ({fails} mismatches)");
            return 1;
        }

        // and the real proof: build both ways and compare. Direct goes first so the captured
        // build is not the one paying whatever first-build-in-process cost exists.
        Console.WriteLine();
        Console.WriteLine("building from game files...");
        var swDirect = System.Diagnostics.Stopwatch.StartNew();
        var direct = ZoneBuilder.BuildAuto(sqpack, match.Value.Bg);
        swDirect.Stop();
        Console.WriteLine("building from the captured scene...");
        var swCaptured = System.Diagnostics.Stopwatch.StartNew();
        var captured = ZoneBuilder.BuildCaptured(sqpack, back, match.Value.Bg);
        swCaptured.Stop();
        // Control: build the same scene a second time by the same route. Without this, any
        // difference looks like the capture path's fault when the builder itself may simply
        // not be deterministic — and those two call for completely different fixes.
        Console.WriteLine("building from game files a second time (determinism control)...");
        var control = ZoneBuilder.BuildAuto(sqpack, match.Value.Bg);

        var (capTiles, capPolys) = Stats(captured);
        var (dirTiles, dirPolys) = Stats(direct);
        var (ctlTiles, ctlPolys) = Stats(control);
        Console.WriteLine();
        Console.WriteLine($"  captured: {capTiles} tiles, {capPolys} polys, volume {(captured.Volume != null ? "yes" : "no")} ({swCaptured.Elapsed.TotalSeconds:f1}s)");
        Console.WriteLine($"  direct:   {dirTiles} tiles, {dirPolys} polys, volume {(direct.Volume != null ? "yes" : "no")} ({swDirect.Elapsed.TotalSeconds:f1}s)");
        Console.WriteLine($"  control:  {ctlTiles} tiles, {ctlPolys} polys (same route as direct)");
        Console.WriteLine();

        fails += Compare("tiles", dirTiles, capTiles);
        var builderDeterministic = ctlPolys == dirPolys;
        Console.WriteLine($"  [{(builderDeterministic ? "PASS" : "FAIL")}] builder is deterministic"
            + $"{new string(' ', 22)}{(builderDeterministic ? "identical across two runs" : $"{dirPolys} then {ctlPolys} from the same input")}");

        if (!builderDeterministic)
        {
            // The capture path cannot be held to a tighter standard than the builder's own
            // run-to-run spread, so measure against that instead of demanding exact equality.
            var noise = Math.Abs(ctlPolys - dirPolys);
            var gap = Math.Abs(capPolys - dirPolys);
            var withinNoise = gap <= Math.Max(noise, dirPolys / 100); // 1% or the observed noise
            Console.WriteLine($"  [{(withinNoise ? "PASS" : "FAIL")}] captured within builder noise"
                + $"{new string(' ', 14)}capture differs by {gap}, builder's own noise {noise}"
                + $" ({100.0 * gap / dirPolys:f2}% vs {100.0 * noise / dirPolys:f2}%)");
            if (!withinNoise)
                ++fails;
        }
        else
        {
            fails += Compare("polys", dirPolys, capPolys);
        }

        Console.WriteLine();
        Console.WriteLine(fails == 0
            ? "capture path verified: a scene survives the wire and builds the same mesh"
            : $"capture path FAILED ({fails} mismatches)");
        return fails == 0 ? 0 : 1;
    }

    private static bool Exact(float[] a, float[] b) => a.Length == b.Length && a.Zip(b).All(p => p.First.Equals(p.Second));

    private static bool Exact(TransformDto a, TransformDto b) =>
        Exact(a.T, b.T) && Exact(a.R, b.R) && Exact(a.S, b.S) && a.Type == b.Type;

    private static int Compare<T>(string what, T expected, T actual)
    {
        var ok = EqualityComparer<T>.Default.Equals(expected, actual);
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what,-40} {(ok ? $"{actual}" : $"{actual} (expected {expected})")}");
        return ok ? 0 : 1;
    }

    private static (int Tiles, int Polys) Stats(global::Navmesh.Navmesh navmesh)
    {
        int tiles = 0, polys = 0;
        for (int i = 0; i < navmesh.Mesh.GetMaxTiles(); ++i)
        {
            var tile = navmesh.Mesh.GetTile(i);
            if (tile?.data?.header == null)
                continue;
            ++tiles;
            polys += tile.data.header.polyCount;
        }
        return (tiles, polys);
    }
}
