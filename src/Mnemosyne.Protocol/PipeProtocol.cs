using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mnemosyne.Protocol;

// Wire types for the Mnemosyne named-pipe protocol.
// Spec: D:\Dev\Ariadne\docs\mnemosyne-protocol.md — the source of truth shared with
// Ariadne; change that file first. Envelope: newline-delimited camelCase JSON with a
// client-chosen `id` echoed on every response.

public static class MnemosynePipe
{
    public const string PipeName = "mnemosyne";
    public const int ProtocolVersion = 1;

    public static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

public sealed class Request
{
    public int Id { get; set; }
    public string Op { get; set; } = ""; // hello | listZones | zoneStatus | getMesh | findPath | notifyMeshBuilt
                                         // | updateGameState | getGameState | nearestPoint | isPointOnMesh
                                         // | pointOnFloor | buildBitmap | buildZone | reportTraversal
    public string? CacheKey { get; set; }
    public string? Path { get; set; }    // notifyMeshBuilt
    public float[]? From { get; set; }   // findPath: [x, y, z]
    public float[]? To { get; set; }
    public bool? Fly { get; set; }
    public uint? TerritoryId { get; set; } // updateGameState
    public float[]? Pos { get; set; }      // updateGameState: [x, y, z]
    public float? Rotation { get; set; }   // updateGameState: yaw radians
    public bool? Flying { get; set; }      // updateGameState
    public float? Speed { get; set; }      // updateGameState: exact y/s if the client knows it
    public string? Character { get; set; } // updateGameState: display name (fleet)
    public ulong? ContentId { get; set; }  // updateGameState: stable identity across reconnects

    // findPath variants (vnavmesh Nav.PathfindWithTolerance / PathfindAvoid)
    public float? Tolerance { get; set; }
    /// <summary>Yalms of breathing room to keep from obstacles on walk routes. Omitted =
    /// the server default (1 y); 0 disables padding and gives you Recast's raw wall-hugging
    /// route. Endpoints are never moved.</summary>
    public float? Clearance { get; set; }
    public float[]? AvoidCenter { get; set; }
    public float? AvoidRadius { get; set; }

    // Query.Mesh.* ops
    public float[]? Point { get; set; }
    public float? HalfExtentXZ { get; set; }
    public float? HalfExtentY { get; set; }
    public bool? ReachableOnly { get; set; }
    public bool? AllowUnreachable { get; set; }

    // buildZone: Ariadne's live capture of the exact zone variant. A dense zone is a
    // multi-megabyte single line - the reader must not cap line length.
    public SceneCaptureDto? Scene { get; set; }

    // reportTraversal: execution feedback into the mesh
    public string? Mode { get; set; }    // "walk" | "fly" | "direct"
    public bool? Success { get; set; }
    public string? Note { get; set; }

    // buildBitmap
    public float[][]? StartingPoints { get; set; }
    public string? Filename { get; set; }
    public float? PixelSize { get; set; }
    public float[]? MinBounds { get; set; }
    public float[]? MaxBounds { get; set; }

    // reachableCells: the window around `from` (spec: docs/mnemosyne-protocol.md). Radius and
    // cellSize default to 120 / 2; minY/maxY absent means no height band.
    public float? Radius { get; set; }
    public float? CellSize { get; set; }
    public float? MinY { get; set; }
    public float? MaxY { get; set; }
}

public class Response
{
    public int Id { get; set; }
    public bool Ok { get; set; }
    public string? Error { get; set; }
    /// <summary>Why this answer looks the way it does, when ok/error alone would leave the
    /// consumer guessing. Every `ok:false` carries one; `ok:true` carries one only when the
    /// success is qualified. Clients must tolerate unknown values.</summary>
    public string? Result { get; set; }
}

public sealed class HelloResponse : Response
{
    public int Protocol { get; set; }
    public string? App { get; set; }
    public string? Version { get; set; }
    public int MeshVersion { get; set; }
}

public sealed class ZoneDto
{
    public string CacheKey { get; set; } = "";
    public int Version { get; set; }
    public int Customization { get; set; }
    public long Size { get; set; }
    public string? Mtime { get; set; } // ISO-8601 UTC
}

public sealed class ListZonesResponse : Response
{
    public List<ZoneDto>? Zones { get; set; }
}

public sealed class ZoneStatusResponse : Response
{
    public string? Status { get; set; } // "cached" | "missing" | "stale"
    public int Version { get; set; }
    public int Customization { get; set; }
    public float Progress { get; set; } = -1;  // 0..1 while building, -1 idle (vnavmesh's value)
    public bool Building { get; set; }
    public bool PathfindInProgress { get; set; } // this client's queries only
    public int PathfindNumQueued { get; set; }
}

public sealed class GetMeshResponse : Response
{
    public string? Path { get; set; }
    public int Version { get; set; }
    public int Customization { get; set; }
    public long Size { get; set; }
}

public sealed class FindPathResponse : Response
{
    public float[][]? Waypoints { get; set; }
    public bool Partial { get; set; } // path stops short of `to`: continue by re-querying from the last waypoint
    /// <summary>Closest usable point, accompanying result "targetOffMesh" (nearest to `to`)
    /// or "startOffMesh" (nearest to `from`). Absent otherwise.</summary>
    public float[]? Nearest { get; set; }
    /// <summary>Present when the route changes mode partway - currently a fly route that has
    /// to land and walk the last stretch, which is what a doorway forces.</summary>
    public List<LegDto>? Legs { get; set; }
    public double? EtaSeconds { get; set; } // length / calibrated mode speed; absent until calibrated
}

/// <summary>One span of a multi-modal route. Legs index into the flat `waypoints` array, so
/// a consumer that ignores them still gets a followable path - it just follows it mode-naive.
/// `enter` is the transition to perform before this leg's waypoints.</summary>
public sealed class LegDto
{
    public string Mode { get; set; } = "walk";   // walk | fly
    public string? Enter { get; set; }           // mount | jumpOff | land | dismount | teleport
    public uint? EnterArg { get; set; }          // aetheryte id for teleport
    public int First { get; set; }               // index into waypoints
    public int Count { get; set; }
}

public sealed class PointResponse : Response
{
    public bool Found { get; set; }
    public float[]? Point { get; set; }
}

public sealed class OnMeshResponse : Response
{
    public bool OnMesh { get; set; }
}

public sealed class BitmapResponse : Response
{
    public string? Path { get; set; }
}

/// <summary>reachableCells: a world-aligned grid of walkable surfaces with reachability from
/// `from` (spec: docs/mnemosyne-protocol.md). `columns`, `heights` and `states` are parallel —
/// one entry per <i>surface</i>, since a column can hold several stacked floors — and a column
/// index is `zi * width + xi`, ascending.</summary>
public sealed class ReachableCellsResponse : Response
{
    public float[]? Start { get; set; }    // `from` snapped onto the mesh: where the flood began
    public float[]? Origin { get; set; }   // world X/Z of the grid's minimum corner
    public float CellSize { get; set; }
    public int Width { get; set; }
    public int Depth { get; set; }
    public int[]? Columns { get; set; }
    public float[]? Heights { get; set; }  // Y at the cell centre, 0.1 y precision
    public int[]? States { get; set; }     // 1 reachable · 2 cutOff
    /// <summary>The flood reached walkable mesh beyond this window, so an exhausted grid is
    /// not the same thing as an exhausted zone.</summary>
    public bool ReachableOutside { get; set; }
    public float[]? Nearest { get; set; }  // accompanying startOffMesh, like findPath
    public ReachableCellsStatsDto? Stats { get; set; }
}

public sealed class ReachableCellsStatsDto
{
    public int ReachablePolys { get; set; } // zone-wide: the component you are standing in
    public int WalkablePolys { get; set; }  // zone-wide: everything a walker could stand on
}

public sealed class PlayerDto
{
    public int ClientId { get; set; }
    public string? Character { get; set; }
    public ulong ContentId { get; set; }
    public string? CacheKey { get; set; }
    public uint TerritoryId { get; set; }
    public float[]? Pos { get; set; }
    public float Rotation { get; set; }
    public bool Flying { get; set; }
    public float Speed { get; set; }
    public double AgeMs { get; set; }
}

public sealed class SpeedsDto
{
    public float? Ground { get; set; } // sustained maxima in y/s, learned from observation
    public float? Fly { get; set; }
}

public sealed class GameStateResponse : Response
{
    public bool Present { get; set; }      // false: nothing pushed yet, or stale (> 5 s)
    public string? CacheKey { get; set; }
    public uint TerritoryId { get; set; }
    public float[]? Pos { get; set; }
    public float Rotation { get; set; }
    public bool Flying { get; set; }
    public double AgeMs { get; set; }
    public float Speed { get; set; }       // current movement speed, y/s
    public SpeedsDto? Speeds { get; set; } // calibrated per-mode maxima
    /// <summary>Every live client (the user runs several game clients); the flat fields
    /// above mirror the most recently updated one for legacy consumers.</summary>
    public List<PlayerDto>? Players { get; set; }
}
