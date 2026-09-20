using Mnemosyne.Client;
using Mnemosyne.Core;
using Mnemosyne.Protocol;

namespace Mnemosyne.Cli;

// Phase 1 go/no-go: exercise every server op the 17 consumer-facing vnavmesh gates rely on
// and assert sane answers. Green here means Ariadne can claim the vnavmesh.* names.
public static class Conformance
{
    private static int _pass, _fail, _skip;

    public static async Task<int> RunAsync(string zoneHint)
    {
        var entry = MeshCache.Enumerate().Where(e => e.IsSupported && e.Key.Contains(zoneHint, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => new FileInfo(e.Path).Length).FirstOrDefault();
        if (entry == null)
        {
            Console.WriteLine($"no current-version zone matches '{zoneHint}'");
            return 1;
        }

        using var client = new MnemosyneClient();
        try
        {
            await client.ConnectAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"cannot connect: {ex.Message} - is Mnemosyne.Service running?");
            return 1;
        }

        var hello = await client.HelloAsync();
        Check("hello", hello.Ok && hello.App == "mnemosyne", $"app={hello.App} protocol={hello.Protocol}");

        var zones = await client.ListZonesAsync();
        Check("listZones", zones.Ok && zones.Zones is { Count: > 0 }, $"{zones.Zones?.Count ?? 0} zones");

        var key = entry.Key;
        var status = await client.ZoneStatusAsync(key);
        Check("zoneStatus", status.Ok && status.Status == "cached", $"status={status.Status} progress={status.Progress}");
        Check("Nav.BuildProgress shape", status.Progress is -1 or >= 0 and <= 1, $"progress={status.Progress}");

        var mesh = await client.GetMeshAsync(key);
        Check("getMesh", mesh.Ok && File.Exists(mesh.Path), mesh.Path ?? mesh.Error ?? "");

        // sample two connected points to drive the query ops with realistic input
        var (from, to) = SampleConnectedPair(mesh.Path!);
        var mid = new[] { (from[0] + to[0]) / 2, (from[1] + to[1]) / 2, (from[2] + to[2]) / 2 };

        var path = await client.FindPathAsync(key, from, to);
        Check("findPath (Nav.Pathfind)", path.Ok && path.Waypoints is { Length: >= 2 },
            $"{path.Waypoints?.Length ?? 0} waypoints{(path.Partial ? " partial" : "")}");

        var tol = await client.SendAsync<FindPathResponse>(new Request
        {
            Op = "findPath", CacheKey = key, From = from, To = to, Fly = false, Tolerance = 15f,
        });
        Check("findPath tolerance (Nav.PathfindWithTolerance)", tol.Ok && tol.Waypoints is { Length: >= 2 },
            $"{tol.Waypoints?.Length ?? 0} waypoints");

        // Put the hazard on the route we just got, not on the straight line between the
        // endpoints - otherwise "avoid" trivially passes by never having been in the way.
        var onRoute = path.Waypoints is { Length: > 2 } wp ? wp[wp.Length / 2] : mid;
        const float avoidRadius = 15f;
        var avoid = await client.SendAsync<FindPathResponse>(new Request
        {
            Op = "findPath", CacheKey = key, From = from, To = to, Fly = false,
            AvoidCenter = onRoute, AvoidRadius = avoidRadius,
        });
        var baseMin = MinDistanceXZ(path.Waypoints, onRoute);
        var avoidMin = MinDistanceXZ(avoid.Waypoints, onRoute);
        Check("findPath avoid (Nav.PathfindAvoid)", avoid.Ok && avoidMin > baseMin,
            $"closest waypoint {baseMin:f1}m -> {avoidMin:f1}m from a {avoidRadius:f0}m hazard "
            + $"({path.Waypoints?.Length ?? 0} -> {avoid.Waypoints?.Length ?? 0} waypoints)");

        // ---- phase 2: classified answers -------------------------------------------------
        Check("findPath result (ok)", path.Result == "ok", $"result={path.Result ?? "<none>"}");

        // Guessing an off-mesh point by eye is how this check first lied to itself: +12 y
        // over open ground turned out to sit on a cliff shelf. Ask the server, using the
        // same snap extents the pathfinder uses, and take the first height it disowns.
        var highGoal = await FindOffMeshAbove(client, key, to);
        if (highGoal != null)
        {
            var offGoal = await client.SendAsync<FindPathResponse>(new Request
            {
                Op = "findPath", CacheKey = key, From = from, To = highGoal, Fly = false,
            });
            Check("findPath targetOffMesh + nearest",
                offGoal is { Ok: false, Result: "targetOffMesh", Nearest.Length: 3 },
                $"result={offGoal.Result ?? "<none>"} nearest={Fmt(offGoal.Nearest)}");
        }
        else
        {
            Skip("findPath targetOffMesh + nearest", "no off-mesh height near the goal");
        }

        var highStart = await FindOffMeshAbove(client, key, from);
        if (highStart != null)
        {
            var offStart = await client.SendAsync<FindPathResponse>(new Request
            {
                Op = "findPath", CacheKey = key, From = highStart, To = to, Fly = false,
            });
            Check("findPath startOffMesh + nearest",
                offStart is { Ok: false, Result: "startOffMesh", Nearest.Length: 3 },
                $"result={offStart.Result ?? "<none>"} nearest={Fmt(offStart.Nearest)}");
        }
        else
        {
            Skip("findPath startOffMesh + nearest", "no off-mesh height near the start");
        }

        // a disconnected on-mesh point must be named, not answered with a bare "no"
        if (FindDisconnectedPoint(mesh.Path!, from) is { } island)
        {
            var cut = await client.SendAsync<FindPathResponse>(new Request
            {
                Op = "findPath", CacheKey = key, From = from, To = island, Fly = false,
            });
            Check("findPath disconnected is classified",
                cut.Result is "noRouteOnMesh" or "unreachable",
                $"result={cut.Result ?? "<none>"} ok={cut.Ok} partial={cut.Partial}");
        }
        else
        {
            Skip("findPath disconnected is classified", "no disconnected component in this zone");
        }

        // The Phase 0 promise: a cold build must never hold a request. Answer must be
        // immediate and say "meshNotReady", with zoneStatus reporting the build.
        if (FindUnbuiltZone() is { } coldKey)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var cold = await client.SendAsync<FindPathResponse>(new Request
            {
                Op = "findPath", CacheKey = coldKey, From = from, To = to, Fly = false,
            });
            sw.Stop();
            Check("findPath on a cold zone answers immediately",
                cold is { Ok: false, Result: "meshNotReady" } && sw.ElapsedMilliseconds < 2000,
                $"result={cold.Result ?? "<none>"} in {sw.ElapsedMilliseconds} ms ({coldKey})");

            var building = await client.ZoneStatusAsync(coldKey);
            Check("zoneStatus reports building", building.Status == "building" && building.Progress >= 0,
                $"status={building.Status} progress={building.Progress:f2}");
        }
        else
        {
            Skip("findPath on a cold zone answers immediately", "every buildable zone is already built");
            Skip("zoneStatus reports building", "no cold zone available");
        }

        var near = await client.SendAsync<PointResponse>(new Request
        {
            Op = "nearestPoint", CacheKey = key, Point = to, HalfExtentXZ = 5, HalfExtentY = 5,
        });
        Check("nearestPoint (Query.Mesh.NearestPoint)", near.Ok && near.Found && near.Point is { Length: 3 },
            near.Found ? $"({near.Point![0]:f1}, {near.Point[1]:f1}, {near.Point[2]:f1})" : "not found");

        var reach = await client.SendAsync<PointResponse>(new Request
        {
            Op = "nearestPoint", CacheKey = key, Point = to, HalfExtentXZ = 5, HalfExtentY = 5, ReachableOnly = true,
        });
        Check("nearestPoint reachable (Query.Mesh.NearestPointReachable)", reach.Ok && reach.Found, reach.Found ? "found" : "not found");

        var onMesh = await client.SendAsync<OnMeshResponse>(new Request
        {
            Op = "isPointOnMesh", CacheKey = key, Point = to, HalfExtentY = 5,
        });
        Check("isPointOnMesh (Query.Mesh.IsPointOnMesh)", onMesh.Ok && onMesh.OnMesh, $"onMesh={onMesh.OnMesh}");

        // a point well above the goal must resolve down onto the same floor
        var above = new[] { to[0], to[1] + 30f, to[2] };
        var floor = await client.SendAsync<PointResponse>(new Request
        {
            Op = "pointOnFloor", CacheKey = key, Point = above, HalfExtentXZ = 5,
        });
        var floorSane = floor.Ok && floor.Found && floor.Point is { Length: 3 } && Math.Abs(floor.Point[1] - to[1]) < 35;
        Check("pointOnFloor (Query.Mesh.PointOnFloor)", floorSane,
            floor.Found ? $"y={floor.Point![1]:f1} (goal y={to[1]:f1})" : "not found");

        var bmp = await client.SendAsync<BitmapResponse>(new Request
        {
            Op = "buildBitmap", CacheKey = key, StartingPoints = [from], Filename = "conformance.bmp", PixelSize = 1f,
        });
        Check("buildBitmap (Nav.BuildBitmap)", bmp.Ok && File.Exists(bmp.Path), bmp.Path ?? bmp.Error ?? "");

        var bmpBounded = await client.SendAsync<BitmapResponse>(new Request
        {
            Op = "buildBitmap", CacheKey = key, StartingPoints = [from], Filename = "conformance_bounded.bmp",
            PixelSize = 1f, MinBounds = [from[0] - 100, from[1] - 50, from[2] - 100],
            MaxBounds = [from[0] + 100, from[1] + 50, from[2] + 100],
        });
        Check("buildBitmap bounded (Nav.BuildBitmapBounded)", bmpBounded.Ok && File.Exists(bmpBounded.Path),
            bmpBounded.Path ?? bmpBounded.Error ?? "");

        // Phase 4: served walk routes must keep clear of geometry. Recast's raw route hugs
        // walls (0.00m clearance measured on 7 of 8 waypoints in Ul'dah), which is what a
        // follower snags on, so the server pads them before handing them over.
        var padCheck = await client.SendAsync<FindPathResponse>(new Request
        {
            Op = "findPath", CacheKey = key, From = from, To = to, Fly = false,
        });
        var raw = await client.SendAsync<FindPathResponse>(new Request
        {
            Op = "findPath", CacheKey = key, From = from, To = to, Fly = false, Clearance = 0,
        });
        var padMin = InteriorClearance(mesh.Path!, padCheck.Waypoints);
        var rawMin = InteriorClearance(mesh.Path!, raw.Waypoints);
        Check("served routes are padded off obstacles", padMin >= rawMin,
            $"interior clearance {rawMin:f2}m unpadded -> {padMin:f2}m padded");
        Check("clearance:0 opts out", raw.Ok && raw.Waypoints is { Length: >= 2 },
            $"{raw.Waypoints?.Length ?? 0} waypoints, min clearance {rawMin:f2}m");

        // ---- phase 3: feedback channels --------------------------------------------------

        // reportTraversal: "I got there where you said I couldn't" is an off-mesh-link
        // candidate, and repeats of the same traversal must merge rather than pile up.
        // The store persists across runs by design, so measure deltas rather than absolutes -
        // an earlier run's evidence is the feature working, not a failure.
        var storeBefore = EvidenceStore.Load(key);
        var evidenceBefore = storeBefore.Count;
        var linkFrom = new[] { from[0], from[1], from[2] };
        var linkTo = new[] { from[0] + 3, from[1] + 2, from[2] };
        var countBefore = storeBefore.LinkCandidates.FirstOrDefault(c => c.Note == "conformance")?.Count ?? 0;
        for (int i = 0; i < 3; ++i)
        {
            await client.SendAsync<Response>(new Request
            {
                Op = "reportTraversal", CacheKey = key, From = linkFrom, To = linkTo,
                Mode = "direct", Success = true, Note = "conformance",
            });
        }
        var evidence = EvidenceStore.Load(key);
        var candidate = evidence.LinkCandidates.FirstOrDefault(c => c.Note == "conformance");
        Check("reportTraversal records a link candidate", candidate != null,
            $"{evidence.LinkCandidates.Count} link candidates, {evidence.BlockCandidates.Count} block candidates");
        var merged = candidate != null && candidate.Count == countBefore + 3 && evidence.Count <= evidenceBefore + 1;
        Check("reportTraversal merges repeats", merged,
            $"seen {countBefore} -> {candidate?.Count ?? 0} (3 reports), store grew by {evidence.Count - evidenceBefore}");

        // a successful planned leg teaches nothing and must not be stored
        await client.SendAsync<Response>(new Request
        {
            Op = "reportTraversal", CacheKey = key, From = linkFrom, To = to, Mode = "walk", Success = true,
        });
        Check("reportTraversal ignores unsurprising legs", EvidenceStore.Load(key).Count == evidence.Count,
            $"store still {evidence.Count}");

        // buildZone: a capture must be acked immediately, never held for the build
        var capture = new SceneCaptureDto
        {
            CacheKey = key, TerritoryId = 1, Terrains = ["bg/ffxiv/sea_s1/fld/s1f6/collision"],
        };
        var swBuild = System.Diagnostics.Stopwatch.StartNew();
        var buildAck = await client.SendAsync<Response>(new Request
        {
            Op = "buildZone", CacheKey = key, Scene = capture,
        });
        swBuild.Stop();
        Check("buildZone acks immediately", buildAck.Ok && swBuild.ElapsedMilliseconds < 2000,
            $"ok={buildAck.Ok} result={buildAck.Result ?? "<none>"} in {swBuild.ElapsedMilliseconds} ms");

        var emptyCapture = await client.SendAsync<Response>(new Request
        {
            Op = "buildZone", CacheKey = key, Scene = new SceneCaptureDto { CacheKey = key },
        });
        Check("buildZone rejects an empty capture", !emptyCapture.Ok, emptyCapture.Error ?? "accepted it");

        // Overrides baked into served files. This writes into the user's real override store,
        // so the previous contents are restored before we leave, pass or fail.
        var savedOverrides = OverrideStore.Load(key);
        try
        {
            var blockAt = path.Waypoints![path.Waypoints.Length / 2];
            OverrideStore.Save(key, new ZoneOverrides
            {
                FlagEdits = [new OverrideShape
                {
                    Kind = "sphere", Center = blockAt, Extent = [12, 12, 12], Block = true, Note = "conformance",
                }],
            });

            var baked = await client.GetMeshAsync(key);
            var isBaked = baked.Ok && baked.Path?.Contains("served", StringComparison.OrdinalIgnoreCase) == true;
            Check("getMesh serves a baked file when overrides exist", isBaked, baked.Path ?? baked.Error ?? "");

            // and the bake must mean something: the blocked polys have to be gone from the
            // served file, not merely present in the override JSON
            if (isBaked && File.Exists(baked.Path))
            {
                var bakedMesh = MeshCache.Load(baked.Path!);
                var bakedPf = new MeshPathfinder(bakedMesh.Mesh);
                var start = new System.Numerics.Vector3(from[0], from[1], from[2]);
                var goal = new System.Numerics.Vector3(to[0], to[1], to[2]);
                var blocked = new System.Numerics.Vector3(blockAt[0], blockAt[1], blockAt[2]);
                var route = bakedPf.FindWalkPath(start, goal);
                var clearance = route == null ? float.MaxValue
                    : route.Waypoints.Min(w => System.Numerics.Vector3.Distance(w, blocked));
                Check("baked mesh actually avoids the blocked region", clearance > 1f,
                    route == null ? "no route at all (fully blocked)" : $"closest waypoint {clearance:f1}m from a 12m block");
            }
            else
            {
                Skip("baked mesh actually avoids the blocked region", "no baked file to inspect");
            }
        }
        finally
        {
            OverrideStore.Save(key, savedOverrides);
            var servedCopy = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mnemosyne", "served", key + ".navmesh");
            if (File.Exists(servedCopy))
                File.Delete(servedCopy);
        }

        // fleet: two clients push distinct state and both must survive
        using var second = new MnemosyneClient();
        await second.ConnectAsync();
        await client.UpdateGameStateAsync(key, 1, [1, 2, 3], 0, false);
        await second.UpdateGameStateAsync(key, 1, [40, 50, 60], 1, true);
        var fleet = await client.GetGameStateAsync();
        Check("fleet getGameState", fleet.Ok && fleet.Players is { Count: >= 2 },
            $"{fleet.Players?.Count ?? 0} players tracked");
        Check("fleet legacy fields", fleet.Present && fleet.Pos is { Length: 3 }, "flat fields mirror latest");

        Console.WriteLine();
        Console.WriteLine($"conformance: {_pass} passed, {_fail} failed{(_skip > 0 ? $", {_skip} skipped" : "")}");
        return _fail == 0 ? 0 : 1;
    }

    private static float MinDistanceXZ(float[][]? waypoints, float[] center)
    {
        var best = float.MaxValue;
        foreach (var w in waypoints ?? [])
        {
            if (w.Length < 3)
                continue;
            var dx = w[0] - center[0];
            var dz = w[2] - center[2];
            best = Math.Min(best, MathF.Sqrt(dx * dx + dz * dz));
        }
        return best == float.MaxValue ? -1 : best;
    }

    /// <summary>A height above <paramref name="ground"/> that the server agrees is off the
    /// mesh under the pathfinder's own 5 y snap extent, and still inside the 20 y radius the
    /// classifier searches for a `nearest`. Null when the column is solid mesh all the way.</summary>
    private static async Task<float[]?> FindOffMeshAbove(MnemosyneClient client, string key, float[] ground)
    {
        foreach (var dy in new[] { 12f, 15f, 18f })
        {
            var probe = new[] { ground[0], ground[1] + dy, ground[2] };
            var hit = await client.SendAsync<PointResponse>(new Request
            {
                Op = "nearestPoint", CacheKey = key, Point = probe, HalfExtentXZ = 5, HalfExtentY = 5,
            });
            if (hit is { Ok: true, Found: false })
                return probe;
        }
        return null;
    }

    /// <summary>Worst clearance among a route's interior waypoints. Endpoints are excluded:
    /// the server never moves them, so a goal against a wall would swamp the measurement.</summary>
    private static float InteriorClearance(string meshPath, float[][]? waypoints)
    {
        if (waypoints is not { Length: > 2 })
            return -1;
        var navmesh = MeshCache.Load(meshPath);
        var query = new DotRecast.Detour.DtNavMeshQuery(navmesh.Mesh);
        var filter = new DotRecast.Detour.DtQueryDefaultFilter();
        var min = float.MaxValue;
        for (int i = 1; i < waypoints.Length - 1; ++i)
        {
            var w = waypoints[i];
            if (w.Length < 3)
                continue;
            var c = PathPadding.Clearance(query, filter, new System.Numerics.Vector3(w[0], w[1], w[2]));
            if (c >= 0)
                min = Math.Min(min, c);
        }
        return min == float.MaxValue ? -1 : min;
    }

    private static string Fmt(float[]? p) => p is { Length: 3 } ? $"({p[0]:f1}, {p[1]:f1}, {p[2]:f1})" : "<none>";

    private static void Skip(string name, string why)
    {
        ++_skip;
        Console.WriteLine($"  [SKIP] {name,-48} {why}");
    }

    /// <summary>A point on the mesh that the seed cannot walk to, or null if the zone is one
    /// connected piece. Used to prove a disconnect gets named rather than shrugged at.</summary>
    private static float[]? FindDisconnectedPoint(string meshPath, float[] from)
    {
        var mesh = MeshCache.Load(meshPath).Mesh;
        var pf = new MeshPathfinder(mesh);
        var start = new System.Numerics.Vector3(from[0], from[1], from[2]);
        var centers = PolyCenters(mesh);
        var stride = Math.Max(1, centers.Count / 300);
        for (int i = 0; i < centers.Count; i += stride)
        {
            var c = centers[i];
            if (pf.FindWalkPath(start, c) is null or { Partial: true })
                return [c.X, c.Y, c.Z];
        }
        return null;
    }

    /// <summary>A zone Mnemosyne could build but hasn't, for the cold-build test.</summary>
    private static string? FindUnbuiltZone()
    {
        var built = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mnemosyne", "built");
        var cached = MeshCache.Enumerate().Where(e => e.IsSupported).Select(e => e.Key).ToList();
        foreach (var (bgKey, info) in ZoneNames.All)
        {
            if (info.Bg is not { Length: > 0 })
                continue;
            if (cached.Any(k => k.StartsWith(bgKey, StringComparison.OrdinalIgnoreCase)))
                continue;
            if (File.Exists(Path.Combine(built, bgKey + ".navmesh")))
                continue;
            return bgKey; // the bare bg key doubles as a cacheKey; the server strips at "__"
        }
        return null;
    }

    private static void Check(string name, bool ok, string detail)
    {
        if (ok)
            ++_pass;
        else
            ++_fail;
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name,-48} {detail}");
    }

    private static List<System.Numerics.Vector3> PolyCenters(DotRecast.Detour.DtNavMesh mesh)
    {
        var centers = new List<System.Numerics.Vector3>();
        for (int i = 0; i < mesh.GetMaxTiles(); ++i)
        {
            var tile = mesh.GetTile(i);
            if (tile?.data?.header == null)
                continue;
            for (int p = 0; p < tile.data.header.polyCount; ++p)
            {
                var poly = tile.data.polys[p];
                if (poly.GetPolyType() != 0)
                    continue;
                var sum = System.Numerics.Vector3.Zero;
                for (int v = 0; v < poly.vertCount; ++v)
                {
                    int vi = poly.verts[v] * 3;
                    sum += new System.Numerics.Vector3(tile.data.verts[vi], tile.data.verts[vi + 1], tile.data.verts[vi + 2]);
                }
                centers.Add(sum / poly.vertCount);
            }
        }
        return centers;
    }

    // two far-apart points on the largest connected walkable component
    private static (float[] From, float[] To) SampleConnectedPair(string meshPath)
    {
        var mesh = MeshCache.Load(meshPath).Mesh;
        var pf = new MeshPathfinder(mesh);
        var centers = PolyCenters(mesh);
        // Pathfinding to every poly to find the farthest reachable one is O(polys) full
        // searches - minutes on an outdoor zone. Sort by distance instead and probe a few
        // dozen from the far end; the first that connects is far enough to exercise a real
        // route, which is all this harness needs.
        var seed = centers[centers.Count / 2];
        var byDistance = centers.OrderByDescending(c => System.Numerics.Vector3.DistanceSquared(c, seed)).ToList();
        var target = centers[0];
        for (int i = 0; i < byDistance.Count && i < 40; ++i)
        {
            var candidate = byDistance[i * Math.Max(1, byDistance.Count / 400) % byDistance.Count];
            if (pf.FindWalkPath(seed, candidate, []) is { Partial: false })
            {
                target = candidate;
                break;
            }
        }
        return ([seed.X, seed.Y, seed.Z], [target.X, target.Y, target.Z]);
    }
}
