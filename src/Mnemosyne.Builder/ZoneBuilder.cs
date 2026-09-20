using Lumina;
using Lumina.Data;
using Navmesh;

namespace Mnemosyne.Builder;

// Builds a zone's navmesh from game files, entirely outside the game process.
public static class ZoneBuilder
{
    // convenience overload so hosts don't need Lumina types
    public static global::Navmesh.Navmesh Build(string sqpackDir, string bgPath, bool flyable, Action<int, int>? progress = null) =>
        Build(new GameData(sqpackDir), bgPath, 0, flyable, progress);

    // fully automatic: flyability resolved from the TerritoryType sheet (same rule as vnavmesh)
    public static global::Navmesh.Navmesh BuildAuto(string sqpackDir, string bgPath, Action<int, int>? progress = null)
    {
        var game = new GameData(sqpackDir);
        return Build(game, bgPath, 0, IsFlyable(game, bgPath), progress);
    }

    public static bool IsFlyable(GameData game, string bgPath)
    {
        try
        {
            foreach (var row in game.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>()!)
                if (row.Bg.ToString() == bgPath)
                    return row.TerritoryIntendedUse.RowId is 1 or 49 or 47; // outdoor, island, Diadem
        }
        catch
        {
            // sheet unavailable - assume not flyable
        }
        return false;
    }

    /// <summary>Build from Ariadne's live capture instead of the offline LGB reconstruction.
    /// <paramref name="bgPath"/> is used only to resolve flyability (the capture doesn't carry
    /// it); when unknown, flight falls back to the TerritoryType lookup by territory id.</summary>
    public static global::Navmesh.Navmesh BuildCaptured(string sqpackDir, Mnemosyne.Protocol.SceneCaptureDto dto,
        string? bgPath, Action<int, int>? progress = null)
    {
        var game = new GameData(sqpackDir);
        var flyable = bgPath is { Length: > 0 } ? IsFlyable(game, bgPath) : IsFlyable(game, dto.TerritoryId);
        var scene = CapturedScene.ToSceneDefinition(dto);
        if (scene.TerritoryID == 0 && bgPath is { Length: > 0 })
            scene.TerritoryID = TerritoryIdFor(game, bgPath);
        return BuildScene(game, scene, flyable, progress);
    }

    /// <summary>Round-trip a zone's offline scene through the capture path and report what
    /// the conversion changed. Empty means the path is lossless for this zone.</summary>
    public static List<string> CaptureRoundTripDiff(GameData game, string bgPath, string cacheKey)
        => CapturedScene.RoundTripDiff(LgbSceneReader.Read(game, bgPath, 0), cacheKey);

    /// <summary>Build the same freshly-read scene twice — once as-is, once after a capture
    /// round-trip — inside one process. Isolates "the conversion changed the scene" from
    /// "something else about the two call paths differs".</summary>
    public static (int Direct, int Captured) CaptureBuildAB(GameData game, string bgPath, string cacheKey)
    {
        var flyable = IsFlyable(game, bgPath);
        var scene = LgbSceneReader.Read(game, bgPath, 0);
        var roundTripped = CapturedScene.ToSceneDefinition(CapturedScene.ToDto(scene, cacheKey));
        return (PolyCount(BuildScene(game, scene, flyable, null)),
            PolyCount(BuildScene(game, roundTripped, flyable, null)));
    }

    private static int PolyCount(global::Navmesh.Navmesh navmesh)
    {
        int polys = 0;
        for (int i = 0; i < navmesh.Mesh.GetMaxTiles(); ++i)
            if (navmesh.Mesh.GetTile(i)?.data?.header is { } header)
                polys += header.polyCount;
        return polys;
    }

    /// <summary>The territory a bg path belongs to. Customizations are keyed by territory id,
    /// so without this every zone builds with the default settings - which for North Horn means
    /// watershed partitioning that vnavmesh explicitly switched away from, and no island links.
    /// A bg can back several territories (duplicate instances); prefer one that has a
    /// customization, since that is the whole reason we are looking.</summary>
    public static uint TerritoryIdFor(GameData game, string bgPath)
    {
        try
        {
            uint first = 0;
            foreach (var row in game.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>()!)
            {
                if (row.Bg.ToString() != bgPath)
                    continue;
                if (NavmeshCustomizationRegistry.PerTerritory.ContainsKey(row.RowId))
                    return row.RowId;
                first = first == 0 ? row.RowId : first;
            }
            return first;
        }
        catch
        {
            return 0;
        }
    }

    public static bool IsFlyable(GameData game, uint territoryId)
    {
        try
        {
            var row = game.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>()?.GetRowOrDefault(territoryId);
            return row?.TerritoryIntendedUse.RowId is 1 or 49 or 47;
        }
        catch
        {
            return false;
        }
    }

    public static global::Navmesh.Navmesh Build(GameData game, string bgPath, uint territoryId, bool flyable, Action<int, int>? progress = null)
    {
        if (territoryId == 0)
            territoryId = TerritoryIdFor(game, bgPath);
        return BuildScene(game, LgbSceneReader.Read(game, bgPath, territoryId), flyable, progress);
    }

    // Shared by both entry points. The offline path reconstructs the scene from LGB files;
    // buildZone hands us the game's own live layout instead. Everything downstream - reading
    // collision out of sqpack, rasterizing, tiling - is identical either way.
    private static global::Navmesh.Navmesh BuildScene(GameData game, SceneDefinition scene, bool flyable, Action<int, int>? progress)
    {
        SceneExtractor.FileReader = path =>
        {
            try
            {
                return game.GetFile<FileResource>(path)?.Data;
            }
            catch
            {
                return null;
            }
        };
        NavmeshCustomization.FlyingSupportedResolver = _ => flyable;

        Console.WriteLine($"scene: {scene.BgParts.Count} bg parts, {scene.Colliders.Count} colliders, {scene.MeshPaths.Count} unique collision meshes, {scene.ExitRanges.Count} exit ranges");
        foreach (var terr in scene.Terrains)
            Console.WriteLine($"terrain '{terr}/list.pcb': {(SceneExtractor.FileReader(terr + "/list.pcb") is { Length: > 0 } d ? $"{d.Length} bytes" : "MISSING")}");
        int missingMeshes = scene.MeshPaths.Values.Count(p => SceneExtractor.FileReader(p) is not { Length: > 0 });
        if (missingMeshes > 0)
            Console.WriteLine($"WARNING: {missingMeshes}/{scene.MeshPaths.Count} collision mesh files missing");

        // vnavmesh keeps a per-territory customization for zones its defaults get wrong:
        // different partitioning, hand-authored off-mesh links, added or removed colliders.
        // Building without them produces a mesh that is not merely different but worse -
        // North Horn's default watershed partitioning throws "Bad triangulation" on the
        // spiral staircase vnavmesh wrote the customization to avoid.
        var customization = NavmeshCustomizationRegistry.ForTerritory(scene.TerritoryID);
        if (customization != NavmeshCustomizationRegistry.Default)
            Console.WriteLine($"using {customization.GetType().Name} (territory {scene.TerritoryID}, v{customization.Version})");

        var builder = new NavmeshBuilder(scene, customization);
        int done = 0, total = builder.NumTilesX * builder.NumTilesZ;
        builder.BuildTiles(() => progress?.Invoke(Interlocked.Increment(ref done), total));

        // The mesh pass is the manager's job in vnavmesh, not the builder's - it is where the
        // hand-authored links get stitched in, so skipping it silently drops them.
        customization.CustomizeMesh(builder.Navmesh, [.. scene.FestivalLayers]);
        return builder.Navmesh;
    }
}
