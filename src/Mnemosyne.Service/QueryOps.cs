using DotRecast.Detour;
using Mnemosyne.Core;
using Mnemosyne.Protocol;
using Navmesh;
using System.Numerics;

namespace Mnemosyne.Service;

// The Query.Mesh.* / Nav.BuildBitmap* gates consumers call (Theseus, Olympus, SealBreaker).
// Semantics deliberately mirror vnavmesh's NavmeshQuery helpers so a consumer relayed
// through Ariadne gets the same answers it got from vnavmesh.
public sealed partial class ZoneService
{
    // vnavmesh's "reachable" filter excludes flood-fill-pruned polys; ours excludes the same
    // flag, which our OverrideStore prune also sets.
    private static readonly IDtQueryFilter ReachableFilter = new DtQueryDefaultFilter(
        0xffff & ~global::Navmesh.Navmesh.FLAG_UNREACHABLE, 0, [1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f]);

    private static Vector3? Vec(float[]? a) => a is { Length: 3 } ? new Vector3(a[0], a[1], a[2]) : null;
    private static float[] Arr(Vector3 v) => [v.X, v.Y, v.Z];

    /// <summary>Loaded zone for a query op, or null with the error response filled in.</summary>
    private LoadedZone? ZoneForQuery(Request req, out Response? error)
    {
        error = null;
        if (KeyToPath(req.CacheKey) == null)
        {
            error = Error(req, "cacheKey required");
            return null;
        }
        var path = FindZoneFile(req.CacheKey);
        if (path == null)
        {
            // never block a query on a cold build - say so and let the caller poll zoneStatus
            error = KickBuild(req.CacheKey!) != BuildKick.NotBuildable
                ? Error(req, "zone is building", Results.MeshNotReady)
                : Error(req, "missing (and not buildable)");
            return null;
        }
        return GetOrLoad(req.CacheKey!, path);
    }

    private Response NearestPoint(Request req)
    {
        if (Vec(req.Point) is not { } p)
            return Error(req, "point must be [x, y, z]");
        var zone = ZoneForQuery(req, out var error);
        if (zone == null)
            return error!;

        float xz = req.HalfExtentXZ ?? 5, y = req.HalfExtentY ?? 5;
        var filter = req.ReachableOnly == true ? ReachableFilter : _filter;
        lock (zone.Lock)
        {
            var query = new DtNavMeshQuery(zone.Mesh);
            query.FindNearestPoly(p.SystemToRecast(), new(xz, y, xz), filter, out var polyRef, out _, out _);
            if (polyRef == 0)
                return new PointResponse { Id = req.Id, Ok = true, Found = false };
            // vnavmesh answers with the closest point ON that poly, not the poly centre
            if (query.ClosestPointOnPoly(polyRef, p.SystemToRecast(), out var closest, out _).Failed())
                return new PointResponse { Id = req.Id, Ok = true, Found = false };
            return new PointResponse { Id = req.Id, Ok = true, Found = true, Point = Arr(closest.RecastToSystem()) };
        }
    }

    private Response IsPointOnMesh(Request req)
    {
        if (Vec(req.Point) is not { } p)
            return Error(req, "point must be [x, y, z]");
        var zone = ZoneForQuery(req, out var error);
        if (zone == null)
            return error!;

        float y = req.HalfExtentY ?? 5;
        var filter = req.AllowUnreachable == false ? ReachableFilter : _filter;
        lock (zone.Lock)
        {
            var query = new DtNavMeshQuery(zone.Mesh);
            // XZ extent 0 on purpose: "is this point over the mesh", not "is one nearby"
            query.FindNearestPoly(p.SystemToRecast(), new(0, y, 0), filter, out _, out _, out var overPoly);
            return new OnMeshResponse { Id = req.Id, Ok = true, OnMesh = overPoly };
        }
    }

    private Response PointOnFloor(Request req)
    {
        if (Vec(req.Point) is not { } p)
            return Error(req, "point must be [x, y, z]");
        var zone = ZoneForQuery(req, out var error);
        if (zone == null)
            return error!;

        float xz = req.HalfExtentXZ ?? 5;
        var filter = req.AllowUnreachable == false ? ReachableFilter : _filter;
        lock (zone.Lock)
        {
            var query = new DtNavMeshQuery(zone.Mesh);
            // every poly in a tall column, then the highest surface still below p (vnavmesh
            // searches +-2048 y the same way)
            var collector = new PolyCollector();
            query.QueryPolygons(p.SystemToRecast(), new(xz, 2048, xz), filter, collector);
            Vector3? best = null;
            foreach (var poly in collector.Result)
            {
                if (query.ClosestPointOnPoly(poly, p.SystemToRecast(), out var closest, out _).Failed())
                    continue;
                var pt = closest.RecastToSystem();
                if (pt.Y <= p.Y && (best == null || pt.Y > best.Value.Y))
                    best = pt;
            }
            return best is { } found
                ? new PointResponse { Id = req.Id, Ok = true, Found = true, Point = Arr(found) }
                : new PointResponse { Id = req.Id, Ok = true, Found = false };
        }
    }

    private sealed class PolyCollector : IDtPolyQuery
    {
        public readonly List<long> Result = [];

        public void Process(DtMeshTile tile, DtPoly poly, long refs) => Result.Add(refs);
    }

    private Response BuildBitmap(Request req)
    {
        if (req.StartingPoints is not { Length: > 0 } starts || string.IsNullOrWhiteSpace(req.Filename))
            return Error(req, "startingPoints and filename required");
        var zone = ZoneForQuery(req, out var error);
        if (zone == null)
            return error!;

        // never let a client name a path - it only names a file inside our own store
        var name = Path.GetFileName(req.Filename);
        if (string.IsNullOrWhiteSpace(name))
            return Error(req, "invalid filename");
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mnemosyne", "bitmaps");
        Directory.CreateDirectory(dir);
        var outPath = Path.Combine(dir, name);

        lock (zone.Lock)
        {
            var bitmap = NavmeshBitmapBuilder.Build(zone.Mesh,
                [.. starts.Select(s => new Vector3(s[0], s[1], s[2]))],
                req.PixelSize ?? 0.5f,
                Vec(req.MinBounds), Vec(req.MaxBounds));
            bitmap.Save(outPath);
        }
        return new BitmapResponse { Id = req.Id, Ok = true, Path = outPath };
    }

    // ---- reachableCells: the exploration query (spec: docs/mnemosyne-protocol.md) --------

    /// <summary>"Where can I walk from here?" as a world-aligned grid of stacked walkable
    /// surfaces — Theseus's auto-solver uses it to find unexplored ground and the edges where
    /// walkable mesh is cut off. Reachability only: visited state stays with the consumer, per
    /// run. The flood half is cached per start poly; the rasterisation is redone per query,
    /// since the window (radius, cell size, height band) differs between calls from one spot.
    /// </summary>
    private Response ReachableCells(Request req)
    {
        if (Vec(req.From) is not { } from)
            return Error(req, "from must be [x, y, z]");
        var radius = req.Radius ?? 120f;
        var cellSize = req.CellSize ?? 2f;
        if (radius <= 0 || radius > 512)
            return Error(req, "radius must be 0-512 yalms");
        if (cellSize is < 0.5f or > 16)
            return Error(req, "cellSize must be 0.5-16 yalms");

        // World-aligned cells can add a column or row: refuse a window past the ceiling rather
        // than truncate it, because a truncated grid reads as "nothing out there" (spec).
        var plan = ReachableCellsQuery.GridPlan(from, radius, cellSize);
        if (!ReachableCellsQuery.PlanFits(plan))
            return Error(req, $"grid too large: {plan.Width}x{plan.Depth} columns at radius {radius} / cellSize {cellSize} (max {ReachableCellsQuery.MaxColumns})");

        var zone = ZoneForQuery(req, out var error);
        if (zone == null)
            return error!;

        lock (zone.Lock)
        {
            var query = new ReachableCellsQuery(zone.Mesh, _filter, zone.Overrides.Links);
            if (!query.TryStart(from, out var startRef, out var snapped))
            {
                return new ReachableCellsResponse
                {
                    Id = req.Id,
                    Ok = false,
                    Error = "`from` is not on the mesh",
                    Result = Results.StartOffMesh,
                    Nearest = NearestReachable(zone, from, 20),
                };
            }

            var component = zone.FloodFrom(startRef, () => query.Flood(startRef));
            // the window is aligned to `from`, not to the snapped point: the spec's grid contract
            // is what lets one visited set span several queries, and the snap can move the point
            // by up to 5 y
            var grid = query.Rasterize(component, from, radius, cellSize, req.MinY, req.MaxY);
            return new ReachableCellsResponse
            {
                Id = req.Id,
                Ok = true,
                Result = Results.Ok,
                Start = Arr(snapped),
                Origin = [grid.Origin.X, grid.Origin.Y],
                CellSize = grid.CellSize,
                Width = grid.Width,
                Depth = grid.Depth,
                Columns = grid.Columns,
                Heights = grid.Heights,
                States = [.. grid.States.Select(s => (int)s)],
                ReachableOutside = grid.ReachableOutside,
                Stats = new ReachableCellsStatsDto
                {
                    ReachablePolys = grid.ReachablePolys,
                    WalkablePolys = grid.WalkablePolys,
                },
            };
        }
    }

    // ---- classified findPath answers (protocol doc, "Classified answers") ----------------
    // A bare "no path" made consumers disambiguate by experiment: retry, walk closer, rebuild
    // the mesh, give up - Odysseus grew a whole retry ladder out of it. These name the cause
    // once so the consumer can act.

    /// <summary>Why a walk route failed or stopped short. Caller holds <c>zone.Lock</c>.</summary>
    private FindPathResponse ClassifyWalkFailure(Request req, LoadedZone zone, Vector3 from, Vector3 to)
    {
        var query = new DtNavMeshQuery(zone.Mesh);
        var snap = new DotRecast.Core.Numerics.RcVec3f(5, 5, 5);
        query.FindNearestPoly(from.SystemToRecast(), snap, _filter, out var fromRef, out _, out _);
        query.FindNearestPoly(to.SystemToRecast(), snap, _filter, out var toRef, out _, out _);

        // how far to look for the consolation point: at least the caller's tolerance, and
        // enough to clear a ledge or a doorway when they gave none
        var reach = MathF.Max(req.Tolerance ?? 0, 20);
        if (fromRef == 0)
            return OffMesh(req, Results.StartOffMesh, "`from` is not on the mesh", NearestReachable(zone, from, reach));
        if (toRef == 0)
            return OffMesh(req, Results.TargetOffMesh, "`to` is not on the mesh", NearestReachable(zone, to, reach));

        // Both ends snapped, so the geometry exists. Flood-fill pruning is the difference
        // between "this island is genuinely cut off" and "the mesh has a hole" - and only the
        // second one is a bug worth waking someone up for.
        zone.Mesh.GetTileAndPolyByRefUnsafe(fromRef, out _, out var fromPoly);
        zone.Mesh.GetTileAndPolyByRefUnsafe(toRef, out _, out var toPoly);
        var pruned = (fromPoly.flags & global::Navmesh.Navmesh.FLAG_UNREACHABLE) != 0 ? "from"
            : (toPoly.flags & global::Navmesh.Navmesh.FLAG_UNREACHABLE) != 0 ? "to" : null;
        if (pruned != null)
            return Fail(req, Results.Unreachable, $"`{pruned}` is in a region the mesh marks unreachable");

        return Fail(req, Results.NoRouteOnMesh, "both ends are on the mesh but no route connects them");
    }

    /// <summary>Closest point a walker could actually stand on and reach, for the `nearest`
    /// field. Null when nothing usable is within range.</summary>
    private static float[]? NearestReachable(LoadedZone zone, Vector3 p, float range)
    {
        var query = new DtNavMeshQuery(zone.Mesh);
        query.FindNearestPoly(p.SystemToRecast(), new(range, range, range), ReachableFilter, out var polyRef, out _, out _);
        if (polyRef == 0)
            return null;
        return query.ClosestPointOnPoly(polyRef, p.SystemToRecast(), out var closest, out _).Succeeded()
            ? Arr(closest.RecastToSystem())
            : null;
    }

    private static FindPathResponse OffMesh(Request req, string result, string message, float[]? nearest) =>
        new() { Id = req.Id, Ok = false, Error = message, Result = result, Nearest = nearest };

    private static FindPathResponse Fail(Request req, string result, string message) =>
        new() { Id = req.Id, Ok = false, Error = message, Result = result };

    // ---- buildZone / reportTraversal: the two feedback channels from the game process -----

    private Response BuildZone(Request req)
    {
        if (req.Scene is not { } scene)
            return Error(req, "scene required");
        var key = req.CacheKey ?? scene.CacheKey;
        if (KeyToPath(key) == null)
            return Error(req, "cacheKey required");
        if (scene.InstanceCount == 0 && scene.Terrains.Length == 0)
            return Error(req, "capture is empty - layout was not ready");

        // Ack immediately and build behind it: the client is holding a 10 s request timeout
        // and the build is tens of seconds. It polls zoneStatus from here.
        var kick = KickBuild(key, scene);
        Console.WriteLine($"buildZone '{key}': {scene.InstanceCount} instances, "
            + $"{scene.Terrains.Length} terrains, {scene.FestivalLayers.Length} festival layers -> {kick}");
        return kick == BuildKick.NotBuildable
            ? Error(req, "cannot build this zone (no game files?)")
            : new Response { Id = req.Id, Ok = true, Result = Results.MeshNotReady };
    }

    private Response ReportTraversal(Request req)
    {
        if (req.From is not { Length: 3 } from || req.To is not { Length: 3 } to)
            return Error(req, "from and to must be [x, y, z]");
        if (KeyToPath(req.CacheKey) == null)
            return Error(req, "cacheKey required");

        var report = new TraversalReport
        {
            From = from,
            To = to,
            Mode = req.Mode ?? "walk",
            Success = req.Success ?? false,
            Note = req.Note,
        };
        var count = EvidenceStore.Record(req.CacheKey!, report);
        if (count > 0)
        {
            var kind = report is { Mode: "direct", Success: true } ? "off-mesh link" : "block/cost";
            Console.WriteLine($"reportTraversal '{req.CacheKey}': {kind} candidate at "
                + $"({from[0]:f0}, {from[1]:f0}, {from[2]:f0}) -> ({to[0]:f0}, {to[1]:f0}, {to[2]:f0}), seen {count}x");
        }
        // A successful planned leg is recorded as nothing and that is not an error - the
        // follower reports every leg, and only the surprising ones are evidence.
        return new Response { Id = req.Id, Ok = true, Result = Results.Ok };
    }

    // ---- per-client pathfind counters (vnavmesh's Nav.PathfindInProgress is per-process) ----

    private readonly Dictionary<int, int> _pathfindActive = [];
    private readonly object _pathfindLock = new();

    private void PathfindEnter(int clientId)
    {
        lock (_pathfindLock)
            _pathfindActive[clientId] = _pathfindActive.GetValueOrDefault(clientId) + 1;
    }

    private void PathfindExit(int clientId)
    {
        lock (_pathfindLock)
        {
            var n = _pathfindActive.GetValueOrDefault(clientId) - 1;
            if (n <= 0)
                _pathfindActive.Remove(clientId);
            else
                _pathfindActive[clientId] = n;
        }
    }

    private int PathfindQueued(int clientId)
    {
        lock (_pathfindLock)
            return _pathfindActive.GetValueOrDefault(clientId);
    }
}
