using Mnemosyne.Builder;
using Mnemosyne.Core;
using Mnemosyne.Viewer;
using Raylib_cs;

// usage: Mnemosyne.Viewer [zone-path-or-key-substring] [--screenshot out.png]
if (args.Length > 0 && args[0] == "--test-backdrop")
{
    var dir = TerritoryGeometry.FindSqpackDir();
    if (dir == null)
    {
        Console.WriteLine("no sqpack found");
        return 1;
    }
    var bg = args.Length > 1 ? args[1] : "ffxiv/sea_s1/twn/s1t1/level/s1t1";
    var geo = TerritoryGeometry.Build(dir, bg);
    Console.WriteLine($"models {geo.Models}  tris {geo.Tris}");
    return 0;
}

string? zoneArg = null;
string? screenshotPath = null;
bool startInEditMode = false;
bool autoPathArg = false;
bool autoFlyArg = false;
bool noVolumeArg = false;
bool noMeshArg = false;
bool followArg = false;
bool pickerArg = false;
bool travelArg = false;
bool collisionArg = false;
bool noGeometryArg = false;
bool objectsArg = false;
bool autoPlanArg = false;
for (int i = 0; i < args.Length; ++i)
{
    if (args[i] == "--screenshot" && i + 1 < args.Length)
        screenshotPath = args[++i];
    else if (args[i] == "--edit")
        startInEditMode = true; // headless proof of the edit overlay; E toggles it live
    else if (args[i] == "--auto-path")
        autoPathArg = true;
    else if (args[i] == "--auto-fly")
        autoFlyArg = true;
    else if (args[i] == "--no-volume")
        noVolumeArg = true;
    else if (args[i] == "--no-mesh")
        noMeshArg = true;
    else if (args[i] == "--follow")
        followArg = true;
    else if (args[i] == "--picker")
        pickerArg = true;
    else if (args[i] == "--travel")
        travelArg = true;
    else if (args[i] == "--collision")
        collisionArg = true;
    else if (args[i] == "--no-geometry")
        noGeometryArg = true;
    else if (args[i] == "--objects")
        objectsArg = true;
    else if (args[i] == "--auto-plan")
        autoPlanArg = true;
    else
        zoneArg = args[i];
}

string? ResolveZone(string? arg)
{
    if (arg == null)
        return null;
    if (File.Exists(arg))
        return arg;
    return MeshCache.Enumerate()
        .Where(e => e.IsSupported && (e.Key.Contains(arg, StringComparison.OrdinalIgnoreCase)
            || (ZoneNames.Resolve(e.Key)?.Contains(arg, StringComparison.OrdinalIgnoreCase) ?? false)))
        .OrderByDescending(e => new FileInfo(e.Path).Length)
        .FirstOrDefault()?.Path;
}

string? initialPath = ResolveZone(zoneArg);
if (zoneArg != null && initialPath == null)
{
    Console.WriteLine($"No supported cache entry matches '{zoneArg}'");
    return 1;
}
if (initialPath == null && screenshotPath != null && !pickerArg)
    initialPath = MeshCache.Enumerate().Where(e => e.IsSupported)
        .OrderByDescending(e => new FileInfo(e.Path).Length)
        .FirstOrDefault()?.Path;

Raylib.SetConfigFlags(ConfigFlags.ResizableWindow | ConfigFlags.Msaa4xHint | ConfigFlags.VSyncHint);
Raylib.InitWindow(1600, 900, "Mnemosyne Viewer");
Raylib.SetExitKey(KeyboardKey.Null);
Rlgl.DisableBackfaceCulling();
Rlgl.SetClipPlanes(0.3, 10000); // default far plane clips kilometer-scale zones

var camera = new FreeCamera();
var picker = new ZonePicker();
var pathTool = new PathTool();
var editTool = new EditTool();
var animator = new PathAnimator();
using var gameLink = new GameLink();
bool followPlayer = followArg;
DotRecast.Detour.DtNavMesh? currentMesh = null;
Task<NavmeshGeometry>? rebuildTask = null;
editTool.MeshEdited += () =>
{
    if (currentMesh is { } m)
        rebuildTask = Task.Run(() => NavmeshGeometry.Build(m));
};
string? sqpackDir = TerritoryGeometry.FindSqpackDir();
Model? model = null;
Mesh? navRlMesh = null;
Model? volumeModel = null;
List<Model> backdropModels = [];
NavmeshGeometry? geometry = null;
VolumeGeometry? volumeGeometry = null;
TerritoryGeometry? backdropGeometry = null;
Task<TerritoryGeometry>? backdropTask = null;
bool showBackdrop = !noGeometryArg;
List<Model> collisionModels = [];
CollisionGeometry? collisionGeometry = null;
Task<CollisionGeometry>? collisionTask = null;
bool showCollision = false;
string? collisionZoneBg = null;
List<ObjectMarker> objectMarkers = [];
Task<List<ObjectMarker>>? objectTask = null;
bool showObjects = false;
string zoneName = "";
string zoneTitle = "";
long loadMs = 0;
bool loadedFromFastCache = false;
int renderMode = 0; // 0 = solid+wires, 1 = solid, 2 = wires
bool showNavmesh = !noMeshArg;
bool showVolume = !noVolumeArg;
int helpMode = 0; // 0 = hint line, 1 = full panel, 2 = hidden
bool quit = false;
bool pathFramed = false;
bool objectsFramed = false;
int framesSinceLoad = 0;
int framesTotal = 0;
int travelHoldFrames = 0;
Task<(FastCache.Loaded Nav, NavmeshGeometry Geo, VolumeGeometry? VolGeo, string Name, long Ms,
    ZoneOverrides Overrides, int[][] FlagSnapshot, List<DoorMarker> Doors)>? loadTask = null;
Task<string?>? zoneBuildTask = null;
string buildingZoneName = "";
int buildDone = 0, buildTotal = 0;

void StartBuildZone(ZonePicker.Item item)
{
    if (sqpackDir == null || item.Bg.Length == 0)
        return;
    buildingZoneName = item.Name;
    buildDone = 0;
    buildTotal = 0;
    var bg = item.Bg;
    var bgKey = item.SubText;
    var dir = sqpackDir;
    zoneBuildTask = Task.Run<string?>(() =>
    {
        var navmesh = Mnemosyne.Builder.ZoneBuilder.BuildAuto(dir, bg, (done, total) => { buildDone = done; buildTotal = total; });
        var builtDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mnemosyne", "built");
        Directory.CreateDirectory(builtDir);
        var outPath = Path.Combine(builtDir, bgKey + "__viewer____0.navmesh");
        var temp = outPath + ".tmp";
        using (var stream = File.Create(temp))
        using (var writer = new BinaryWriter(stream))
            navmesh.Serialize(writer);
        File.Move(temp, outPath, true);
        return outPath;
    });
}

void StartLoad(string path)
{
    var name = Path.GetFileNameWithoutExtension(path);
    backdropTask = null; // abandon any in-flight backdrop build for the previous zone
    loadTask = Task.Run(() =>
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var nav = FastCache.Load(path, withVolume: true);
        var snapshot = EditTool.SnapshotFlags(nav.Mesh);
        var overrides = OverrideStore.Load(name);
        if (!overrides.IsEmpty)
            OverrideStore.Apply(nav.Mesh, overrides);
        var doors = sqpackDir != null && ZoneNames.Lookup(name)?.Bg is { Length: > 0 } bg
            ? DoorScan.Scan(sqpackDir, bg)
            : [];
        var geo = NavmeshGeometry.Build(nav.Mesh);
        var volGeo = nav.Volume != null ? VolumeGeometry.Build(nav.Volume) : null;
        return (nav, geo, volGeo, name, sw.ElapsedMilliseconds, overrides, snapshot, doors);
    });
}

// pick two far-apart tile vertices that actually connect, for headless verification
void AutoPath(DotRecast.Detour.DtNavMesh m)
{
    var pts = new List<System.Numerics.Vector3>();
    for (int i = 0; i < m.GetMaxTiles(); ++i)
    {
        var t = m.GetTile(i);
        if (t?.data?.header is { vertCount: > 0 })
            pts.Add(new(t.data.verts[0], t.data.verts[1], t.data.verts[2]));
    }
    var pairs = pts.SelectMany(a => pts.Select(b => (a, b))).Where(p => p.a != p.b)
        .OrderByDescending(p => System.Numerics.Vector3.DistanceSquared(p.a, p.b)).Take(60).ToList();
    foreach (var acceptPartial in (ReadOnlySpan<bool>)[false, true])
    {
        foreach (var (a, b) in pairs)
        {
            pathTool.OnClick(a);
            pathTool.OnClick(b);
            if (pathTool.Path.Count >= 2 && (acceptPartial || !pathTool.Status.Contains("partial")))
                return;
            pathTool.Clear();
        }
    }
}

// multi-modal test: two far-apart points on the SAME walkable component (corner tile verts
// sit in unreachable boundary pockets), so walking is possible but slow - exactly the case
// where the planner should choose to mount and fly. Async: the plan lands a few frames later.
void AutoPlan(DotRecast.Detour.DtNavMesh m)
{
    var centers = new List<(long Ref, System.Numerics.Vector3 Pos)>();
    for (int i = 0; i < m.GetMaxTiles(); ++i)
    {
        var tile = m.GetTile(i);
        if (tile?.data?.header == null)
            continue;
        long refBase = m.GetPolyRefBase(tile);
        for (int p = 0; p < tile.data.header.polyCount; ++p)
        {
            if (tile.data.polys[p].GetPolyType() != 0)
                continue;
            var sum = System.Numerics.Vector3.Zero;
            var poly = tile.data.polys[p];
            for (int v = 0; v < poly.vertCount; ++v)
            {
                int vi = poly.verts[v] * 3;
                sum += new System.Numerics.Vector3(tile.data.verts[vi], tile.data.verts[vi + 1], tile.data.verts[vi + 2]);
            }
            centers.Add((refBase | (uint)p, sum / poly.vertCount));
        }
    }
    if (centers.Count == 0)
        return;
    var query = new DotRecast.Detour.DtNavMeshQuery(m);
    var seed = centers[centers.Count / 2];
    var reach = new List<long>();
    var parents = new List<long>();
    var costs = new List<float>();
    query.FindPolysAroundCircle(seed.Ref, new DotRecast.Core.Numerics.RcVec3f(seed.Pos.X, seed.Pos.Y, seed.Pos.Z), 1e9f,
        new DotRecast.Detour.DtQueryDefaultFilter(), ref reach, ref parents, ref costs);
    var reachable = new HashSet<long>(reach) { seed.Ref };
    var connected = centers.Where(c => reachable.Contains(c.Ref)).ToList();
    if (connected.Count < 2)
        return;
    var far = connected.OrderByDescending(c => System.Numerics.Vector3.DistanceSquared(c.Pos, seed.Pos)).First();
    pathTool.OnClick(seed.Pos);
    pathTool.OnClick(far.Pos);
}

// fly test: a mid-length hop (~200-500m) between lifted tile corners, so the voxel A* stays quick
void AutoFly(DotRecast.Detour.DtNavMesh m)
{
    var pts = new List<System.Numerics.Vector3>();
    for (int i = 0; i < m.GetMaxTiles(); ++i)
    {
        var t = m.GetTile(i);
        if (t?.data?.header is { vertCount: > 0 })
            pts.Add(new(t.data.verts[0], t.data.verts[1] + 15, t.data.verts[2]));
    }
    var pair = pts.SelectMany(a => pts.Select(b => (a, b)))
        .Where(p => System.Numerics.Vector3.Distance(p.a, p.b) is > 200 and < 500)
        .OrderByDescending(p => System.Numerics.Vector3.DistanceSquared(p.a, p.b))
        .FirstOrDefault();
    if (pair.a != pair.b)
    {
        pathTool.OnClick(pair.a);
        pathTool.OnClick(pair.b);
    }
}

// Stable per-connection colour so a toon keeps the same marker between frames; the palette
// is small on purpose - four clients is the real fleet size.
static Color FleetColor(int clientId) => (clientId % 4) switch
{
    0 => new Color(80, 220, 255, 255),
    1 => new Color(255, 200, 80, 255),
    2 => new Color(140, 255, 140, 255),
    _ => new Color(255, 140, 200, 255),
};

static Mesh BuildMesh(float[] positions, byte[] colors)
{
    int vertexCount = positions.Length / 3;
    var mesh = new Mesh(vertexCount, vertexCount / 3);
    mesh.AllocVertices();
    mesh.AllocColors();
    positions.CopyTo(mesh.VerticesAs<float>());
    colors.CopyTo(mesh.ColorsAs<byte>());
    Raylib.UploadMesh(ref mesh, false);
    return mesh;
}

static Model MakeModel(float[] positions, byte[] colors) => Raylib.LoadModelFromMesh(BuildMesh(positions, colors));

static Model MakeIndexedModel(MeshBatch batch)
{
    var mesh = new Mesh(batch.Positions.Length / 3, batch.Indices.Length / 3);
    mesh.AllocVertices();
    mesh.AllocColors();
    mesh.AllocIndices();
    batch.Positions.CopyTo(mesh.VerticesAs<float>());
    batch.Colors.CopyTo(mesh.ColorsAs<byte>());
    batch.Indices.CopyTo(mesh.IndicesAs<ushort>());
    Raylib.UploadMesh(ref mesh, false);
    return Raylib.LoadModelFromMesh(mesh);
}

if (initialPath != null)
    StartLoad(initialPath);
else
{
    picker.Open = true;
    picker.Refresh();
}

while (!quit && !Raylib.WindowShouldClose())
{
    if (loadTask is { IsCompletedSuccessfully: true })
    {
        var (nav, geo, volGeo, name, ms, overrides, flagSnapshot, doors) = loadTask.Result;
        loadTask = null;
        if (model.HasValue)
            Raylib.UnloadModel(model.Value);
        if (volumeModel.HasValue)
            Raylib.UnloadModel(volumeModel.Value);
        geometry = geo;
        var rlMesh = BuildMesh(geo.Positions, geo.Colors);
        navRlMesh = rlMesh;
        model = Raylib.LoadModelFromMesh(rlMesh);
        currentMesh = nav.Mesh;
        pathTool.SetMesh(nav.Mesh, nav.Volume, () => editTool.Overrides.Links);
        editTool.SetZone(nav.Mesh, name, overrides, flagSnapshot, doors);
        if (autoPlanArg)
        {
            pathTool.Mode = PathTool.PathMode.Auto;
            AutoPlan(nav.Mesh);
        }
        else if (autoPathArg)
            AutoPath(nav.Mesh);
        else if (autoFlyArg && nav.Volume != null)
        {
            pathTool.ToggleFly();
            AutoFly(nav.Mesh);
        }
        volumeGeometry = volGeo;
        volumeModel = volGeo is { Boxes: > 0 } ? MakeModel(volGeo.Positions, volGeo.Colors) : null;
        zoneName = name;
        zoneTitle = ZoneNames.Resolve(name) ?? name;
        loadMs = ms;
        loadedFromFastCache = nav.FromFastCache;
        framesSinceLoad = 0;
        camera.FrameBounds(geo.BoundsMin, geo.BoundsMax);
        Console.WriteLine($"zone '{name}': bounds {geo.BoundsMin} .. {geo.BoundsMax}, camera {camera.Position}, yaw {camera.Yaw:f2} pitch {camera.Pitch:f2}");

        foreach (var bm in backdropModels)
            Raylib.UnloadModel(bm);
        backdropModels.Clear();
        backdropGeometry = null;
        foreach (var cm in collisionModels)
            Raylib.UnloadModel(cm);
        collisionModels.Clear();
        collisionGeometry = null;
        collisionTask = null;
        objectMarkers = [];
        objectTask = null;
        collisionZoneBg = ZoneNames.Lookup(name)?.Bg;
        if (collisionArg && sqpackDir is { } cdir && collisionZoneBg is { Length: > 0 } cbg)
        {
            showCollision = true;
            collisionTask = Task.Run(() => CollisionGeometry.Build(cdir, cbg));
        }
        if (objectsArg && sqpackDir is { } odir2 && collisionZoneBg is { Length: > 0 } obg2)
        {
            showObjects = true;
            objectTask = Task.Run(() => ObjectScan.Scan(odir2, obg2));
        }
        if (sqpackDir != null && ZoneNames.Lookup(name)?.Bg is { Length: > 0 } bgPath)
        {
            var dir = sqpackDir;
            backdropTask = Task.Run(() => TerritoryGeometry.Build(dir, bgPath));
        }
    }
    else if (loadTask is { IsFaulted: true })
    {
        Console.WriteLine($"Load failed: {loadTask.Exception?.GetBaseException().Message}");
        loadTask = null;
        if (screenshotPath != null)
            break;
    }

    if (backdropTask is { IsCompletedSuccessfully: true })
    {
        var geo = backdropTask.Result;
        backdropTask = null;
        backdropGeometry = geo;
        foreach (var batch in geo.Batches)
            backdropModels.Add(MakeIndexedModel(batch));
    }
    else if (backdropTask is { IsFaulted: true })
    {
        Console.WriteLine($"Backdrop load failed: {backdropTask.Exception?.GetBaseException().Message}");
        backdropTask = null;
    }

    if (objectTask is { IsCompletedSuccessfully: true })
    {
        objectMarkers = objectTask.Result;
        objectTask = null;
    }
    else if (objectTask is { IsFaulted: true })
    {
        Console.WriteLine($"Object scan failed: {objectTask.Exception?.GetBaseException().Message}");
        objectTask = null;
        showObjects = false;
    }

    if (collisionTask is { IsCompletedSuccessfully: true })
    {
        var geo = collisionTask.Result;
        collisionTask = null;
        collisionGeometry = geo;
        foreach (var batch in geo.Batches)
            collisionModels.Add(MakeIndexedModel(batch));
    }
    else if (collisionTask is { IsFaulted: true })
    {
        Console.WriteLine($"Collision load failed: {collisionTask.Exception?.GetBaseException().Message}");
        collisionTask = null;
        showCollision = false;
    }

    if (rebuildTask is { IsCompletedSuccessfully: true })
    {
        var geo = rebuildTask.Result;
        rebuildTask = null;
        if (model.HasValue)
            Raylib.UnloadModel(model.Value);
        geometry = geo;
        var rlMesh = BuildMesh(geo.Positions, geo.Colors);
        navRlMesh = rlMesh;
        model = Raylib.LoadModelFromMesh(rlMesh);
    }
    else if (rebuildTask is { IsFaulted: true })
    {
        rebuildTask = null;
    }

    if (zoneBuildTask is { IsCompletedSuccessfully: true })
    {
        var builtPath = zoneBuildTask.Result;
        zoneBuildTask = null;
        if (builtPath != null)
            StartLoad(builtPath);
    }
    else if (zoneBuildTask is { IsFaulted: true })
    {
        Console.WriteLine($"zone build failed: {zoneBuildTask.Exception?.GetBaseException().Message}");
        zoneBuildTask = null;
    }

    var picked = picker.Update();
    if (picked != null)
    {
        if (!picked.NeedsBuild)
            StartLoad(picked.Path!);
        else if (zoneBuildTask == null)
            StartBuildZone(picked);
    }

    if (Raylib.IsKeyPressed(KeyboardKey.Escape))
    {
        if (picker.Open)
            picker.Open = false;
        else
            quit = true;
    }
    if (Raylib.IsKeyPressed(KeyboardKey.F2))
        renderMode = (renderMode + 1) % 3;
    if (Raylib.IsKeyPressed(KeyboardKey.O))
    {
        showObjects = !showObjects;
        if (showObjects && objectMarkers.Count == 0 && objectTask == null
            && sqpackDir is { } odir && collisionZoneBg is { Length: > 0 } obg)
            objectTask = Task.Run(() => ObjectScan.Scan(odir, obg));
    }
    if (Raylib.IsKeyPressed(KeyboardKey.F3))
    {
        showCollision = !showCollision;
        // extracted on first request: it re-reads every collision file for the zone
        if (showCollision && collisionGeometry == null && collisionTask == null
            && sqpackDir is { } dir && collisionZoneBg is { Length: > 0 } bg)
            collisionTask = Task.Run(() => CollisionGeometry.Build(dir, bg));
    }
    if (Raylib.IsKeyPressed(KeyboardKey.N))
        showNavmesh = !showNavmesh;
    if (Raylib.IsKeyPressed(KeyboardKey.V))
        showVolume = !showVolume;
    if (Raylib.IsKeyPressed(KeyboardKey.G))
        showBackdrop = !showBackdrop;
    if (Raylib.IsKeyPressed(KeyboardKey.C))
        pathTool.Clear();
    if (Raylib.IsKeyPressed(KeyboardKey.F))
        pathTool.ToggleFly();
    if (Raylib.IsKeyPressed(KeyboardKey.P))
        followPlayer = !followPlayer;
    if (Raylib.IsKeyPressed(KeyboardKey.T))
    {
        if (Raylib.IsKeyDown(KeyboardKey.LeftShift))
            animator.Chase = !animator.Chase;
        else
            animator.Toggle();
    }
    if (Raylib.IsKeyPressed(KeyboardKey.Comma))
        animator.Rate = MathF.Max(0.25f, animator.Rate / 2);
    if (Raylib.IsKeyPressed(KeyboardKey.Period))
        animator.Rate = MathF.Min(16f, animator.Rate * 2);
    if (startInEditMode)
    {
        editTool.Active = true;
        startInEditMode = false;
    }
    if (Raylib.IsKeyPressed(KeyboardKey.E))
        editTool.Active = !editTool.Active;
    if (editTool.Active)
    {
        if (Raylib.IsKeyPressed(KeyboardKey.X)) editTool.Mode = "block";
        if (Raylib.IsKeyPressed(KeyboardKey.U)) editTool.Mode = "unblock";
        if (Raylib.IsKeyPressed(KeyboardKey.B)) editTool.Mode = "box";
        if (Raylib.IsKeyPressed(KeyboardKey.L)) editTool.Mode = "link";
        if (Raylib.IsKeyPressed(KeyboardKey.K)) editTool.Mode = "prune";
        if (Raylib.IsKeyPressed(KeyboardKey.R)) editTool.RemoveLast();
        if (Raylib.IsKeyPressed(KeyboardKey.LeftBracket)) editTool.BrushRadius = MathF.Max(2, editTool.BrushRadius - 2);
        if (Raylib.IsKeyPressed(KeyboardKey.RightBracket)) editTool.BrushRadius = MathF.Min(40, editTool.BrushRadius + 2);
        if (Raylib.IsKeyDown(KeyboardKey.LeftControl) && Raylib.IsKeyPressed(KeyboardKey.S)) editTool.Save();
    }
    pathTool.Update();

    var gameSnap = gameLink.Latest;
    if (followPlayer && gameSnap != null && gameSnap.CacheKey is { Length: > 0 } gameKey && gameKey != zoneName && loadTask == null)
    {
        // follow mode: the viewer tracks the player's zone
        var gamePath = Path.Combine(MeshCache.DefaultDirectory, gameKey + ".navmesh");
        if (File.Exists(gamePath))
            StartLoad(gamePath);
    }

    if (!picker.Open && navRlMesh.HasValue && Raylib.IsMouseButtonPressed(MouseButton.Left))
    {
        var ray = Raylib.GetScreenToWorldRay(Raylib.GetMousePosition(), camera.ToCamera3D());
        var hit = Raylib.GetRayCollisionMesh(ray, navRlMesh.Value, System.Numerics.Matrix4x4.Identity);
        if (hit.Hit)
        {
            if (editTool.Active)
                editTool.OnClick(hit.Point);
            else
                pathTool.OnClick(hit.Point);
        }
    }
    if (Raylib.IsKeyPressed(KeyboardKey.H))
        helpMode = (helpMode + 1) % 3; // hint line -> full panel -> hidden

    // travel animation runs at the calibrated speed; in Auto mode each leg uses its own
    float walkSpeed = gameLink.GroundSpeed ?? PathAnimator.DefaultGroundSpeed;
    float flySpeedNow = gameLink.FlySpeed ?? PathAnimator.DefaultFlySpeed;
    pathTool.WalkSpeed = walkSpeed;
    pathTool.FlySpeed = flySpeedNow;
    bool travelCalibrated = (pathTool.Fly ? gameLink.FlySpeed : gameLink.GroundSpeed) != null;
    float travelSpeed = pathTool.Fly ? flySpeedNow : walkSpeed;
    animator.Update(pathTool.Path, d => pathTool.SpeedAt(d, walkSpeed, flySpeedNow), Raylib.GetFrameTime());

    camera.Update(Raylib.GetFrameTime(), picker.Open);
    if (followPlayer && gameSnap != null)
        camera.Position = gameSnap.Pos - camera.Forward * 45 + new System.Numerics.Vector3(0, 5, 0);
    else if (animator.Chase && animator.HasPath)
        camera.Position = animator.Position - camera.Forward * 18 + new System.Numerics.Vector3(0, 6, 0);

    Raylib.BeginDrawing();
    Raylib.ClearBackground(new Color(24, 26, 32, 255));

    if (model.HasValue)
    {
        Raylib.BeginMode3D(camera.ToCamera3D());
        if (showBackdrop)
            foreach (var bm in backdropModels)
                Raylib.DrawModel(bm, System.Numerics.Vector3.Zero, 1, Color.White);
        if (showCollision)
            foreach (var cm in collisionModels)
                Raylib.DrawModel(cm, System.Numerics.Vector3.Zero, 1, Color.White);
        if (showNavmesh && renderMode is 0 or 1)
            Raylib.DrawModel(model.Value, System.Numerics.Vector3.Zero, 1, Color.White);
        if (showNavmesh && renderMode is 0 or 2)
            Raylib.DrawModelWires(model.Value, System.Numerics.Vector3.Zero, 1, new Color(25, 28, 38, 160));
        if (showVolume && volumeModel.HasValue)
            Raylib.DrawModel(volumeModel.Value, System.Numerics.Vector3.Zero, 1, Color.White);
        pathTool.Draw();
        animator.Draw(animator.CurrentSpeed > walkSpeed * 1.5f || pathTool.Fly);
        editTool.Draw();
        if (showObjects && objectMarkers.Count > 0)
            ObjectMarkers.Draw3D(objectMarkers, camera.Position);
        // every toon in the fleet that is standing in the zone we have loaded - the user
        // runs four clients, and a marker for only the last one to push is a lie
        var here = gameLink.Players.Where(p => p.CacheKey == zoneName).ToList();
        if (here.Count == 0 && gameSnap != null)
            here = [new GameLink.Player(0, null, gameSnap.CacheKey, gameSnap.Pos, gameSnap.Rotation, gameSnap.Flying, gameSnap.Speed, gameSnap.AgeMs)];
        if (here.Count > 0)
        {
            // player markers, x-ray so they read through geometry
            Rlgl.DrawRenderBatchActive();
            Rlgl.DisableDepthTest();
            foreach (var p in here)
            {
                var head = p.Pos + new System.Numerics.Vector3(0, 1, 0);
                var facing = new System.Numerics.Vector3(MathF.Sin(p.Rotation), 0, MathF.Cos(p.Rotation));
                var markerColor = p.Flying ? Color.Magenta : FleetColor(p.ClientId);
                Raylib.DrawSphere(head, 1.0f, markerColor);
                Raylib.DrawCylinderEx(head, head + facing * 4, 0.45f, 0.05f, 8, markerColor);
                Raylib.DrawLine3D(p.Pos, p.Pos + new System.Numerics.Vector3(0, 30, 0), markerColor);
            }
            Rlgl.DrawRenderBatchActive();
            Rlgl.EnableDepthTest();
        }
        Raylib.EndMode3D();
        foreach (var p in here)
        {
            if (p.Character is not { Length: > 0 } name)
                continue;
            var screen = Raylib.GetWorldToScreen(p.Pos + new System.Numerics.Vector3(0, 3.2f, 0), camera.ToCamera3D());
            // GetWorldToScreen projects points behind the camera too; skip those
            if (System.Numerics.Vector3.Dot(p.Pos - camera.Position, camera.Forward) <= 0)
                continue;
            Raylib.DrawText(name, (int)screen.X + 1, (int)screen.Y + 1, 14, new Color(0, 0, 0, 180));
            Raylib.DrawText(name, (int)screen.X, (int)screen.Y, 14, FleetColor(p.ClientId));
        }
        ++framesSinceLoad;
    }

    if (geometry != null)
    {
        Raylib.DrawText(zoneTitle, 10, 10, 24, Color.RayWhite);
        Raylib.DrawText(zoneName, 10, 38, 16, new Color(130, 140, 155, 255));
        var volInfo = volumeGeometry != null ? $"  volume {volumeGeometry.Boxes} boxes ({(showVolume ? "on" : "off")}, V)" : "  no fly volume";
        var objectInfo = objectTask != null ? "  objects scanning..."
            : objectMarkers.Count > 0 ? $"  objects {objectMarkers.Count} ({(showObjects ? "on" : "off")}, O)"
            : "";
        var collisionInfo = collisionTask != null ? "  collision extracting..."
            : collisionGeometry != null ? $"  collision {collisionGeometry.Instances} inst {collisionGeometry.Tris / 1000}k tris ({(showCollision ? "on" : "off")}, F3)"
            : "";
        var geoInfo = backdropTask != null ? "  geometry loading..."
            : backdropGeometry != null ? $"  geometry {backdropGeometry.Models} models {backdropGeometry.Tris / 1000}k tris ({(showBackdrop ? "on" : "off")}, G)"
            : sqpackDir == null ? "  geometry unavailable (no game install found)" : "";
        Raylib.DrawText($"tiles {geometry.Tiles}  polys {geometry.Polys}  tris {geometry.Tris}  loaded in {loadMs} ms{(loadedFromFastCache ? " (fast)" : " (brotli+bake)")}{volInfo}{geoInfo}{collisionInfo}{objectInfo}", 10, 58, 18, new Color(170, 180, 195, 255));
    }
    if (showObjects && objectMarkers.Count > 0)
        ObjectMarkers.DrawLabels(objectMarkers, camera.ToCamera3D());

    var fleetInfo = gameLink.Players.Count > 1 ? $"  fleet {gameLink.Players.Count}" : "";
    var gameInfo = gameSnap != null
        ? $"game: ({gameSnap.Pos.X:f0}, {gameSnap.Pos.Y:f0}, {gameSnap.Pos.Z:f0})  {gameSnap.Speed:f1} y/s{(gameSnap.Flying ? " flying" : "")}  follow {(followPlayer ? "on" : "off")} (P){fleetInfo}"
        : gameLink.Connected ? "game: no player data" : "game: service not connected";
    Raylib.DrawText(gameInfo, Raylib.GetScreenWidth() - Raylib.MeasureText(gameInfo, 18) - 12, 34, 18,
        gameSnap != null ? new Color(80, 220, 255, 255) : new Color(120, 130, 145, 255));
    if (pathTool.Status.Length > 0)
    {
        var etaText = animator.HasPath && travelSpeed > 0.5f && pathTool.PlanSeconds <= 0
            ? $"  ~{animator.TotalLength / travelSpeed:f0}s at {travelSpeed:f1} y/s"
            : ""; // Auto mode already reports its own total in the status
        Raylib.DrawText(pathTool.Status + etaText, 10, 82, 18, new Color(255, 235, 140, 255));
    }
    if (animator.HasPath)
    {
        // in Auto mode the planner's own total (incl. mount time) is the honest number
        float totalSeconds = pathTool.PlanSeconds > 0
            ? pathTool.PlanSeconds
            : animator.TotalLength / MathF.Max(travelSpeed, 0.01f);
        Raylib.DrawText(animator.Status(totalSeconds, travelCalibrated) + (animator.Playing ? "" : "  (T to travel)"),
            10, 130, 18, animator.Playing ? new Color(255, 200, 60, 255) : new Color(170, 160, 130, 255));
    }
    if (showCollision && collisionGeometry != null)
    {
        // legend: these are the rasterizer's own categories, not decoration
        int ly = Raylib.GetScreenHeight() - 130;
        Raylib.DrawText("collision (what Recast rasterized):", 10, ly, 18, Color.RayWhite);
        Raylib.DrawText("walkable surface", 30, ly + 22, 18, new Color(110, 190, 130, 255));
        Raylib.DrawText("forced unwalkable", 30, ly + 42, 18, new Color(200, 80, 70, 255));
        Raylib.DrawText("fly-through (solid to walkers only)", 30, ly + 62, 18, new Color(90, 150, 220, 255));
        Raylib.DrawText("unlandable", 30, ly + 82, 18, new Color(215, 165, 70, 255));
    }
    if (loadTask != null)
        Raylib.DrawText("Loading...", 10, 106, 20, new Color(255, 210, 120, 255));
    if (zoneBuildTask != null)
        Raylib.DrawText(buildTotal > 0
            ? $"building {buildingZoneName}: tile {buildDone}/{buildTotal}"
            : $"building {buildingZoneName}: reading game files...", 10, 106, 20, new Color(255, 170, 60, 255));
    if (editTool.Active)
        Raylib.DrawText(editTool.StatusLine, 10, Raylib.GetScreenHeight() - 52, 18, new Color(255, 170, 60, 255));
    if (helpMode == 0)
        Raylib.DrawText("H full help | Tab zones | LMB waypoints | T travel path | E edit mesh | F walk/fly | C clear | P follow | N mesh | F2 mode | V volume | G geometry | Esc quit",
            10, Raylib.GetScreenHeight() - 28, 18, new Color(140, 150, 165, 255));
    else if (helpMode == 1)
        HelpPanel.Draw();
    Raylib.DrawFPS(Raylib.GetScreenWidth() - 100, 10);

    picker.Draw();
    Raylib.EndDrawing();

    // in objects screenshot mode, frame a landmark so labels are in range
    if (screenshotPath != null && objectsArg && !objectsFramed && objectMarkers.Count > 0)
    {
        var focus = objectMarkers.FirstOrDefault(m => m.Kind == MarkerKind.Aetheryte) ?? objectMarkers[0];
        camera.FrameBounds(focus.Pos - new System.Numerics.Vector3(20), focus.Pos + new System.Numerics.Vector3(20));
        objectsFramed = true;
        framesSinceLoad = 0;
    }

    // in auto-path screenshot mode, zoom to the computed path once it exists
    if (screenshotPath != null && (autoPathArg || autoFlyArg) && !pathFramed && pathTool.Path.Count > 0)
    {
        var pMin = new System.Numerics.Vector3(float.MaxValue);
        var pMax = new System.Numerics.Vector3(float.MinValue);
        foreach (var w in pathTool.Path)
        {
            pMin = System.Numerics.Vector3.Min(pMin, w);
            pMax = System.Numerics.Vector3.Max(pMax, w);
        }
        camera.FrameBounds(pMin, pMax);
        pathFramed = true;
        framesSinceLoad = 0;
    }

    // wait a few swaps after everything is ready: TakeScreenshot reads the buffer of the previous frame
    if (loadTask != null || backdropTask != null || pathTool.Busy)
        framesSinceLoad = 0;
    ++framesTotal;
    // --travel: auto-play once a path exists, then hold so the traveler is visibly underway
    if (travelArg && animator.HasPath && !animator.Playing && !animator.Finished && travelHoldFrames == 0)
    {
        animator.Chase = true;
        animator.Toggle();
    }
    if (travelArg && animator.Playing)
        ++travelHoldFrames;
    if (screenshotPath != null && travelArg && travelHoldFrames < 150)
        framesSinceLoad = 0; // keep waiting while the traveler covers ground
    if (screenshotPath != null && collisionArg && (collisionTask != null || collisionGeometry == null))
        framesSinceLoad = 0; // wait for the collision extract to finish
    if (screenshotPath != null && objectsArg && (objectTask != null || objectMarkers.Count == 0))
        framesSinceLoad = 0; // wait for the object scan to finish
    if (screenshotPath != null && autoPlanArg && pathTool.Busy)
        framesSinceLoad = 0; // the multi-modal plan runs off-thread
    if (screenshotPath != null && (framesSinceLoad >= 5 || (pickerArg && framesTotal >= 10)))
    {
        // raylib resolves the path relative to its working directory even when absolute
        var temp = $"mnemosyne_shot_{Environment.ProcessId}.png";
        Raylib.TakeScreenshot(temp);
        File.Move(temp, screenshotPath, true);
        quit = true;
    }
}

if (model.HasValue)
    Raylib.UnloadModel(model.Value);
if (volumeModel.HasValue)
    Raylib.UnloadModel(volumeModel.Value);
foreach (var bm in backdropModels)
    Raylib.UnloadModel(bm);
foreach (var cm in collisionModels)
    Raylib.UnloadModel(cm);
Raylib.CloseWindow();
return 0;
