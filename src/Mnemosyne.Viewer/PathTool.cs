using DotRecast.Detour;
using Mnemosyne.Core;
using Navmesh.NavVolume;
using Raylib_cs;
using System.Numerics;

namespace Mnemosyne.Viewer;

// Click-to-place waypoint pathfinding: first click sets start, second sets end and
// runs the query; the next click starts a new pair. F toggles walk (navmesh) vs
// fly (voxel volume); fly queries can take seconds, so they run off-thread.
public sealed class PathTool
{
    private MeshPathfinder? _pathfinder;
    private Func<IReadOnlyList<OverrideLink>>? _links;
    private VoxelMap? _volume;
    private VoxelPathfind? _volumeQuery;
    private FlightPathfinder? _flight;
    private Task<(List<Vector3> Path, string Status)>? _task;
    private Task<TravelPlan?>? _planTask;

    // speeds the planner costs with; Program keeps these in sync with the calibrated values
    public float WalkSpeed = PathAnimator.DefaultGroundSpeed;
    public float FlySpeed = PathAnimator.DefaultFlySpeed;

    public enum PathMode { Walk, Fly, Auto }

    public Vector3? Start;
    public Vector3? End;
    public List<Vector3> Path = [];
    public string Status = "";
    public PathMode Mode = PathMode.Walk;
    public List<PathLeg> Legs = [];        // populated in Auto mode
    public float PlanSeconds;
    public bool Fly => Mode == PathMode.Fly;
    public bool HasVolume => _volume != null;
    public bool Busy => _task != null || _planTask != null;

    // speed the traveler should use at a given distance along the path (per-leg in Auto)
    public float SpeedAt(float distance, float walkSpeed, float flySpeed)
    {
        if (Legs.Count == 0)
            return Fly ? flySpeed : walkSpeed;
        float acc = 0;
        foreach (var leg in Legs)
        {
            acc += leg.Length;
            if (distance <= acc)
                return leg.Mode == TravelMode.Fly ? flySpeed : walkSpeed;
        }
        return Legs[^1].Mode == TravelMode.Fly ? flySpeed : walkSpeed;
    }

    public void SetMesh(DtNavMesh? mesh, VoxelMap? volume, Func<IReadOnlyList<OverrideLink>>? links = null)
    {
        _pathfinder = mesh != null ? new MeshPathfinder(mesh) : null;
        _links = links;
        _volume = volume;
        _volumeQuery = null;
        _flight = null;
        _task = null;
        if (volume == null && Mode != PathMode.Walk)
            Mode = PathMode.Walk;
        Clear();
    }

    public void Clear()
    {
        Start = End = null;
        Path = [];
        Legs = [];
        PlanSeconds = 0;
        Status = "";
    }

    public void ToggleFly()
    {
        if (_volume == null)
        {
            Status = "this zone has no flying volume (walk only)";
            return;
        }
        Mode = Mode switch
        {
            PathMode.Walk => PathMode.Fly,
            PathMode.Fly => PathMode.Auto,
            _ => PathMode.Walk,
        };
        if (Start != null && End != null && !Busy)
            Recompute();
        else if (Path.Count == 0)
            Status = $"{Mode.ToString().ToLowerInvariant()} mode - click to place waypoints";
    }

    public void OnClick(Vector3 p)
    {
        if (Busy)
            return;
        if (Start == null || End != null)
        {
            Start = p;
            End = null;
            Path.Clear();
            Status = "start placed - click to set destination";
        }
        else
        {
            End = p;
            Recompute();
        }
    }

    // poll from the frame loop; applies a finished background query
    public void Update()
    {
        if (_task is { IsCompleted: true })
        {
            var (path, status) = _task.IsCompletedSuccessfully ? _task.Result : ([], $"query failed: {_task.Exception?.GetBaseException().Message}");
            _task = null;
            Path = path;
            Status = status;
        }
        if (_planTask is { IsCompleted: true })
        {
            if (_planTask.IsCompletedSuccessfully && _planTask.Result is { } plan)
            {
                Legs = plan.Legs;
                Path = plan.Flatten();
                PlanSeconds = plan.TotalSeconds;
                var modes = string.Join(" → ", plan.Legs.Select(l => l.Mode == TravelMode.Fly ? "fly" : "walk"));
                Status = $"auto: {plan.Summary}  [{modes}]  {plan.TotalSeconds:f0}s total";
            }
            else
            {
                Legs = [];
                Path = [];
                Status = _planTask.IsCompletedSuccessfully ? "no route found" : $"plan failed: {_planTask.Exception?.GetBaseException().Message}";
            }
            _planTask = null;
        }
    }

    private void Recompute()
    {
        Path = [];
        Legs = [];
        PlanSeconds = 0;
        if (_pathfinder == null || Start == null || End == null)
            return;
        var from = Start.Value;
        var to = End.Value;

        if (Mode == PathMode.Auto && _volume != null)
        {
            var pathfinder = _pathfinder;
            var volume = _volume;
            var links = _links?.Invoke() ?? [];
            var walkSpeed = WalkSpeed;
            var flySpeed = FlySpeed;
            Status = "planning route...";
            _planTask = Task.Run(() =>
            {
                _flight ??= new FlightPathfinder(volume);
                return new MultiModalPlanner(pathfinder, _flight).Plan(from, to, walkSpeed, flySpeed, links);
            });
            return;
        }

        if (Fly && _volume != null)
        {
            _volumeQuery ??= new VoxelPathfind(_volume) { MaxSteps = 200_000 }; // ~1.5s worst case; partial toward goal beyond that
            var volumeQuery = _volumeQuery;
            var volume = _volume;
            var self = this;
            Status = "computing fly path...";
            _task = Task.Run(() =>
            {
                // primary: coarse octree Lazy Theta* (built once per zone, a few seconds)
                self._flight ??= new FlightPathfinder(volume);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var flightPath = self._flight.FindPath(from, to, out _);
                sw.Stop();
                if (flightPath.Count >= 2)
                {
                    float length = 0;
                    for (int i = 1; i < flightPath.Count; ++i)
                        length += Vector3.Distance(flightPath[i - 1], flightPath[i]);
                    return (flightPath, $"fly path: {length:f0}m, {flightPath.Count} waypoints, {sw.Elapsed.TotalMilliseconds:f1} ms (theta*)");
                }
                return ComputeFly(volume, volumeQuery, from, to); // coarse-unreachable: fine fallback
            });
        }
        else
        {
            var (path, status) = ComputeWalk(from, to);
            Path = path;
            Status = status;
        }
    }

    private (List<Vector3>, string) ComputeWalk(Vector3 from, Vector3 to)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = _pathfinder!.FindWalkPath(from, to, _links?.Invoke() ?? []);
        sw.Stop();
        if (result == null)
            return ([], "no path found");
        var partial = result.Partial ? " (partial)" : "";
        return (result.Waypoints, $"walk path: {PathLength(result.Waypoints):f0}m, {result.Waypoints.Count} waypoints, {result.PolyCount} polys, {sw.Elapsed.TotalMilliseconds:f1} ms{partial}");
    }

    private static (List<Vector3>, string) ComputeFly(VoxelMap volume, VoxelPathfind volumeQuery, Vector3 from, Vector3 to)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var fromVoxel = VoxelSearch.FindNearestEmptyVoxel(volume, from, new Vector3(3, 3, 3));
        var toVoxel = VoxelSearch.FindNearestEmptyVoxel(volume, to, new Vector3(3, 3, 3));
        if (fromVoxel == VoxelMap.InvalidVoxel || toVoxel == VoxelMap.InvalidVoxel)
            return ([], "no empty voxel near a waypoint");
        var voxelPath = volumeQuery.FindPath(fromVoxel, toVoxel, from, to, false, false, CancellationToken.None); // raycast=true measured 9x SLOWER offline (87s vs 9.5s on a 580m hop)
        sw.Stop();
        if (voxelPath.Count == 0)
            return ([], "no volume path found");
        List<Vector3> path = [.. voxelPath.Select(v => v.p)];
        var partial = Vector3.Distance(path[^1], to) > 10 ? " (partial - budget)" : "";
        return (path, $"fly path: {PathLength(path):f0}m, {path.Count} waypoints, {sw.Elapsed.TotalSeconds:f2} s{partial}");
    }

    private static float PathLength(List<Vector3> path)
    {
        float length = 0;
        for (int i = 1; i < path.Count; ++i)
            length += Vector3.Distance(path[i - 1], path[i]);
        return length;
    }

    public void Draw()
    {
        // draw x-ray: waypoints and route stay visible through terrain
        Rlgl.DrawRenderBatchActive();
        Rlgl.DisableDepthTest();
        var lift = new Vector3(0, 0.3f, 0);
        if (Start is { } s)
        {
            Raylib.DrawSphere(s + lift, 1.2f, Color.Lime);
            Raylib.DrawLine3D(s, s + new Vector3(0, 15, 0), Color.Lime);
        }
        if (End is { } e)
        {
            Raylib.DrawSphere(e + lift, 1.2f, Color.Red);
            Raylib.DrawLine3D(e, e + new Vector3(0, 15, 0), Color.Red);
        }
        if (Legs.Count > 0)
        {
            // multi-modal: each leg in its own colour, transitions marked
            foreach (var leg in Legs)
            {
                var legColor = leg.Mode == TravelMode.Fly ? Color.Magenta : Color.Yellow;
                for (int i = 1; i < leg.Waypoints.Count; ++i)
                    Raylib.DrawCylinderEx(leg.Waypoints[i - 1] + lift, leg.Waypoints[i] + lift, 0.35f, 0.35f, 6, legColor);
                foreach (var w in leg.Waypoints)
                    Raylib.DrawSphere(w + lift, 0.5f, legColor);
            }
            for (int i = 1; i < Legs.Count; ++i)
                if (Legs[i - 1].Mode != Legs[i].Mode && Legs[i].Waypoints.Count > 0)
                {
                    var swap = Legs[i].Waypoints[0] + lift;
                    Raylib.DrawSphere(swap, 1.4f, Color.White); // mount / dismount
                    Raylib.DrawLine3D(swap, swap + new Vector3(0, 10, 0), Color.White);
                }
        }
        else
        {
            var color = Fly ? Color.Magenta : Color.Yellow;
            for (int i = 1; i < Path.Count; ++i)
                Raylib.DrawCylinderEx(Path[i - 1] + lift, Path[i] + lift, 0.35f, 0.35f, 6, color);
            foreach (var w in Path)
                Raylib.DrawSphere(w + lift, 0.6f, color);
        }
        Rlgl.DrawRenderBatchActive();
        Rlgl.EnableDepthTest();
    }
}

