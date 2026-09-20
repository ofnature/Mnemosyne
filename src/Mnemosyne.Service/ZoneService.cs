using DotRecast.Detour;
using Mnemosyne.Core;
using Mnemosyne.Protocol;
using Navmesh;
using Navmesh.NavVolume;
using System.Numerics;

namespace Mnemosyne.Service;

// Implements the ops from docs/mnemosyne-protocol.md against vnavmesh's mesh cache.
// Zones are loaded on demand and kept warm in a small LRU.
public sealed partial class ZoneService
{
    public const string AppName = "mnemosyne";
    public const string AppVersion = "0.1.0";

    private sealed class LoadedZone
    {
        public required string Key;
        public required DtNavMesh Mesh;
        public required MeshPathfinder Pathfinder;
        public required ZoneOverrides Overrides;
        public required bool HasVolume;
        public required string FastPath;
        public VoxelMap? Volume; // loaded on demand, in the background: never inside a request
        public Task? VolumeLoad; // the in-flight load; a request that finds it running gets meshNotReady
        public VoxelPathfind? VolumeQuery;
        public FlightPathfinder? Flight; // coarse octree planner; VolumeQuery is its fallback
        public readonly object Lock = new();
        public long LastUse;

        /// <summary>Write time of the overrides file this copy was built from. A newer file on
        /// disk means this copy is stale for every client sharing it.</summary>
        public DateTime OverridesStamp;

        // reachableCells floods, keyed by start poly: the component walk is the expensive half
        // of that query, and consumers ask repeatedly from the same spot with different windows.
        // Capped, because a component is a big set of poly refs and a sweeping consumer asks
        // from many places. All access is under Lock, and the whole cache dies with the zone —
        // which is what invalidates it when an override edit reloads the mesh.
        private const int MaxCachedFloods = 4;
        private readonly Dictionary<long, HashSet<long>> _reachFloods = [];
        private readonly Queue<long> _reachFloodOrder = [];

        public HashSet<long> FloodFrom(long startRef, Func<HashSet<long>> compute)
        {
            if (_reachFloods.TryGetValue(startRef, out var hit))
                return hit;

            var computed = compute();
            _reachFloods[startRef] = computed;
            _reachFloodOrder.Enqueue(startRef);
            while (_reachFloodOrder.Count > MaxCachedFloods)
                _reachFloods.Remove(_reachFloodOrder.Dequeue());
            return computed;
        }
    }

    // sized for a fleet: the user runs 4 game clients, potentially in 4 different zones,
    // and the viewer browses a fifth
    private const int MaxLoaded = 10;
    // fly search budget: ~125k nodes/s, so this keeps worst-case fly queries ~1.5s.
    // Exhaustion yields a partial path toward the goal; clients re-query from its end.
    private const int FlyStepBudget = 200_000;
    private static readonly DotRecast.Core.Numerics.RcVec3f SnapExtents = new(5, 5, 5);
    private readonly Dictionary<string, LoadedZone> _zones = [];
    private readonly object _lock = new();
    private readonly IDtQueryFilter _filter = new DtQueryDefaultFilter();

    public Response Handle(Request req, int clientId = 0)
    {
        try
        {
            return req.Op switch
            {
                "hello" => new HelloResponse
                {
                    Id = req.Id,
                    Ok = true,
                    Protocol = MnemosynePipe.ProtocolVersion,
                    App = AppName,
                    Version = AppVersion,
                    MeshVersion = (int)global::Navmesh.Navmesh.Version,
                },
                "listZones" => ListZones(req),
                "zoneStatus" => ZoneStatus(req, clientId),
                "getMesh" => GetMesh(req),
                "findPath" => FindPath(req, clientId),
                "notifyMeshBuilt" => NotifyMeshBuilt(req),
                "updateGameState" => UpdateGameState(req, clientId),
                "getGameState" => GetGameState(req),
                "nearestPoint" => NearestPoint(req),
                "isPointOnMesh" => IsPointOnMesh(req),
                "pointOnFloor" => PointOnFloor(req),
                "buildBitmap" => BuildBitmap(req),
                "buildZone" => BuildZone(req),
                "reportTraversal" => ReportTraversal(req),
                "reachableCells" => ReachableCells(req),
                _ => Error(req, $"unknown op '{req.Op}'"),
            };
        }
        catch (Exception ex)
        {
            return Error(req, ex.Message);
        }
    }

    private static Response Error(Request req, string message, string? result = null) =>
        new() { Id = req.Id, Ok = false, Error = message, Result = result ?? Results.Failed };

    /// <summary>The classified `result` vocabulary (protocol doc, "Classified answers").
    /// A consumer acts on these once instead of disambiguating a bare "no" by experiment.</summary>
    public static class Results
    {
        public const string Ok = "ok";
        public const string TargetOffMesh = "targetOffMesh";
        public const string StartOffMesh = "startOffMesh";
        public const string NoRouteOnMesh = "noRouteOnMesh";
        public const string MeshNotReady = "meshNotReady";
        public const string Unreachable = "unreachable";
        public const string AvoidIgnored = "avoidIgnored";
        /// <summary>A fly route could not reach the goal through the volume, so the remainder
        /// is a walk leg appended to it. The waypoints are complete; the character has to be
        /// on the ground for the tail. Common at doorways, which the volume seals and the mesh
        /// does not - and indoors is not flyable anyway.</summary>
        public const string WalkedTail = "walkedTail";
        /// <summary>A fly request answered with a ground route, because walking it is quicker
        /// once the air detour is costed at the calibrated flight speed. The waypoints are a
        /// walk route - stay on the ground.</summary>
        public const string GroundFaster = "groundFaster";
        /// <summary>The search ran out of its step budget before it could decide, so the
        /// waypoints are the best route found toward the goal and `partial` is true. Distinct
        /// from NoRouteOnMesh, which is a *proof* that no route exists: one is retryable from
        /// the last waypoint, the other will not change until the mesh does.</summary>
        public const string BudgetExhausted = "budgetExhausted";
        /// <summary>Bad request or a fault - not a routing outcome. Kept distinct so a
        /// consumer never retries a malformed call as if the mesh were merely cold.</summary>
        public const string Failed = "failed";
    }

    private static Response ListZones(Request req) => new ListZonesResponse
    {
        Id = req.Id,
        Ok = true,
        Zones = [.. MeshCache.Enumerate().Select(e => new ZoneDto
        {
            CacheKey = e.Key,
            Version = (int)e.Version,
            Customization = e.CustomizationVersion,
            Size = new FileInfo(e.Path).Length,
            Mtime = new FileInfo(e.Path).LastWriteTimeUtc.ToString("O"),
        })],
    };

    // cache keys are filename stems built from game data, but never trust them as paths
    private static string? KeyToPath(string? cacheKey)
    {
        if (string.IsNullOrEmpty(cacheKey) || cacheKey.IndexOfAny(['/', '\\', ':']) >= 0 || cacheKey.Contains(".."))
            return null;
        return Path.Combine(MeshCache.DefaultDirectory, cacheKey + ".navmesh");
    }

    // ---- builder fallback: zones missing from vnavmesh's cache get built from game files ----

    public static string BuiltDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mnemosyne", "built");

    /// <summary>Meshes built from a live scene capture, kept apart from offline builds because
    /// they mean something stronger. A capture is the exact variant a player was standing in -
    /// right festival layers, right shared-group states - and has been shown byte-identical to
    /// vnavmesh's own build of the same key. An offline build is a baseline approximation that
    /// skips festival layers entirely, so the two must not compete on equal terms.</summary>
    public static string CapturedDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mnemosyne", "captured");

    private readonly string? _sqpackDir = GamePaths.FindSqpackDir();
    private readonly object _buildLock = new();

    // -1 when idle, 0..1 while a build runs (vnavmesh's Nav.BuildProgress contract)
    private volatile float _buildProgress = -1;
    public float BuildProgress => _buildProgress;

    // Which key is building right now, if any. Read without the build lock on purpose: the
    // whole point is that a request can find out a build is running *without* waiting for
    // it. A cold build takes tens of seconds; blocking meant every client's next request
    // blew its 10 s timeout and dropped the pipe - four at once, with a fleet.
    private volatile string? _buildingKey;

    public bool IsBuilding(string? cacheKey) =>
        _buildingKey is { } key && (cacheKey == null || key == cacheKey);

    // A zone that cannot be built (missing bg, malformed collision, out of memory) would
    // otherwise be re-kicked by every single request forever. Remember the failure and stop
    // trying for a while - long enough not to thrash, short enough that a fixed install or
    // a game patch gets another go without a service restart.
    private readonly Dictionary<string, long> _buildFailures = [];
    private const long BuildRetryCooldownMs = 5 * 60 * 1000;

    /// <summary>What happened when an op asked for a build. Everything except
    /// <see cref="NotBuildable"/> means "come back later", i.e. meshNotReady.</summary>
    public enum BuildKick
    {
        NotBuildable,   // no game files, no bg path, or it failed recently
        Started,        // a build for this key is now running
        InProgress,     // this key was already building
        Busy,           // a different key is building; this one gets its turn on a later poll
    }

    private static string? BuiltPathFor(string? cacheKey) =>
        KeyToPath(cacheKey) is null ? null : Path.Combine(BuiltDirectory, cacheKey + ".navmesh");

    private static string? CapturedPathFor(string? cacheKey) =>
        KeyToPath(cacheKey) is null ? null : Path.Combine(CapturedDirectory, cacheKey + ".navmesh");

    private static bool IsCurrentMeshFile(string path)
    {
        try
        {
            if (!File.Exists(path))
                return false;
            var (magic, version, _) = MeshCache.ReadHeader(path);
            return magic == global::Navmesh.Navmesh.Magic && version == global::Navmesh.Navmesh.Version;
        }
        catch (IOException)
        {
            return false;
        }
    }

    // A capture of this exact key beats everything: it is the variant the player was actually
    // in, and matches vnavmesh's own build byte for byte when both exist. vnavmesh's cache is
    // next - a real build of (probably) the same variant. The offline baseline is last, because
    // it skips festival layers and can be missing whole tiles: measured 31 against 41 in Limsa.
    private string? FindZoneFile(string? cacheKey)
    {
        var captured = CapturedPathFor(cacheKey);
        if (captured != null && IsCurrentMeshFile(captured))
            return captured;
        var vnav = KeyToPath(cacheKey);
        if (vnav != null && IsCurrentMeshFile(vnav))
            return vnav;
        var built = BuiltPathFor(cacheKey);
        return built != null && IsCurrentMeshFile(built) ? built : null;
    }

    /// <summary>Start a build for this key if one is possible and none is running, and
    /// return immediately. Callers answer "meshNotReady" and poll zoneStatus; nothing waits
    /// on a build.</summary>
    private BuildKick KickBuild(string cacheKey, SceneCaptureDto? scene = null)
    {
        if (_sqpackDir == null)
            return BuildKick.NotBuildable;
        if (scene == null && ZoneNames.Lookup(cacheKey)?.Bg is not { Length: > 0 })
            return BuildKick.NotBuildable;
        lock (_buildLock)
        {
            if (_buildingKey != null)
                return _buildingKey == cacheKey ? BuildKick.InProgress : BuildKick.Busy;
            // a capture overrides both the cooldown and the "already built" shortcut: the
            // caller is telling us the file on disk is the wrong variant
            if (scene == null && _buildFailures.TryGetValue(cacheKey, out var failedAt))
            {
                if (Environment.TickCount64 - failedAt < BuildRetryCooldownMs)
                    return BuildKick.NotBuildable;
                _buildFailures.Remove(cacheKey);
            }
            if (scene == null && IsCurrentMeshFile(BuiltPathFor(cacheKey)!))
                return BuildKick.NotBuildable; // finished while we were being asked
            _buildingKey = cacheKey;
            _buildProgress = 0;
        }
        // long-running and CPU-bound: its own thread, never a pool thread
        new Thread(() => RunBuild(cacheKey, scene)) { IsBackground = true, Name = $"build {cacheKey}" }.Start();
        return BuildKick.Started;
    }

    private void RunBuild(string cacheKey, SceneCaptureDto? scene)
    {
        string? built = null;
        try
        {
            built = BuildNow(cacheKey, scene);
        }
        finally
        {
            lock (_buildLock)
            {
                if (built == null)
                    _buildFailures[cacheKey] = Environment.TickCount64;
                _buildingKey = null;
                _buildProgress = -1;
            }
        }
    }

    // builds from game files when possible; returns the built file path or null.
    // `scene` is Ariadne's live capture (buildZone) - when present it replaces the offline
    // LGB reconstruction, so the mesh matches the variant the player is actually in.
    private string? BuildNow(string cacheKey, SceneCaptureDto? scene = null)
    {
        if (_sqpackDir == null)
            return null;
        var bgPath = ZoneNames.Lookup(cacheKey)?.Bg;
        if (scene == null && bgPath is not { Length: > 0 })
            return null;
        var builtPath = (scene != null ? CapturedPathFor(cacheKey) : BuiltPathFor(cacheKey))!;
        Directory.CreateDirectory(Path.GetDirectoryName(builtPath)!);
        {
            // a capture is always worth building: it describes a variant no file on disk does
            if (scene == null && IsCurrentMeshFile(builtPath))
                return builtPath; // raced with another request
            try
            {
                var source = scene != null
                    ? $"a live capture ({scene.InstanceCount} instances, {scene.FestivalLayers.Length} festival layers)"
                    : $"game files ({bgPath})";
                Console.WriteLine($"building '{cacheKey}' from {source}...");
                var sw = System.Diagnostics.Stopwatch.StartNew();
                _buildProgress = 0;
                var navmesh = scene != null
                    ? Mnemosyne.Builder.ZoneBuilder.BuildCaptured(_sqpackDir, scene, bgPath,
                        (done, total) => _buildProgress = total > 0 ? (float)done / total : 0)
                    : Mnemosyne.Builder.ZoneBuilder.BuildAuto(_sqpackDir, bgPath!,
                    (done, total) => _buildProgress = total > 0 ? (float)done / total : 0);
                Directory.CreateDirectory(BuiltDirectory);
                var temp = builtPath + ".tmp";
                using (var stream = File.Create(temp))
                using (var writer = new BinaryWriter(stream))
                    navmesh.Serialize(writer);
                File.Move(temp, builtPath, true);
                Console.WriteLine($"built '{cacheKey}' in {sw.Elapsed.TotalSeconds:f1} s -> {builtPath}");
                return builtPath;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"build failed for '{cacheKey}': {ex.Message}");
                return null;
            }
        }
    }

    private Response ZoneStatus(Request req, int clientId = 0)
    {
        var vnavPath = KeyToPath(req.CacheKey);
        if (vnavPath == null)
            return Error(req, "cacheKey required");
        // a current-version file (vnavmesh's or our built fallback) counts as cached
        var file = FindZoneFile(req.CacheKey);
        if (file != null)
        {
            var (_, version, customization) = MeshCache.ReadHeader(file);
            return new ZoneStatusResponse
            {
                Id = req.Id,
                Ok = true,
                Status = "cached",
                Version = (int)version,
                Customization = customization,
                Progress = BuildProgress,
                Building = BuildProgress >= 0,
                PathfindInProgress = PathfindQueued(clientId) > 0,
                PathfindNumQueued = PathfindQueued(clientId),
            };
        }
        if (File.Exists(vnavPath))
        {
            try
            {
                var (_, version, customization) = MeshCache.ReadHeader(vnavPath);
                return new ZoneStatusResponse { Id = req.Id, Ok = true, Status = "stale", Version = (int)version,
                    Customization = customization, Progress = BuildProgress, Building = BuildProgress >= 0,
                    PathfindInProgress = PathfindQueued(clientId) > 0, PathfindNumQueued = PathfindQueued(clientId) };
            }
            catch (IOException)
            {
                return new ZoneStatusResponse { Id = req.Id, Ok = true, Status = "stale" };
            }
        }
        return new ZoneStatusResponse
        {
            Id = req.Id,
            Ok = true,
            // "building" is a distinct status, not a flavour of missing: it tells a polling
            // client to keep waiting rather than to kick a build of its own.
            Status = IsBuilding(req.CacheKey) ? "building" : "missing",
            Progress = BuildProgress,
            Building = IsBuilding(req.CacheKey),
            PathfindInProgress = PathfindQueued(clientId) > 0,
            PathfindNumQueued = PathfindQueued(clientId),
        };
    }

    // ---- overrides baked into served files -----------------------------------------------
    // Ariadne's open design question, answered "bake" (protocol doc). A seeded vnavmesh cache
    // then carries the curated mesh too, so hand edits work during the transition rather than
    // only after the replacement. The viewer keeps applying overrides in memory for live
    // editing; this is the on-disk form for everyone else.

    public static string ServedDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mnemosyne", "served");

    private readonly object _bakeLock = new();

    /// <summary>The path to hand out for this zone: the source file when it has no overrides,
    /// otherwise a baked copy, rebuilt whenever the source or the overrides move ahead of it.</summary>
    private string BakeOverrides(string cacheKey, string sourcePath)
    {
        var overrides = OverrideStore.Load(cacheKey);
        // Obstacles are a pathfinding input, not a mesh edit - the padding pass reads them and
        // the mesh is deliberately left alone. A zone carrying only obstacles has nothing to
        // bake, and baking it would copy the file to produce a byte-identical result, then hand
        // that copy out in place of a capture that had been verified against vnavmesh.
        if (overrides.FlagEdits.Count == 0 && overrides.Links.Count == 0 && overrides.PruneSeed == null)
            return sourcePath;

        var bakedPath = Path.Combine(ServedDirectory, cacheKey + ".navmesh");
        var overridePath = Path.Combine(OverrideStore.Directory, OverrideStore.BgKey(cacheKey) + ".json");
        lock (_bakeLock)
        {
            try
            {
                if (IsCurrentMeshFile(bakedPath))
                {
                    var baked = File.GetLastWriteTimeUtc(bakedPath);
                    var sourceFresh = File.GetLastWriteTimeUtc(sourcePath) > baked;
                    var editsFresh = File.Exists(overridePath) && File.GetLastWriteTimeUtc(overridePath) > baked;
                    if (!sourceFresh && !editsFresh)
                        return bakedPath;
                }

                var mesh = MeshCache.Load(sourcePath);
                var changed = OverrideStore.Apply(mesh.Mesh, overrides);
                Directory.CreateDirectory(ServedDirectory);
                var temp = bakedPath + ".tmp";
                using (var stream = File.Create(temp))
                using (var writer = new BinaryWriter(stream))
                    mesh.Serialize(writer);
                File.Move(temp, bakedPath, true);
                Console.WriteLine($"baked {changed} override edits into '{cacheKey}' -> {bakedPath}");
                return bakedPath;
            }
            catch (Exception ex)
            {
                // a failed bake must not cost the consumer its mesh - serve the raw one
                Console.WriteLine($"bake failed for '{cacheKey}': {ex.Message} - serving the unbaked file");
                return sourcePath;
            }
        }
    }

    private Response GetMesh(Request req)
    {
        if (KeyToPath(req.CacheKey) == null)
            return Error(req, "cacheKey required");
        var file = FindZoneFile(req.CacheKey);
        if (file == null)
            return KickBuild(req.CacheKey!) != BuildKick.NotBuildable
                ? Error(req, "building", Results.MeshNotReady)
                : Error(req, "missing (and not buildable)");
        file = BakeOverrides(req.CacheKey!, file);
        var (_, version, customization) = MeshCache.ReadHeader(file);
        return new GetMeshResponse
        {
            Id = req.Id,
            Ok = true,
            Path = Path.GetFullPath(file),
            Version = (int)version,
            Customization = customization,
            Size = new FileInfo(file).Length,
        };
    }

    // latest game state pushed by Ariadne (updateGameState); consumed by the viewer (getGameState)
    private sealed record GameState(string? CacheKey, uint TerritoryId, float[] Pos, float Rotation, bool Flying,
        long TickMs, string? Character, ulong ContentId);
    // keyed by connection: four game clients push independently and must not overwrite
    // each other (a single global slot made the viewer jump between characters)
    private readonly Dictionary<int, GameState> _gameStates = [];
    private readonly object _gameStateLock = new();
    private readonly SpeedTracker _speeds = new();
    private const double GameStateStaleMs = 5000;

    private Response UpdateGameState(Request req, int clientId)
    {
        if (req.Pos is not { Length: 3 })
            return Error(req, "pos must be [x, y, z]");
        var now = Environment.TickCount64;
        // speed calibration is machine-wide and already keyed per zone; four toons feeding
        // it is fine (and converges faster)
        _speeds.OnSample(req.CacheKey, new Vector3(req.Pos[0], req.Pos[1], req.Pos[2]), req.Flying ?? false, req.Speed, now);
        lock (_gameStateLock)
            _gameStates[clientId] = new GameState(req.CacheKey, req.TerritoryId ?? 0, req.Pos,
                req.Rotation ?? 0, req.Flying ?? false, now, req.Character, req.ContentId ?? 0);
        return new Response { Id = req.Id, Ok = true };
    }

    public void OnClientDisconnected(int clientId)
    {
        lock (_gameStateLock)
            _gameStates.Remove(clientId);
        lock (_pathfindLock)
            _pathfindActive.Remove(clientId);
    }

    private Response GetGameState(Request req)
    {
        var now = Environment.TickCount64;
        List<PlayerDto> players;
        lock (_gameStateLock)
        {
            foreach (var stale in _gameStates.Where(kv => now - kv.Value.TickMs > GameStateStaleMs)
                         .Select(kv => kv.Key).ToList())
                _gameStates.Remove(stale);
            players = [.. _gameStates.Select(kv => new PlayerDto
            {
                ClientId = kv.Key,
                Character = kv.Value.Character,
                ContentId = kv.Value.ContentId,
                CacheKey = kv.Value.CacheKey,
                TerritoryId = kv.Value.TerritoryId,
                Pos = kv.Value.Pos,
                Rotation = kv.Value.Rotation,
                Flying = kv.Value.Flying,
                Speed = _speeds.CurrentSpeed,
                AgeMs = now - kv.Value.TickMs,
            }).OrderBy(p => p.ClientId)];
        }

        // flat fields mirror the most recently updated player so legacy consumers keep working
        var latest = players.MinBy(p => p.AgeMs);
        var speeds = new SpeedsDto { Ground = _speeds.GroundSpeed, Fly = _speeds.FlySpeed };
        if (latest == null)
            return new GameStateResponse { Id = req.Id, Ok = true, Present = false, Speeds = speeds, Players = players };
        return new GameStateResponse
        {
            Id = req.Id,
            Ok = true,
            Present = true,
            CacheKey = latest.CacheKey,
            TerritoryId = latest.TerritoryId,
            Pos = latest.Pos,
            Rotation = latest.Rotation,
            Flying = latest.Flying,
            AgeMs = latest.AgeMs,
            Speed = latest.Speed,
            Speeds = speeds,
            Players = players,
        };
    }

    private Response NotifyMeshBuilt(Request req)
    {
        // drop any warm copy so the next findPath reloads the fresh build
        if (req.CacheKey is { Length: > 0 } key)
        {
            lock (_lock)
                _zones.Remove(key);
            Console.WriteLine($"notifyMeshBuilt: {key}");
        }
        return new Response { Id = req.Id, Ok = true };
    }

    private Response FindPath(Request req, int clientId)
    {
        PathfindEnter(clientId);
        try
        {
            return FindPathCore(req);
        }
        finally
        {
            PathfindExit(clientId);
        }
    }

    private Response FindPathCore(Request req)
    {
        if (req.From is not { Length: 3 } || req.To is not { Length: 3 })
            return Error(req, "from and to must be [x, y, z]");
        if (KeyToPath(req.CacheKey) == null)
            return Error(req, "cacheKey required");
        var path = FindZoneFile(req.CacheKey);
        if (path == null)
            return KickBuild(req.CacheKey!) != BuildKick.NotBuildable
                ? Error(req, "zone is building", Results.MeshNotReady)
                : Error(req, "missing (and not buildable)");

        var zone = GetOrLoad(req.CacheKey!, path);
        var from = new Vector3(req.From[0], req.From[1], req.From[2]);
        var to = new Vector3(req.To[0], req.To[1], req.To[2]);

        var avoidCenter = req.AvoidCenter is { Length: 3 } a ? new Vector3(a[0], a[1], a[2]) : (Vector3?)null;
        var avoidRadius = req.AvoidRadius ?? 0;
        // Nav.PathfindAvoid. vnavmesh gates this on the straight from->to segment entering
        // the circle, which silently ignores a hazard sitting on the actual (curved) route
        // - the common case, and one of the "it just walks into it" complaints. We filter
        // whenever a radius is asked for, and instead clamp the radius so it can never
        // exclude the start or the goal (standing in the hazard must not mean "no path").
        var effAvoidRadius = 0f;
        if (avoidRadius > 0 && avoidCenter is { } ac)
        {
            var room = MathF.Min(DistanceXZ(from, ac), DistanceXZ(to, ac)) - 0.5f;
            effAvoidRadius = MathF.Min(avoidRadius, MathF.Max(room, 0));
        }
        var avoidApplies = effAvoidRadius > 0;

        lock (zone.Lock)
        {
            List<Vector3> waypoints;
            bool partial = false;
            bool avoidIgnored = false;
            FindPathResponse? walkFailure = null; // classification for a partial walk route
            Vector3? walkTailFrom = null;          // where a fly route had to land and walk on
            int flyLegCount = 0;                   // waypoints belonging to the fly leg
            bool groundWasFaster = false;          // a fly request answered with a ground route
            bool budgetExhausted = false;          // a search ran out of budget: this route is the best it found
            Vector3? disconnectedTo = null;        // `to` is in another island; this is the closest reachable ground
            if (req.Fly == true)
            {
                // The volume is tens of MB to decode - 5.4 s measured on a field zone - and a
                // client holds a 10 s request timeout, so it must never be paid for inside a
                // request. Kick it once, answer meshNotReady, and let the client poll: the same
                // shape as a zone that is still building.
                if (LoadVolumeInBackground(zone))
                    return Error(req, "volume loading", Results.MeshNotReady);

                // Plan the whole trip, not just the flight. The ad-hoc version flew until
                // the volume search gave up and walked from wherever that happened to be -
                // which in game meant landing at the side of a building and running around to
                // the front door. The backbone planner instead takes off at the first open-sky
                // point on the ground route and lands at the *last* one, so the landing slides
                // back to the entrance and the walk tail is the short bit that has to be walked.
                // It also costs mount/dismount and compares against staying on foot, so a short
                // hop stops pretending flying is worth it.
                if (_speeds.FlySpeed is { } planFly and > 0.5f && _speeds.GroundSpeed is { } planGround and > 0.5f)
                {
                    if (zone.Volume != null)
                    {
                        zone.Flight ??= new FlightPathfinder(zone.Volume);
                        var planner = new MultiModalPlanner(zone.Pathfinder, zone.Flight);
                        var plan = planner.Plan(from, to, planGround, planFly, zone.Overrides.Links);
                        if (plan != null)
                        {
                            var route = plan.Flatten();
                            PathPadding.Apply(zone.Mesh, route, req.Clearance ?? PathPadding.DefaultPad,
                                zone.Overrides.Obstacles);
                            Console.WriteLine($"'{zone.Key}': {plan.Summary} ({plan.TotalSeconds:f1}s)");

                            // Legs index into the flat array. Padding only ever touches the
                            // walk legs' interiors, so leg lengths are recomputed by walking
                            // the plan rather than trusted from before padding ran.
                            var legs = new List<LegDto>();
                            var cursor = 0;
                            foreach (var leg in plan.Legs)
                            {
                                var count = Math.Min(leg.Waypoints.Count, route.Count - cursor);
                                if (count <= 0)
                                    break;
                                legs.Add(new LegDto
                                {
                                    Mode = leg.Mode == TravelMode.Fly ? "fly" : "walk",
                                    Enter = leg.Mode == TravelMode.Fly ? "mount"
                                        : legs.Count == 0 ? null : "land",
                                    First = cursor,
                                    Count = count,
                                });
                                cursor += count;
                            }
                            if (cursor < route.Count && legs.Count > 0)
                                legs[^1].Count += route.Count - cursor; // padding inserted into the last leg

                            return new FindPathResponse
                            {
                                Id = req.Id,
                                Ok = true,
                                Waypoints = [.. route.Select(w => new[] { w.X, w.Y, w.Z })],
                                Partial = false,
                                Result = plan.Legs.Any(l => l.Mode == TravelMode.Fly)
                                    ? Results.Ok
                                    : Results.GroundFaster,
                                Legs = legs.Count > 1 ? legs : null,
                                EtaSeconds = plan.TotalSeconds,
                            };
                        }
                        Console.WriteLine($"'{zone.Key}': no multi-modal plan ({planner.AirReason}) - falling back to a raw volume search");
                    }
                }

                if (zone.Volume == null)
                    return Error(req, "zone has no flying volume");

                // primary: coarse octree Lazy Theta* (PLAN 9b) - km-scale hops in single-digit ms
                if (zone.Flight == null)
                {
                    var buildSw = System.Diagnostics.Stopwatch.StartNew();
                    zone.Flight = new FlightPathfinder(zone.Volume);
                    Console.WriteLine($"flight octree for '{zone.Key}': {zone.Flight.Nav.NodeCount} nodes in {buildSw.ElapsedMilliseconds} ms");
                }
                // the coarse octree has no notion of the avoid circle, so an avoid request
                // has to go down the voxel path (which is also what vnavmesh does)
                var flightPath = avoidApplies ? [] : zone.Flight.FindPath(from, to, out _);
                if (flightPath.Count >= 2)
                {
                    waypoints = flightPath;
                    partial = false;
                }
                else
                {
                    // fallback: fine-grained voxel A* (handles coarse-disconnected pockets)
                    zone.VolumeQuery ??= new VoxelPathfind(zone.Volume) { MaxSteps = FlyStepBudget };
                    var fromVoxel = VoxelSearch.FindNearestEmptyVoxel(zone.Volume, from, new Vector3(3, 3, 3));
                    var toVoxel = VoxelSearch.FindNearestEmptyVoxel(zone.Volume, to, new Vector3(3, 3, 3));
                    if (fromVoxel == VoxelMap.InvalidVoxel)
                    {
                        // Nothing flyable inside the snap box: widen it once and name the closest
                        // empty voxel there is - the same "here is the nearest thing to you" the
                        // walk side gives for an off-mesh start.
                        var fallback = VoxelSearch.FindNearestEmptyVoxel(zone.Volume, from, new Vector3(20, 20, 20));
                        Vector3? at = fallback == VoxelMap.InvalidVoxel ? null : VoxelSearch.FindClosestVoxelPoint(zone.Volume, fallback, from);
                        return OffMesh(req, Results.StartOffMesh, "no empty voxel near `from`",
                            at is { } p ? [p.X, p.Y, p.Z] : null);
                    }
                    if (toVoxel == VoxelMap.InvalidVoxel)
                    {
                        // The goal is not in the volume at all. Name the closest point the flight
                        // can *certainly* reach (a bounded walk over the coarse graph - see
                        // FlightPathfinder.NearestReachable), which is what a consumer needs to fly
                        // as close as it can and walk the rest.
                        var reach = zone.Flight?.NearestReachable(from, to);
                        return OffMesh(req, Results.TargetOffMesh, "no empty voxel near `to`",
                            reach is { } r ? [r.X, r.Y, r.Z] : null);
                    }
                    var voxelPath = zone.VolumeQuery.FindPath(fromVoxel, toVoxel, from, to, false, false,
                        CancellationToken.None, avoidCenter, effAvoidRadius); // raycast=true measured 9x SLOWER offline
                    if (voxelPath.Count == 0)
                    {
                        // The fine engine is the authority on whether the volume connects, so a
                        // failure here is a real no-route - but the coarse graph can still name
                        // the closest point the flight could have reached, and that is the useful
                        // half of the answer: the caller's walk fallback takes it from there.
                        var reachable = zone.Flight?.NearestReachable(from, to);
                        return new FindPathResponse
                        {
                            Id = req.Id,
                            Ok = false,
                            Error = "no volume path found",
                            Result = Results.NoRouteOnMesh,
                            Nearest = reachable is { } reach ? [reach.X, reach.Y, reach.Z] : null,
                        };
                    }
                    // The voxel search emits one waypoint per grid step, so the raw route
                    // zigzags through open air - 64 waypoints over 161 m for a 53 m hop, and
                    // it flies as badly as it reads. vnavmesh never smoothed these either.
                    waypoints = VolumePathSmoother.Smooth(zone.Volume, voxelPath);
                    partial = Vector3.Distance(waypoints[^1], to) > 10; // stopped short of the goal
                    budgetExhausted = partial; // the voxel search ran to its step cap and returned the best it had
                }

                // A flight route that stops short is usually not a budget problem - it is the
                // volume disagreeing with the mesh. Measured at the Yedlihmad doorway: the
                // navmesh leaves the door open and the voxel volume seals it, because the
                // opening is narrower than a voxel leaf. It is also just true that you cannot
                // fly indoors, so the honest route is fly to the threshold and walk in.
                //
                // Rather than hand back a route that dies at a doorway, walk the remainder.
                if (partial && waypoints.Count > 0)
                {
                    var landing = waypoints[^1];
                    var tail = zone.Pathfinder.FindWalkPath(landing, to, zone.Overrides.Links);
                    if (tail is { Partial: false, Waypoints.Count: >= 2 })
                    {
                        // skip the tail's first waypoint: it is the landing point we already have
                        waypoints.AddRange(tail.Waypoints.Skip(1));
                        PathPadding.Apply(zone.Mesh, waypoints, req.Clearance ?? PathPadding.DefaultPad,
                            zone.Overrides.Obstacles);
                        partial = false;
                        walkTailFrom = landing;
                        flyLegCount = waypoints.Count - (tail.Waypoints.Count - 1);
                    }
                }

                // Flying is not automatically faster. Near the ground the volume is mostly
                // solid, so an air route weaves between buildings: measured 142 m for a 53 m
                // hop in Yedlihmad while the ground route was 54 m. Flying is ~3x quicker per
                // yalm, so the fair comparison is time, not distance - and sometimes walking
                // wins outright. The caller asked to fly; answering with a slower route than
                // walking would be obeying the letter and missing the point.
                if (!partial && waypoints.Count > 1 && _speeds.FlySpeed is { } flySpeed and > 0.5f
                    && _speeds.GroundSpeed is { } groundSpeed and > 0.5f)
                {
                    var ground = zone.Pathfinder.FindWalkPath(from, to, zone.Overrides.Links);
                    if (ground is { Partial: false, Waypoints.Count: >= 2 })
                    {
                        var airEta = PathLength(waypoints) / flySpeed;
                        var groundEta = PathLength(ground.Waypoints) / groundSpeed;
                        if (groundEta < airEta)
                        {
                            Console.WriteLine($"'{zone.Key}': walking is faster here "
                                + $"({groundEta:f1}s over {PathLength(ground.Waypoints):f0}m vs "
                                + $"{airEta:f1}s over {PathLength(waypoints):f0}m flying) - answering with the ground route");
                            waypoints = ground.Waypoints;
                            PathPadding.Apply(zone.Mesh, waypoints, req.Clearance ?? PathPadding.DefaultPad,
                                zone.Overrides.Obstacles);
                            groundWasFaster = true;
                        }
                    }
                }

            }
            else
            {
                var avoidFilter = avoidApplies ? new AvoidRadiusFilter(avoidCenter!.Value, effAvoidRadius) : null;
                var result = zone.Pathfinder.FindWalkPath(from, to, zone.Overrides.Links, avoidFilter);
                // A hazard can plug the only corridor. Saying "no path" there is the worst
                // of both worlds - the caller loses the route AND the reason. Fall back to
                // the unconstrained route and label it, so the consumer can decide.
                if (avoidFilter != null && result is null or { Partial: true })
                {
                    var unconstrained = zone.Pathfinder.FindWalkPath(from, to, zone.Overrides.Links);
                    if (unconstrained is { Partial: false })
                    {
                        result = unconstrained;
                        avoidIgnored = true;
                    }
                }
                float tolerance = req.Tolerance ?? 0;
                if (result is null or { Partial: true } && tolerance > 0)
                {
                    // Nav.PathfindWithTolerance / SimpleMove.PathfindAndMoveCloseTo: the goal
                    // itself may be off-mesh, so retry against the nearest reachable point
                    // inside the radius instead of answering "no path" (see protocol doc).
                    if (zone.Pathfinder.TryNearestGround(to, tolerance, out var near)
                        && Vector3.Distance(near, to) <= tolerance)
                    {
                        var retry = zone.Pathfinder.FindWalkPath(from, near, zone.Overrides.Links, avoidFilter);
                        if (retry is { Partial: false })
                            result = retry;
                    }
                }
                if (result == null)
                    return ClassifyWalkFailure(req, zone, from, to);
                if (result.DisconnectedTo is { } unreachable)
                {
                    // The islands proved `to` is in another component, and this route runs to the
                    // closest reachable ground instead. No search can do better than that - and
                    // this is exactly the case that used to pay for an exhaustive one.
                    disconnectedTo = unreachable;
                }
                else if (result.BudgetExhausted)
                {
                    // Not a proof of anything: the search ran out of budget with a route toward
                    // the goal. Answering noRouteOnMesh here would be a claim it has not earned.
                    budgetExhausted = true;
                }
                else if (result.Partial)
                {
                    // A partial walk route with the search finished means the two ends are in
                    // different components. Whether that is a mesh defect or an honest
                    // disconnect is the whole question a consumer needs answered, so answer it -
                    // but still hand back the waypoints, since walking toward the goal is usually
                    // right.
                    walkFailure = ClassifyWalkFailure(req, zone, from, to);
                }
                waypoints = result.Waypoints;
                partial = result.Partial;
                if (tolerance > 0 && waypoints.Count > 2)
                {
                    // trim the tail that is already inside the tolerance radius
                    int keep = waypoints.Count;
                    while (keep > 2 && Vector3.Distance(waypoints[keep - 2], to) <= tolerance)
                        --keep;
                    if (keep < waypoints.Count)
                        waypoints = waypoints.GetRange(0, keep);
                }

                // Push the route off the walls it hugs. Recast's cost is pure distance, so the
                // cheapest path clips every corner it rounds - measured at 0.00 m clearance on
                // 7 of 8 waypoints across Ul'dah, which is what a follower snags on. Runs after
                // the tolerance trim so the final waypoint, which the trim just chose, stays put.
                var clearance = req.Clearance ?? PathPadding.DefaultPad;
                if (clearance > 0)
                    PathPadding.Apply(zone.Mesh, waypoints, clearance, zone.Overrides.Obstacles);
            }
            float pathLength = 0;
            for (int i = 1; i < waypoints.Count; ++i)
                pathLength += Vector3.Distance(waypoints[i - 1], waypoints[i]);
            var modeSpeed = req.Fly == true ? _speeds.FlySpeed : _speeds.GroundSpeed;
            return new FindPathResponse
            {
                Id = req.Id,
                Ok = true,
                Waypoints = [.. waypoints.Select(w => new[] { w.X, w.Y, w.Z })],
                Partial = partial,
                // "ok" only when the route is genuinely complete and unqualified; anything
                // else names itself so the consumer acts once instead of guessing.
                Result = avoidIgnored ? Results.AvoidIgnored
                    : groundWasFaster ? Results.GroundFaster
                    : walkTailFrom != null ? Results.WalkedTail
                    : disconnectedTo != null ? Results.NoRouteOnMesh
                    : budgetExhausted && partial ? Results.BudgetExhausted
                    : walkFailure?.Result ?? (partial ? Results.NoRouteOnMesh : Results.Ok),
                Nearest = walkFailure?.Nearest ?? (disconnectedTo is { } dc
                    ? new[] { dc.X, dc.Y, dc.Z }
                    : null),
                // Padding can insert waypoints into the tail, so the split is recomputed from
                // the final array rather than remembered from before it ran.
                Legs = groundWasFaster
                    ? [new LegDto { Mode = "walk", Enter = "land", First = 0, Count = waypoints.Count }]
                    : walkTailFrom == null ? null :
                [
                    new LegDto { Mode = "fly", First = 0, Count = flyLegCount },
                    new LegDto
                    {
                        Mode = "walk",
                        Enter = "land", // the volume seals the doorway; the rest is on foot
                        First = flyLegCount,
                        Count = waypoints.Count - flyLegCount,
                    },
                ],
                EtaSeconds = modeSpeed is { } v && v > 0.5f ? pathLength / v : null,
            };
        }
    }

    private static float PathLength(List<Vector3> path)
    {
        float length = 0;
        for (int i = 1; i < path.Count; ++i)
            length += Vector3.Distance(path[i - 1], path[i]);
        return length;
    }

    private static float DistanceXZ(Vector3 a, Vector3 b)
    {
        var dx = a.X - b.X;
        var dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    /// <summary>Load the flying volume off the request thread, once per zone however many clients
    /// ask for it. Returns true while a load is still in flight, so the caller can answer
    /// meshNotReady and let the client poll: the decode measured 5.4 s on a field zone against a
    /// 10 s client timeout, and the octree build that follows it belongs off the request path
    /// too. With the load out of the way, a fly request is a search again - which is what the
    /// caller's budget is for.</summary>
    private static bool LoadVolumeInBackground(LoadedZone zone)
    {
        if (zone.Volume != null)
            return false; // loaded
        if (!zone.HasVolume)
            return false; // nothing to load; the caller reports that
        if (zone.VolumeLoad is { IsCompleted: false })
            return true; // already in flight: this request waits for the next poll
        if (zone.VolumeLoad is { IsCompleted: true })
            return false; // finished without producing a volume; do not spin on it every request

        zone.VolumeLoad = Task.Run(() =>
        {
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var volume = FastCache.LoadVolume(zone.FastPath);
                lock (zone.Lock)
                {
                    zone.Volume = volume;
                    if (volume != null)
                        zone.Flight = new FlightPathfinder(volume); // the octree, off the request too
                }
                Console.WriteLine($"volume for '{zone.Key}' loaded in {sw.ElapsedMilliseconds} ms (off-request)");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"volume load for '{zone.Key}' failed: {ex.Message}");
            }
        });
        return true;
    }

    /// <summary>When a zone's edits were last written, or default if it has none. One service
    /// serves the whole fleet from one loaded copy, so an edit has to reach every client - and
    /// a zone loaded before the edit would otherwise keep serving the old mesh until it was
    /// evicted, which with MaxLoaded = 10 can be never.</summary>
    private static DateTime OverridesStampFor(string cacheKey)
    {
        try
        {
            var path = Path.Combine(OverrideStore.Directory, OverrideStore.BgKey(cacheKey) + ".json");
            return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : default;
        }
        catch (IOException)
        {
            return default; // being rewritten right now; the next request picks it up
        }
    }

    private LoadedZone GetOrLoad(string key, string path)
    {
        var stamp = OverridesStampFor(key);
        lock (_lock)
        {
            if (_zones.TryGetValue(key, out var existing))
            {
                if (existing.OverridesStamp == stamp)
                {
                    existing.LastUse = Environment.TickCount64;
                    return existing;
                }
                // Someone edited this zone - the viewer, a solids audit, a promoted traversal.
                // Drop the cached copy so every client picks the edit up on its next query.
                Console.WriteLine($"'{key}': edits changed on disk, reloading for all clients");
                _zones.Remove(key);
            }
        }

        Console.WriteLine($"loading zone '{key}'...");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var loaded = FastCache.Load(path, withVolume: false);
        var overrides = OverrideStore.Load(key);
        var overriddenPolys = OverrideStore.Apply(loaded.Mesh, overrides);
        var zone = new LoadedZone
        {
            Key = key,
            Mesh = loaded.Mesh,
            Pathfinder = new MeshPathfinder(loaded.Mesh),
            Overrides = overrides,
            HasVolume = loaded.HasVolume,
            FastPath = loaded.FastPath,
            Volume = loaded.Volume, // non-null only when the slow path decoded it anyway
            LastUse = Environment.TickCount64,
            OverridesStamp = stamp,
        };
        Console.WriteLine($"loaded '{key}' in {sw.ElapsedMilliseconds} ms (fast: {loaded.FromFastCache}, hasVolume: {loaded.HasVolume}, overrides: {overrides.FlagEdits.Count} shapes/{overrides.Links.Count} links -> {overriddenPolys} polys)");

        lock (_lock)
        {
            if (_zones.TryGetValue(key, out var raced))
                return raced;
            while (_zones.Count >= MaxLoaded)
            {
                var oldest = _zones.Values.OrderBy(z => z.LastUse).First();
                _zones.Remove(oldest.Key);
                Console.WriteLine($"evicted zone '{oldest.Key}'");
            }
            _zones[key] = zone;
            return zone;
        }
    }
}

