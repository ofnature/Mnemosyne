using Mnemosyne.Builder;
using Mnemosyne.Core;
using System.Numerics;

namespace Mnemosyne.Cli;

// The Yedlihmad doorway class, hunted systematically instead of one anecdote at a time.
//
// A closed door's collision mesh rasterizes as a wall, so the navmesh seals a passage the
// player can walk through the moment the door opens. The viewer has had manual door toggling
// for a while; this finds the ones worth toggling by testing whether the mesh actually lets
// you cross each door, and records the failures as evidence for the edit workflow.
//
// Nothing is auto-applied. A wrongly-opened door reroutes every path in the zone through a
// wall, which is worse than the sealed passage it was meant to fix.
//
// usage: Mnemosyne.Cli doors <zone-substring> [--record]
public static class DoorAudit
{
    /// <summary>A crossing this much longer than the straight line counts as blocked. Doors
    /// sit in walls, so some detour is normal; 3x is where "went around the building" starts.</summary>
    private const float DetourLimit = 3f;

    public static int Run(string[] args)
    {
        var record = args.Contains("--record");
        var hint = args.Length > 1 ? args[1] : "";

        var sqpack = GamePaths.FindSqpackDir();
        if (sqpack == null)
        {
            Console.WriteLine("no game install found");
            return 1;
        }

        var entry = MeshCache.Enumerate().Where(e => e.IsSupported && e.Key.Contains(hint, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => new FileInfo(e.Path).Length).FirstOrDefault();
        if (entry == null)
        {
            Console.WriteLine($"no current-version zone matches '{hint}'");
            return 1;
        }
        var info = ZoneNames.Lookup(entry.Key);
        if (info?.Bg is not { Length: > 0 } bg)
        {
            Console.WriteLine($"no bg path known for '{entry.Key}'");
            return 1;
        }
        Console.WriteLine($"zone: {info.Name} ({entry.Key})");

        var doors = DoorScan.Scan(sqpack, bg);
        Console.WriteLine($"{doors.Count} door-like objects in the layout");
        if (doors.Count == 0)
            return 0;

        var navmesh = MeshCache.Load(entry.Path);
        var pf = new MeshPathfinder(navmesh.Mesh);

        var sealedDoors = new List<(DoorMarker Door, string Why, float Detour)>();
        int open = 0, unmeshed = 0;
        foreach (var door in doors)
        {
            var (crossed, why, detour) = TestCrossing(pf, door);
            if (crossed)
                ++open;
            else if (why == "no mesh on either side")
                ++unmeshed;
            else
                sealedDoors.Add((door, why, detour));
        }

        Console.WriteLine();
        Console.WriteLine($"  {open} doors the mesh lets you through");
        Console.WriteLine($"  {unmeshed} with no mesh nearby (interiors the mesh never covered - not a door problem)");
        Console.WriteLine($"  {sealedDoors.Count} sealed by the mesh:");
        Console.WriteLine();
        foreach (var (door, why, detour) in sealedDoors.OrderByDescending(d => d.Detour).Take(25))
            Console.WriteLine($"    ({door.Pos.X,7:f1}, {door.Pos.Y,6:f1}, {door.Pos.Z,7:f1})  {door.Label,-38} {why}"
                + (detour > 0 ? $" ({detour:f1}x detour)" : ""));
        if (sealedDoors.Count > 25)
            Console.WriteLine($"    ... and {sealedDoors.Count - 25} more");

        if (record && sealedDoors.Count > 0)
        {
            foreach (var (door, why, _) in sealedDoors)
            {
                var half = MathF.Max(door.HalfExtents.X, door.HalfExtents.Z);
                EvidenceStore.Record(entry.Key, new TraversalReport
                {
                    From = [door.Pos.X - half, door.Pos.Y, door.Pos.Z],
                    To = [door.Pos.X + half, door.Pos.Y, door.Pos.Z],
                    Mode = "direct",
                    Success = true, // "the world lets you through here" - a link candidate
                    Note = $"door audit: {door.Label} ({why})",
                });
            }
            Console.WriteLine();
            Console.WriteLine($"recorded {sealedDoors.Count} candidates in {EvidenceStore.Directory} - "
                + "open the zone in the viewer's edit mode to review them");
        }
        else if (sealedDoors.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("re-run with --record to file these as edit candidates");
        }
        return 0;
    }

    /// <summary>Can a walker get from one side of this door to the other? Tests the two axes
    /// across the door's own extents, so a wide gate is probed wider than a hatch.</summary>
    private static (bool Crossed, string Why, float Detour) TestCrossing(MeshPathfinder pf, DoorMarker door)
    {
        var reach = MathF.Max(MathF.Max(door.HalfExtents.X, door.HalfExtents.Z), 3f) + 4f;
        var best = float.MaxValue;
        bool anyMesh = false;

        foreach (var axis in new[] { new Vector3(reach, 0, 0), new Vector3(0, 0, reach) })
        {
            if (!pf.TryNearestGround(door.Pos + axis, 8, out var a) || !pf.TryNearestGround(door.Pos - axis, 8, out var b))
                continue;
            anyMesh = true;
            var straight = Vector3.Distance(a, b);
            if (straight < 0.5f)
                continue; // both sides snapped to the same spot; tells us nothing
            var route = pf.FindWalkPath(a, b);
            if (route is not { Partial: false })
                continue;
            best = MathF.Min(best, Length(route.Waypoints) / straight);
        }

        if (!anyMesh)
            return (false, "no mesh on either side", 0);
        if (best == float.MaxValue)
            return (false, "no route across", 0);
        return best <= DetourLimit ? (true, "", best) : (false, "route detours around it", best);
    }

    private static float Length(List<Vector3> path)
    {
        float length = 0;
        for (int i = 1; i < path.Count; ++i)
            length += Vector3.Distance(path[i - 1], path[i]);
        return length;
    }
}
