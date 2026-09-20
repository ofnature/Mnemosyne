using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Layer;
using System.Collections.Generic;
using System.Numerics;

namespace Navmesh;

// compact scene definition as extracted from the game's layout
// the goal is to fill it quickly on main thread, and then extract real data in background thread
public class SceneDefinition
{
    public uint TerritoryID;
    public uint CFCID;
    public SortedSet<uint> FestivalLayers = new();
    public List<uint> ZoneSGs = new();
    public List<string> Terrains = new();
    public Dictionary<uint, (Transform transform, Vector3 bbMin, Vector3 bbMax)> AnalyticShapes = new(); // key = crc; used by bgparts
    public Dictionary<uint, string> MeshPaths = new(); // key = crc, value = pcb path; used by all colliders
    public List<(ulong key, Transform transform, uint crc, ulong matId, ulong matMask, bool analytic)> BgParts = new();
    public List<(ulong key, Transform transform, uint crc, ulong matId, ulong matMask, ColliderType type)> Colliders = new();
    public List<(ulong key, Transform transform)> ExitRanges = new();

    // patched for Mnemosyne: FillFromActiveLayout/FillFromLayout stripped (game-memory only);
    // LgbSceneReader fills these fields offline from LGB + terrain files instead.
}
