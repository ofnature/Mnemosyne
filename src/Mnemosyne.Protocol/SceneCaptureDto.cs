using System.Collections.Generic;

namespace Mnemosyne.Protocol;

// The wire form of Ariadne's live scene capture (buildZone). Mirrors
// D:\Dev\Ariadne\Ariadne\Zone\SceneCapture.cs field for field — that file is the
// authoritative DTO and its SceneCaptureDtoTests lock the wire shape, so change it there
// first. Both ends are C#, so ulong keys travel as JSON numbers.
//
// Only the game process can see which festival layers and shared-group states are live, so
// offline building can only approximate the zone the player is actually standing in. This
// is how the exact variant gets out.

public sealed class TransformDto
{
    public float[] T { get; set; } = []; // translation xyz
    public float[] R { get; set; } = []; // rotation quaternion xyzw
    public float[] S { get; set; } = []; // scale xyz

    /// <summary>Analytic collider kind (0 box, 1 sphere, 2 cylinder, 3 plane), carried on
    /// analytic-shape transforms only. The extractor switches on this to decide what to
    /// rasterize — drop it and every sphere and cylinder in the zone becomes a box.</summary>
    public int Type { get; set; }
}

public sealed class AnalyticShapeDto
{
    public uint Crc { get; set; }
    public TransformDto Transform { get; set; } = new();
    public float[] BbMin { get; set; } = [];
    public float[] BbMax { get; set; } = [];
}

public sealed class MeshPathDto
{
    public uint Crc { get; set; }
    public string Path { get; set; } = "";
}

public sealed class BgPartDto
{
    public ulong Key { get; set; }
    public TransformDto Transform { get; set; } = new();
    public uint Crc { get; set; }
    public ulong MatId { get; set; }
    public ulong MatMask { get; set; }
    public bool Analytic { get; set; }
}

public sealed class ColliderDto
{
    public ulong Key { get; set; }
    public TransformDto Transform { get; set; } = new();
    public uint Crc { get; set; }
    public ulong MatId { get; set; }
    public ulong MatMask { get; set; }
    public int Type { get; set; } // FFXIVClientStructs ColliderType
}

public sealed class ExitRangeDto
{
    public ulong Key { get; set; }
    public TransformDto Transform { get; set; } = new();
}

public sealed class SceneCaptureDto
{
    public string CacheKey { get; set; } = "";
    public uint TerritoryId { get; set; }
    public uint CfcId { get; set; }
    public uint[] FestivalLayers { get; set; } = [];
    public uint[] ZoneSGs { get; set; } = [];
    public string[] Terrains { get; set; } = [];
    public AnalyticShapeDto[] AnalyticShapes { get; set; } = [];
    public MeshPathDto[] MeshPaths { get; set; } = [];
    public BgPartDto[] BgParts { get; set; } = [];
    public ColliderDto[] Colliders { get; set; } = [];
    public ExitRangeDto[] ExitRanges { get; set; } = [];

    public int InstanceCount => BgParts.Length + Colliders.Length;
}
