using DotRecast.Detour;
using Mnemosyne.Core;
using Mnemosyne.Protocol;

// conformance (roadmap phase 1): every op behind the 17 consumer gates - the flip's go/no-go
if (args.Length > 0 && args[0] == "conformance")
    return await Mnemosyne.Cli.Conformance.RunAsync(args.Length > 1 ? args[1] : "s1t1");

// ipc-test mode (milestone 5): exercise the named-pipe service end to end.
// buildzone-test (roadmap phase 3): a real capture through the pipe, build and all
if (args.Length > 0 && args[0] == "buildzone-test")
    return await Mnemosyne.Cli.BuildZoneTest.RunAsync(args.Length > 1 ? args[1] : null);

// doors (roadmap phase 3): find doors the mesh has sealed shut
if (args.Length > 0 && args[0] == "doors")
    return Mnemosyne.Cli.DoorAudit.Run(args);

// padcheck (roadmap phase 4): did padding or a recorded obstacle distort any route?
if (args.Length > 0 && args[0] == "padcheck")
    return Mnemosyne.Cli.PadCheck.Run(args);

// solids (roadmap phase 4): obstacles the navmesh runs straight through
if (args.Length > 0 && args[0] == "solids")
    return Mnemosyne.Cli.Solids.Run(args);

// snags (roadmap phase 4): find the chokepoints a follower will catch on
if (args.Length > 0 && args[0] == "snags")
    return Mnemosyne.Cli.SnagSweep.Run(args);

// route (roadmap phase 4): why a route that looks fine gets you stuck
if (args.Length > 0 && args[0] == "route")
    return Mnemosyne.Cli.RouteClearance.Run(args);

// clearmap (roadmap phase 4): how much room a route has, cell by cell
if (args.Length > 0 && args[0] == "clearmap")
    return Mnemosyne.Cli.ReachMap.RunClearance(args);

// layout: event objects (with EObj ids) and matching shared groups/models in a zone
if (args.Length > 0 && args[0] == "layout")
    return Mnemosyne.Cli.LayoutList.Run(args);

// components: the mesh's disconnected islands and the closest gaps between them
if (args.Length > 0 && args[0] == "components")
    return Mnemosyne.Cli.Components.Run(args);

// reachmap (roadmap phase 3): a local picture of what you can and cannot walk to
if (args.Length > 0 && args[0] == "reachmap")
    return Mnemosyne.Cli.ReachMap.Run(args);

// reachcells: the reachableCells op's own answer, rendered — compare with reachmap
if (args.Length > 0 && args[0] == "reachcells")
    return Mnemosyne.Cli.ReachCells.Run(args);

// customizations: has vnavmesh changed its per-territory customizations since we vendored?
if (args.Length > 0 && args[0] == "customizations")
    return Mnemosyne.Cli.CustomizationDrift.Run();

// territories: what the game sheet says, versus Mnemosyne bundled snapshot
if (args.Length > 0 && args[0] == "territories")
    return Mnemosyne.Cli.Territories.Run(args);

// flyprobe (roadmap phase 4): what the *volume* says, which is what flying reads
if (args.Length > 0 && args[0] == "flyprobe")
    return Mnemosyne.Cli.FlyProbe.Run(args);

// probe (roadmap phase 3): is the mesh solid where the world is open?
if (args.Length > 0 && args[0] == "probe")
    return Mnemosyne.Cli.Probe.Run(args);

// findpath: one path question, timed, in process — the harness for the bounded-search work
if (args.Length > 0 && args[0] == "findpath")
    return Mnemosyne.Cli.FindPath.Run(args);

// capture-test (roadmap phase 3): push an offline scene through buildZone's wire path
if (args.Length > 0 && args[0] == "capture-test")
    return Mnemosyne.Cli.CaptureTest.Run(args.Length > 1 ? args[1] : "s1t1");

if (args.Length > 0 && args[0] == "ipc-test")
    return await IpcTest();

// bench mode (milestone 9): compare brotli load vs fast-cache load
if (args.Length > 0 && args[0] == "bench")
{
    var target = args.Length > 1 ? args[1] : "x6f2";
    var entry = MeshCache.Enumerate().Where(e => e.IsSupported && e.Key.Contains(target, StringComparison.OrdinalIgnoreCase))
        .OrderByDescending(e => new FileInfo(e.Path).Length).FirstOrDefault();
    if (entry == null)
    {
        Console.WriteLine($"no supported zone matches '{target}'");
        return 1;
    }
    Console.WriteLine($"zone: {entry.Key} ({new FileInfo(entry.Path).Length / 1024.0 / 1024.0:f1} MB brotli)");

    var benchSw = System.Diagnostics.Stopwatch.StartNew();
    var slow = MeshCache.Load(entry.Path);
    Console.WriteLine($"brotli load:                {benchSw.ElapsedMilliseconds} ms (volume: {slow.Volume != null})");

    var fastFile = Path.Combine(FastCache.Directory, entry.Key + ".mnav");
    if (File.Exists(fastFile))
        File.Delete(fastFile);
    benchSw.Restart();
    FastCache.Load(entry.Path, withVolume: true);
    Console.WriteLine($"cold load + bake:           {benchSw.ElapsedMilliseconds} ms -> {new FileInfo(fastFile).Length / 1024.0 / 1024.0:f1} MB mnav");

    benchSw.Restart();
    var warm = FastCache.Load(entry.Path, withVolume: true);
    Console.WriteLine($"fast load (with volume):    {benchSw.ElapsedMilliseconds} ms (fromFast: {warm.FromFastCache})");

    benchSw.Restart();
    var meshOnly = FastCache.Load(entry.Path, withVolume: false);
    int meshOnlyTiles = Enumerable.Range(0, meshOnly.Mesh.GetMaxTiles()).Count(i => meshOnly.Mesh.GetTile(i)?.data?.header != null);
    Console.WriteLine($"fast load (mesh only):      {benchSw.Elapsed.TotalMilliseconds:f1} ms (hasVolume: {meshOnly.HasVolume}, tiles: {meshOnlyTiles}, fromFast: {meshOnly.FromFastCache})");

    if (meshOnly.HasVolume)
    {
        benchSw.Restart();
        FastCache.LoadVolume(meshOnly.FastPath);
        Console.WriteLine($"lazy volume upgrade:        {benchSw.ElapsedMilliseconds} ms");
    }
    return 0;
}

// build (milestone 6): build a zone's navmesh from game files, outside the game process,
// and verify it against vnavmesh's cached build of the same zone
if (args.Length > 0 && args[0] == "build")
{
    var target = args.Length > 1 ? args[1] : "s1t1";
    // Resolve through the shared lookup so the built store counts as a reference too.
    // Gridania's vnavmesh cache files are all stale-version, so without that fallback this
    // command refuses to rebuild exactly the zones that most need rebuilding.
    if (Mnemosyne.Cli.Probe.ResolveZoneOrReport(target) is not { } entry)
        return 1;
    if (ZoneNames.Lookup(entry.Key)?.Bg is not { Length: > 0 } bgPath)
    {
        Console.WriteLine("no bg path known for zone");
        return 1;
    }
    var launcherCfg = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XIVLauncher", "launcherConfigV3.json");
    var gameRoot = System.Text.Json.JsonDocument.Parse(File.ReadAllText(launcherCfg)).RootElement.GetProperty("GamePath").GetString()!;
    var sqpack = Path.Combine(gameRoot, "game", "sqpack");

    Console.WriteLine($"building '{entry.Key}' from {bgPath} ...");
    var reference = MeshCache.Load(entry.Path);
    var buildSw = System.Diagnostics.Stopwatch.StartNew();
    var built = Mnemosyne.Builder.ZoneBuilder.Build(sqpack, bgPath, reference.Volume != null,
        (done, total) => { if (done % 64 == 0 || done == total) Console.WriteLine($"  tile {done}/{total}"); });
    buildSw.Stop();

    (int Tiles, int Polys, int Verts) Stats(DtNavMesh m)
    {
        int t = 0, p = 0, v = 0;
        for (int i = 0; i < m.GetMaxTiles(); ++i)
        {
            var tile = m.GetTile(i);
            if (tile?.data?.header == null)
                continue;
            ++t; p += tile.data.header.polyCount; v += tile.data.header.vertCount;
        }
        return (t, p, v);
    }
    var bs = Stats(built.Mesh);
    var rs = Stats(reference.Mesh);
    Console.WriteLine($"built in {buildSw.Elapsed.TotalSeconds:f1} s: tiles {bs.Tiles} (ref {rs.Tiles}), polys {bs.Polys} (ref {rs.Polys}), verts {bs.Verts} (ref {rs.Verts}), volume: {built.Volume != null} (ref {reference.Volume != null})");

    // sample walk path on both meshes: pick two far-apart points guaranteed connected on the
    // reference mesh (flood from a central poly), so the comparison tests real routing
    var pfBuilt = new MeshPathfinder(built.Mesh);
    var pfRef = new MeshPathfinder(reference.Mesh);
    float PathLen(List<System.Numerics.Vector3> wps) => wps.Skip(1).Zip(wps, System.Numerics.Vector3.Distance).Sum();

    System.Numerics.Vector3 PolyCenter(DtMeshData data, DtPoly poly)
    {
        var sum = System.Numerics.Vector3.Zero;
        for (int i = 0; i < poly.vertCount; ++i)
        {
            int vi = poly.verts[i] * 3;
            sum += new System.Numerics.Vector3(data.verts[vi], data.verts[vi + 1], data.verts[vi + 2]);
        }
        return sum / poly.vertCount;
    }
    var centers = new List<(long Ref, System.Numerics.Vector3 Pos)>();
    for (int i = 0; i < reference.Mesh.GetMaxTiles(); ++i)
    {
        var tile = reference.Mesh.GetTile(i);
        if (tile?.data?.header == null)
            continue;
        long refBase = reference.Mesh.GetPolyRefBase(tile);
        for (int p = 0; p < tile.data.header.polyCount; ++p)
            if (tile.data.polys[p].GetPolyType() == 0)
                centers.Add((refBase | (uint)p, PolyCenter(tile.data, tile.data.polys[p])));
    }
    var centroid = centers.Aggregate(System.Numerics.Vector3.Zero, (acc, c) => acc + c.Pos) / centers.Count;
    var seedPoly = centers.OrderBy(c => System.Numerics.Vector3.DistanceSquared(c.Pos, centroid)).First();
    var refQuery = new DtNavMeshQuery(reference.Mesh);
    var reachRefs = new List<long>();
    var reachParents = new List<long>();
    var reachCosts = new List<float>();
    refQuery.FindPolysAroundCircle(seedPoly.Ref, new DotRecast.Core.Numerics.RcVec3f(seedPoly.Pos.X, seedPoly.Pos.Y, seedPoly.Pos.Z), 1e9f, new DtQueryDefaultFilter(), ref reachRefs, ref reachParents, ref reachCosts);
    var reachable = new HashSet<long>(reachRefs) { seedPoly.Ref };
    var connected = centers.Where(c => reachable.Contains(c.Ref)).ToList();
    var f3 = seedPoly.Pos;
    var t3 = connected.OrderByDescending(c => System.Numerics.Vector3.DistanceSquared(c.Pos, f3)).First().Pos;

    var pRef = pfRef.FindWalkPath(f3, t3);
    var pBuilt = pfBuilt.FindWalkPath(f3, t3);
    Console.WriteLine($"path (reference): {(pRef == null ? "none" : $"{pRef.Waypoints.Count} wps, {PathLen(pRef.Waypoints):f0}m{(pRef.Partial ? " partial" : "")}")}");
    Console.WriteLine($"path (built):     {(pBuilt == null ? "none" : $"{pBuilt.Waypoints.Count} wps, {PathLen(pBuilt.Waypoints):f0}m{(pBuilt.Partial ? " partial" : "")}")}");

    var builtDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mnemosyne", "built");
    Directory.CreateDirectory(builtDir);
    var outPath = Path.Combine(builtDir, entry.Key + ".navmesh");
    using (var stream = File.Create(outPath))
    using (var writer = new BinaryWriter(stream))
        built.Serialize(writer);
    Console.WriteLine($"saved: {outPath} ({new FileInfo(outPath).Length / 1024.0 / 1024.0:f1} MB)");
    Console.WriteLine(bs.Polys > rs.Polys / 2 && pBuilt != null ? "BUILD TEST PASSED (sane vs reference)" : "BUILD TEST: check results above");
    return 0;
}

// override-test (milestone 8a): block a spot mid-path via a sidecar override and verify
// the service's findPath detours around it
if (args.Length > 0 && args[0] == "override-test")
{
    using var otClient = new Mnemosyne.Client.MnemosyneClient();
    await otClient.ConnectAsync();
    var otZones = await otClient.ListZonesAsync();
    var hello2 = await otClient.HelloAsync();
    var otLimsa = otZones.Zones!.Where(z => z.Version == hello2.MeshVersion).First(z => z.CacheKey.Contains("s1t1"));
    var otMesh = await otClient.GetMeshAsync(otLimsa.CacheKey);
    var (oFrom, oTo) = SamplePoints(otMesh.Path!);

    var before = await otClient.FindPathAsync(otLimsa.CacheKey, oFrom, oTo);
    if (!before.Ok)
    {
        Console.WriteLine($"OVERRIDE TEST FAILED: baseline path failed: {before.Error}");
        return 1;
    }
    float LenOf(float[][] wps) => wps.Skip(1).Zip(wps, (b, a) => MathF.Sqrt((b[0]-a[0])*(b[0]-a[0]) + (b[1]-a[1])*(b[1]-a[1]) + (b[2]-a[2])*(b[2]-a[2]))).Sum();
    var beforeLen = LenOf(before.Waypoints!);
    Console.WriteLine($"baseline: {before.Waypoints!.Length} waypoints, {beforeLen:f0}m{(before.Partial ? " (partial)" : "")}");

    // block a sphere around the middle waypoint
    var midWp = before.Waypoints[before.Waypoints.Length / 2];
    var overrides = new ZoneOverrides();
    overrides.FlagEdits.Add(new() { Kind = "sphere", Center = midWp, Extent = [12, 12, 12], Block = true, Note = "override-test" });
    OverrideStore.Save(otLimsa.CacheKey, overrides);
    await otClient.NotifyMeshBuiltAsync(otLimsa.CacheKey, otMesh.Path!); // evict so the service reloads with overrides

    var after = await otClient.FindPathAsync(otLimsa.CacheKey, oFrom, oTo);
    var afterLen = after is { Ok: true, Waypoints: not null } ? LenOf(after.Waypoints) : 0;
    Console.WriteLine(after.Ok
        ? $"blocked:  {after.Waypoints!.Length} waypoints, {afterLen:f0}m{(after.Partial ? " (partial)" : "")}"
        : $"blocked:  no path ({after.Error})");

    // cleanup and restore
    OverrideStore.Save(otLimsa.CacheKey, new ZoneOverrides());
    await otClient.NotifyMeshBuiltAsync(otLimsa.CacheKey, otMesh.Path!);

    bool changed = !after.Ok || after.Partial != before.Partial || MathF.Abs(afterLen - beforeLen) > 5 || after.Waypoints!.Length != before.Waypoints.Length;
    Console.WriteLine(changed ? "OVERRIDE TEST PASSED (path changed around the block)" : "OVERRIDE TEST FAILED (path unchanged)");
    return changed ? 0 : 1;
}

// plan-test (milestone 11): run the multi-modal planner and report each stage
if (args.Length > 0 && args[0] == "plan-test")
{
    var target = args.Length > 1 ? args[1] : "x6f2";
    var entry = MeshCache.Enumerate().Where(e => e.IsSupported && e.Key.Contains(target, StringComparison.OrdinalIgnoreCase))
        .OrderByDescending(e => new FileInfo(e.Path).Length).FirstOrDefault();
    if (entry == null)
    {
        Console.WriteLine($"no zone matches '{target}'");
        return 1;
    }
    var loaded = FastCache.Load(entry.Path, withVolume: true);
    Console.WriteLine($"{entry.Key}: volume={loaded.Volume != null}");

    // endpoints: farthest pair on the largest connected walkable component
    var pf = new MeshPathfinder(loaded.Mesh);
    var pts = new List<(long Ref, System.Numerics.Vector3 Pos)>();
    for (int i = 0; i < loaded.Mesh.GetMaxTiles(); ++i)
    {
        var tile = loaded.Mesh.GetTile(i);
        if (tile?.data?.header == null)
            continue;
        long refBase = loaded.Mesh.GetPolyRefBase(tile);
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
            pts.Add((refBase | (uint)p, sum / poly.vertCount));
        }
    }
    var q = new DtNavMeshQuery(loaded.Mesh);
    var seedPt = pts[pts.Count / 2];
    var rr = new List<long>(); var rp = new List<long>(); var rc = new List<float>();
    q.FindPolysAroundCircle(seedPt.Ref, new DotRecast.Core.Numerics.RcVec3f(seedPt.Pos.X, seedPt.Pos.Y, seedPt.Pos.Z), 1e9f, new DtQueryDefaultFilter(), ref rr, ref rp, ref rc);
    var comp = new HashSet<long>(rr) { seedPt.Ref };
    var conn = pts.Where(c => comp.Contains(c.Ref)).ToList();
    var fromPt = seedPt.Pos;
    var toPt = conn.OrderByDescending(c => System.Numerics.Vector3.DistanceSquared(c.Pos, fromPt)).First().Pos;
    Console.WriteLine($"endpoints {System.Numerics.Vector3.Distance(fromPt, toPt):f0}m apart");

    var flightPf = loaded.Volume != null ? new FlightPathfinder(loaded.Volume) : null;
    var planner = new MultiModalPlanner(pf, flightPf);

    void RunPlan(string label, System.Numerics.Vector3 a, System.Numerics.Vector3 b)
    {
        var sw3 = System.Diagnostics.Stopwatch.StartNew();
        var plan = planner.Plan(a, b, 7.7f, 20f, []);
        sw3.Stop();
        Console.WriteLine($"--- {label} ({System.Numerics.Vector3.Distance(a, b):f0}m apart)");
        Console.WriteLine($"    air option: {(planner.AirReason.Length == 0 ? "accepted" : "rejected - " + planner.AirReason)}");
        if (plan == null)
        {
            Console.WriteLine("    no route at all");
            return;
        }
        Console.WriteLine($"    chosen: {plan.Summary}, {plan.TotalSeconds:f0}s total, planned in {sw3.ElapsedMilliseconds} ms");
        foreach (var leg in plan.Legs)
            Console.WriteLine($"      {leg.Mode,-4} {leg.Length,7:f0}m {leg.Seconds,6:f0}s  {leg.Waypoints.Count} waypoints");
    }

    RunPlan("long haul", fromPt, toPt);

    // sheltered goal: a connected point whose sky is blocked (indoors, cave, under an
    // overhang) - the planner should fly close then walk the last stretch in
    var sheltered = conn.FirstOrDefault(c =>
        System.Numerics.Vector3.Distance(c.Pos, fromPt) > 300 && !HasSky(flightPf, c.Pos));
    if (sheltered.Pos != default)
        RunPlan("sheltered goal", fromPt, sheltered.Pos);
    else
        Console.WriteLine("--- sheltered goal: none found in this zone's connected component");

    // both ends sheltered: the full walk -> mount -> fly -> land -> walk chain
    var shelteredStart = conn.FirstOrDefault(c =>
        !HasSky(flightPf, c.Pos) && sheltered.Pos != default
        && System.Numerics.Vector3.Distance(c.Pos, sheltered.Pos) > 500);
    if (shelteredStart.Pos != default && sheltered.Pos != default)
        RunPlan("sheltered at both ends", shelteredStart.Pos, sheltered.Pos);
    else
        Console.WriteLine("--- sheltered both ends: no suitable pair");

    static bool HasSky(FlightPathfinder? f, System.Numerics.Vector3 p)
    {
        if (f == null)
            return true;
        for (float h = 8; h <= 32; h += 4)
        {
            int leaf = f.Nav.LeafAt(p + new System.Numerics.Vector3(0, h, 0));
            if (leaf < 0 || f.Nav.Nodes[leaf].State != FlightNav.StateEmpty)
                return false;
        }
        return true;
    }
    return 0;
}

// fly-bench (milestone 9a): profile the voxel A* - steps vs per-step cost
if (args.Length > 0 && args[0] == "fly-bench")
{
    var target = args.Length > 1 ? args[1] : "x6f2";
    var entry = MeshCache.Enumerate().Where(e => e.IsSupported && e.Key.Contains(target, StringComparison.OrdinalIgnoreCase))
        .OrderByDescending(e => new FileInfo(e.Path).Length).FirstOrDefault();
    if (entry == null)
    {
        Console.WriteLine($"no zone matches '{target}'");
        return 1;
    }
    var loaded = FastCache.Load(entry.Path, withVolume: true);
    if (loaded.Volume == null)
    {
        Console.WriteLine("zone has no volume");
        return 1;
    }
    // hop endpoints: real walkable locations (poly centers of the ground mesh, connectivity-
    // verified), lifted into the air - corner tile verts sit in boundary-collider pockets
    // that nothing can reach and make every engine look broken
    System.Numerics.Vector3 FlyPolyCenter(DtMeshData data, DtPoly poly)
    {
        var sum = System.Numerics.Vector3.Zero;
        for (int i = 0; i < poly.vertCount; ++i)
        {
            int vi = poly.verts[i] * 3;
            sum += new System.Numerics.Vector3(data.verts[vi], data.verts[vi + 1], data.verts[vi + 2]);
        }
        return sum / poly.vertCount;
    }
    var flyCenters = new List<(long Ref, System.Numerics.Vector3 Pos)>();
    for (int i = 0; i < loaded.Mesh.GetMaxTiles(); ++i)
    {
        var tile = loaded.Mesh.GetTile(i);
        if (tile?.data?.header == null)
            continue;
        long refBase = loaded.Mesh.GetPolyRefBase(tile);
        for (int p = 0; p < tile.data.header.polyCount; ++p)
            if (tile.data.polys[p].GetPolyType() == 0)
                flyCenters.Add((refBase | (uint)p, FlyPolyCenter(tile.data, tile.data.polys[p])));
    }
    // find the LARGEST connected component - a centroid-nearest seed can land on an island
    var flyQuery = new DtNavMeshQuery(loaded.Mesh);
    var flyAssigned = new HashSet<long>();
    List<(long Ref, System.Numerics.Vector3 Pos)> flyConnected = [];
    (long Ref, System.Numerics.Vector3 Pos) flySeed = default;
    foreach (var seedCand in flyCenters.Where(c => !flyAssigned.Contains(c.Ref)).OrderBy(_ => 0).Take(2000))
    {
        if (flyAssigned.Contains(seedCand.Ref))
            continue;
        var flyReach = new List<long>();
        var flyParents = new List<long>();
        var flyCosts = new List<float>();
        flyQuery.FindPolysAroundCircle(seedCand.Ref, new DotRecast.Core.Numerics.RcVec3f(seedCand.Pos.X, seedCand.Pos.Y, seedCand.Pos.Z), 1e9f, new DtQueryDefaultFilter(), ref flyReach, ref flyParents, ref flyCosts);
        var comp = new HashSet<long>(flyReach) { seedCand.Ref };
        flyAssigned.UnionWith(comp);
        if (comp.Count > flyConnected.Count)
        {
            flyConnected = flyCenters.Where(c => comp.Contains(c.Ref)).ToList();
            flySeed = seedCand;
        }
        if (flyConnected.Count > flyCenters.Count / 2)
            break;
    }
    var lift = new System.Numerics.Vector3(0, 20, 0);
    var pa = flySeed.Pos + lift;
    var pb = flyConnected.OrderByDescending(c => System.Numerics.Vector3.DistanceSquared(c.Pos, flySeed.Pos)).First().Pos + lift;
    var mid = flyConnected.OrderBy(c => MathF.Abs(System.Numerics.Vector3.Distance(c.Pos, flySeed.Pos) - 800)).First().Pos + lift;

    var flightSw = System.Diagnostics.Stopwatch.StartNew();
    var flight = new FlightPathfinder(loaded.Volume);
    flightSw.Stop();
    Console.WriteLine($"octree: built in {flight.Nav.BuildMs:f0} ms, {flight.Nav.NodeCount} nodes ({flight.Nav.EmptyLeafCount} empty leaves)");

    var shortA = System.Numerics.Vector3.Lerp(pa, pb, 0.45f);
    var shortB = System.Numerics.Vector3.Lerp(pa, pb, 0.55f);
    foreach (var (from, to, label) in new[] { (shortA, shortB, "short hop"), (mid, pb, "medium hop"), (pa, pb, "full diagonal") })
    {
        var vp = new Navmesh.NavVolume.VoxelPathfind(loaded.Volume) { MaxSteps = 200_000 }; // same budget the service uses
        var fromVoxel = Navmesh.NavVolume.VoxelSearch.FindNearestEmptyVoxel(loaded.Volume, from, new System.Numerics.Vector3(3, 3, 3));
        var toVoxel = Navmesh.NavVolume.VoxelSearch.FindNearestEmptyVoxel(loaded.Volume, to, new System.Numerics.Vector3(3, 3, 3));
        if (fromVoxel == Navmesh.NavVolume.VoxelMap.InvalidVoxel || toVoxel == Navmesh.NavVolume.VoxelMap.InvalidVoxel)
        {
            Console.WriteLine($"{label}: no voxel");
            continue;
        }
        var sw2 = System.Diagnostics.Stopwatch.StartNew();
        var result = vp.FindPath(fromVoxel, toVoxel, from, to, false, false, CancellationToken.None);
        sw2.Stop();
        Console.WriteLine($"{label}: dist {(to - from).Length():f0}m");
        Console.WriteLine($"  old A*:      waypoints {result.Count}, {sw2.Elapsed.TotalSeconds:f2} s, explored {vp.NodeSpan.Length} nodes");

        sw2.Restart();
        var thetaPath = flight.FindPath(from, to, out var thetaPartial);
        sw2.Stop();
        float len = thetaPath.Skip(1).Zip(thetaPath, System.Numerics.Vector3.Distance).Sum();
        // ground truth: every returned segment must be clear in the RAW voxel map
        int badSegments = 0;
        for (int i = 1; i < thetaPath.Count; ++i)
        {
            var sv = Navmesh.NavVolume.VoxelSearch.FindNearestEmptyVoxel(loaded.Volume, thetaPath[i - 1], new System.Numerics.Vector3(1, 1, 1));
            var ev = Navmesh.NavVolume.VoxelSearch.FindNearestEmptyVoxel(loaded.Volume, thetaPath[i], new System.Numerics.Vector3(1, 1, 1));
            if (sv == Navmesh.NavVolume.VoxelMap.InvalidVoxel || ev == Navmesh.NavVolume.VoxelMap.InvalidVoxel
                || !Navmesh.NavVolume.VoxelSearch.LineOfSight(loaded.Volume, sv, ev, thetaPath[i - 1], thetaPath[i]))
                ++badSegments;
        }
        Console.WriteLine($"  lazy theta*: waypoints {thetaPath.Count}, {sw2.Elapsed.TotalMilliseconds:f1} ms, {len:f0}m{(thetaPartial ? " (partial)" : "")}, bad segments {badSegments} [{flight.LastOutcome}]");
    }
    return 0;
}

// fake-game mode (milestone 10): impersonate Ariadne, pushing a player marker that
// patrols between two points of a zone so the viewer's live link can be tested
// without the game running.
if (args.Length > 0 && args[0] == "fake-game")
{
    var target = args.Length > 1 ? args[1] : "s1t1";
    var entry = MeshCache.Enumerate().Where(e => e.IsSupported && e.Key.Contains(target, StringComparison.OrdinalIgnoreCase))
        .OrderByDescending(e => new FileInfo(e.Path).Length).FirstOrDefault();
    if (entry == null)
    {
        Console.WriteLine($"no supported zone matches '{target}'");
        return 1;
    }
    var (a, b) = SamplePoints(entry.Path);
    var pa = new System.Numerics.Vector3(a[0], a[1], a[2]);
    var pb = new System.Numerics.Vector3(b[0], b[1], b[2]);

    // optional client count: one connection per fake toon, so this exercises the real
    // fleet path (per-connection state) rather than one client rewriting its own sample
    int fleetSize = args.Length > 2 && int.TryParse(args[2], out var n) ? Math.Clamp(n, 1, 8) : 1;
    var names = new[] { "Alpha", "Bravo", "Charlie", "Delta", "Echo", "Foxtrot", "Golf", "Hotel" };
    Console.WriteLine($"pushing {fleetSize} fake player(s) in '{entry.Key}' between {pa} and {pb} at 10 Hz - Ctrl+C to stop");

    float legDist = MathF.Max(System.Numerics.Vector3.Distance(pa, pb), 1);
    float step = 0.6f / legDist; // 6.0 y/s at 10 Hz - the real run speed, so it calibrates honestly
    var pushers = Enumerable.Range(0, fleetSize).Select(async i =>
    {
        using var fakeClient = new Mnemosyne.Client.MnemosyneClient();
        await fakeClient.ConnectAsync();
        float t = fleetSize == 1 ? 0 : i / (float)fleetSize; // spread them along the leg
        bool forward = true;
        while (true)
        {
            t += (forward ? 1 : -1) * step;
            if (t is >= 1 or <= 0)
                forward = !forward;
            var pos = System.Numerics.Vector3.Lerp(pa, pb, Math.Clamp(t, 0, 1));
            var dir = forward ? pb - pa : pa - pb;
            var rotation = MathF.Atan2(dir.X, dir.Z);
            await fakeClient.UpdateGameStateAsync(entry.Key, 0, [pos.X, pos.Y, pos.Z], rotation, flying: false,
                character: $"{names[i]}@Testworld");
            await Task.Delay(100);
        }
    });
    await Task.WhenAll(pushers);
    return 0;
}

// Smoke test (milestone 2): load a v25 vnavmesh cache file, print stats, run a
// nearest-poly query at a point taken from the mesh itself.

string path;
if (args.Length > 0)
{
    path = args[0];
}
else
{
    var candidates = MeshCache.Enumerate().Where(e => e.IsSupported).OrderBy(e => e.Key).ToList();
    Console.WriteLine($"Cache dir: {MeshCache.DefaultDirectory}");
    Console.WriteLine($"Supported (v{Navmesh.Navmesh.Version}) files: {candidates.Count}");
    if (candidates.Count == 0)
    {
        Console.WriteLine("No supported cache files found; pass a .navmesh path as argument.");
        return 1;
    }
    path = candidates[0].Path;
}

Console.WriteLine($"Loading: {path}");
var sw = System.Diagnostics.Stopwatch.StartNew();
var navmesh = MeshCache.Load(path);
sw.Stop();
Console.WriteLine($"Loaded in {sw.ElapsedMilliseconds} ms (customization version {navmesh.CustomizationVersion})");

var mesh = navmesh.Mesh;
var meshParams = mesh.GetParams();
Console.WriteLine($"Params: origin=({meshParams.orig.X:f1}, {meshParams.orig.Y:f1}, {meshParams.orig.Z:f1}), tile={meshParams.tileWidth:f1}x{meshParams.tileHeight:f1}, maxTiles={meshParams.maxTiles}, maxPolys={meshParams.maxPolys}");

int tiles = 0, polys = 0, verts = 0;
for (int i = 0; i < mesh.GetMaxTiles(); ++i)
{
    var tile = mesh.GetTile(i);
    if (tile?.data?.header == null)
        continue;
    ++tiles;
    polys += tile.data.header.polyCount;
    verts += tile.data.header.vertCount;
}
Console.WriteLine($"Tiles: {tiles}, polys: {polys}, verts: {verts}");
Console.WriteLine(navmesh.Volume != null
    ? $"Volume: {navmesh.Volume.Levels.Length} levels, bounds {navmesh.Volume.RootTile.BoundsMin} .. {navmesh.Volume.RootTile.BoundsMax}"
    : "Volume: none");

// query smoke test: use the first vertex of the first non-empty tile as a known-walkable point
var firstTile = Enumerable.Range(0, mesh.GetMaxTiles()).Select(mesh.GetTile).First(t => t?.data?.header != null)!;
var probe = new DotRecast.Core.Numerics.RcVec3f(firstTile.data.verts[0], firstTile.data.verts[1], firstTile.data.verts[2]);
var query = new DtNavMeshQuery(mesh);
var status = query.FindNearestPoly(probe, new(2, 2, 2), new DtQueryDefaultFilter(), out var nearestRef, out var nearestPt, out _);
Console.WriteLine($"FindNearestPoly at ({probe.X:f1}, {probe.Y:f1}, {probe.Z:f1}): status={status}, ref={nearestRef:X}, pt=({nearestPt.X:f1}, {nearestPt.Y:f1}, {nearestPt.Z:f1})");

if (!status.Succeeded() || nearestRef == 0)
{
    Console.WriteLine("SMOKE TEST FAILED: query did not find a poly");
    return 1;
}
Console.WriteLine("SMOKE TEST PASSED");
return 0;

// picks two distant tile-corner vertices of a zone as test waypoints
static (float[] From, float[] To) SamplePoints(string cachePath)
{
    var mesh = MeshCache.Load(cachePath).Mesh;
    var pts = new List<System.Numerics.Vector3>();
    for (int i = 0; i < mesh.GetMaxTiles(); ++i)
    {
        var t = mesh.GetTile(i);
        if (t?.data?.header is { vertCount: > 0 })
            pts.Add(new(t.data.verts[0], t.data.verts[1], t.data.verts[2]));
    }
    var best = pts.SelectMany(a => pts.Select(b => (a, b)))
        .OrderByDescending(p => System.Numerics.Vector3.DistanceSquared(p.a, p.b)).First();
    return ([best.a.X, best.a.Y, best.a.Z], [best.b.X, best.b.Y, best.b.Z]);
}

static async Task<int> IpcTest()
{
    using var client = new Mnemosyne.Client.MnemosyneClient();
    try
    {
        await client.ConnectAsync();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"IPC TEST FAILED: cannot connect ({ex.Message}) - is Mnemosyne.Service running?");
        return 1;
    }

    var hello = await client.HelloAsync();
    Console.WriteLine($"hello: app={hello.App} v{hello.Version}, protocol {hello.Protocol}, meshVersion {hello.MeshVersion}");
    if (hello.App != "mnemosyne")
    {
        Console.WriteLine($"IPC TEST FAILED: connected to '{hello.App}', not the real service (is the stub still running?)");
        return 1;
    }

    var zones = await client.ListZonesAsync();
    if (!zones.Ok || zones.Zones is not { Count: > 0 })
    {
        Console.WriteLine($"IPC TEST FAILED: listZones: {zones.Error}");
        return 1;
    }
    var current = zones.Zones.Where(z => z.Version == hello.MeshVersion).ToList();
    Console.WriteLine($"listZones: {zones.Zones.Count} zones ({current.Count} current-version)");

    // getMesh + walk path in Limsa
    var limsa = current.FirstOrDefault(z => z.CacheKey.Contains("s1t1"));
    if (limsa == null)
    {
        Console.WriteLine("IPC TEST FAILED: no current s1t1 in cache");
        return 1;
    }
    var mesh = await client.GetMeshAsync(limsa.CacheKey);
    Console.WriteLine(mesh.Ok ? $"getMesh: {mesh.Path} ({mesh.Size} bytes)" : $"getMesh failed: {mesh.Error}");

    var (from, to) = SamplePoints(mesh.Path!);
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var walk = await client.FindPathAsync(limsa.CacheKey, from, to);
    Console.WriteLine(walk.Ok
        ? $"findPath(walk, s1t1): {walk.Waypoints!.Length} waypoints in {sw.ElapsedMilliseconds} ms round-trip"
        : $"findPath(walk) failed: {walk.Error}");

    // fly path in a volume zone
    var volZone = current.FirstOrDefault(z => z.CacheKey.Contains("x6f2"));
    bool flyOk = true;
    if (volZone != null)
    {
        var volMesh = await client.GetMeshAsync(volZone.CacheKey);
        var (vf, vt) = SamplePoints(volMesh.Path!);
        vf[1] += 20; // lift into the air
        vt[1] += 20;
        sw.Restart();
        var fly = await client.FindPathAsync(volZone.CacheKey, vf, vt, fly: true);
        Console.WriteLine(fly.Ok
            ? $"findPath(fly, x6f2): {fly.Waypoints!.Length} waypoints in {sw.ElapsedMilliseconds} ms round-trip"
            : $"findPath(fly) failed: {fly.Error}");
        flyOk = fly.Ok;
    }

    // reachableCells: the exploration op, end to end — a JSON request out and a grid back
    sw.Restart();
    var cells = await client.SendAsync<ReachableCellsResponse>(new Request
    {
        Op = "reachableCells", CacheKey = limsa.CacheKey, From = from, Radius = 60, CellSize = 2,
    });
    var cellsOk = cells.Ok && cells.Columns is { Length: > 0 } && cells.Stats is { WalkablePolys: > 0 };
    Console.WriteLine(cells.Ok
        ? $"reachableCells(s1t1): {cells.Columns!.Length} surfaces on {cells.Width}x{cells.Depth} @ {cells.CellSize}y, "
          + $"{cells.Stats!.ReachablePolys}/{cells.Stats.WalkablePolys} polys reachable, "
          + $"reachableOutside {cells.ReachableOutside}, {sw.ElapsedMilliseconds} ms round-trip"
        : $"reachableCells failed: {cells.Error} [{cells.Result}]");

    // and the off-mesh case: a point in the air answers startOffMesh, carrying a nearest point
    var air = new[] { from[0], from[1] + 500, from[2] };
    var offMesh = await client.SendAsync<ReachableCellsResponse>(new Request
    {
        Op = "reachableCells", CacheKey = limsa.CacheKey, From = air, Radius = 30, CellSize = 2,
    });
    var offMeshOk = !offMesh.Ok && offMesh.Result == "startOffMesh" && offMesh.Nearest is { Length: 3 };
    Console.WriteLine(offMeshOk
        ? $"reachableCells(off-mesh): startOffMesh, nearest ({offMesh.Nearest![0]:f1}, {offMesh.Nearest[1]:f1}, {offMesh.Nearest[2]:f1})"
        : $"reachableCells(off-mesh) unexpected: ok={offMesh.Ok} result={offMesh.Result} nearest={offMesh.Nearest?.Length}");

    var allOk = mesh.Ok && walk.Ok && flyOk && cellsOk && offMeshOk;
    Console.WriteLine(allOk ? "IPC TEST PASSED" : "IPC TEST FAILED");
    return allOk ? 0 : 1;
}



