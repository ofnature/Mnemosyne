// patched for Mnemosyne: UI (Draw*) methods stripped - Dalamud/ImGui only; settings values verbatim.
using DotRecast.Recast;
using System;

namespace Navmesh;

public class NavmeshSettings
{
    [Flags]
    public enum Filter
    {
        None = 0,
        LowHangingObstacles = 1 << 0,
        LedgeSpans = 1 << 1,
        WalkableLowHeightSpans = 1 << 2,
        Interiors = 1 << 3,
    }

    public float CellSize = 0.25f;
    public float CellHeight = 0.25f;
    public float AgentHeight = 2.0f;
    public float AgentRadius = 0.5f;
    public float AgentMaxClimb = 0.5f;
    public float AgentMaxSlopeDeg = 55f;
    public Filter Filtering = Filter.LowHangingObstacles | Filter.LedgeSpans | Filter.WalkableLowHeightSpans;
    public float RegionMinSize = 8;
    public float RegionMergeSize = 20;
    public RcPartition Partitioning = RcPartition.WATERSHED;
    public float PolyMaxEdgeLen = 12f;
    public float PolyMaxSimplificationError = 1.5f;
    public int PolyMaxVerts = 6;
    public float DetailSampleDist = 6f;
    public float DetailMaxSampleError = 1f;

    public bool GenerateEdgeClimbLinks = false;
    public bool GenerateEdgeJumpLinks = false;
    public float GroundTolerance = 0.3f;
    public float ClimbDownDistance = 0.4f;
    public float ClimbDownMaxHeight = 3.2f;
    public float ClimbDownMinHeight = 1.5f;
    public float EdgeJumpEndDistance = 2f;
    public float EdgeJumpHeight = 1.8f;
    public float EdgeJumpMaxDrop = 500f;
    public float EdgeJumpMinDrop = 1.5f;

    // we assume that bounds are constant -1024 to 1024 along each axis (since that's the quantization range of position in some packets)
    // there is some code that relies on tiling being power-of-2
    // current values mean 128x128x128 L1 tiles -> 16x16x16 L2 tiles -> 2x2x2 voxels
    public int[] NumTiles = [16, 8, 8];


}
