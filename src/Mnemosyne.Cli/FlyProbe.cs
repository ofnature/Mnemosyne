using Mnemosyne.Core;
using Navmesh.NavVolume;
using System.Numerics;

namespace Mnemosyne.Cli;

// Flying and walking read different data. The navmesh says where a floor is; the voxel volume
// says where air is. A doorway can be open in one and shut in the other — walk through it fine
// and stop dead trying to fly through — and no amount of staring at the navmesh explains why.
//
// This prints the volume: a slice of empty vs solid space at a height, so a sealed doorway is
// visible as a wall of '#' where the floor plan shows an opening.
//
// usage: Mnemosyne.Cli flyprobe <zone-substring> <x> <y> <z> [radius] [step]
public static class FlyProbe
{
    public static int Run(string[] args)
    {
        if (args.Length < 5 || !float.TryParse(args[2], out var x)
            || !float.TryParse(args[3], out var y) || !float.TryParse(args[4], out var z))
        {
            Console.WriteLine("usage: flyprobe <zone-substring> <x> <y> <z> [radius] [step]");
            return 1;
        }
        var radius = args.Length > 5 && float.TryParse(args[5], out var r) ? r : 8f;
        var step = args.Length > 6 && float.TryParse(args[6], out var s) ? s : 0.5f;

        if (Probe.ResolveZoneOrReport(args[1]) is not { } entry)
            return 1;
        Console.WriteLine($"zone: {entry.Key}");

        var navmesh = MeshCache.Load(entry.Path);
        if (navmesh.Volume is not { } volume)
        {
            Console.WriteLine("this zone has no flying volume - nothing to fly through");
            return 1;
        }

        var point = new Vector3(x, y, z);
        var (voxel, empty) = volume.FindLeafVoxel(point);
        Console.WriteLine($"point ({x:f1}, {y:f1}, {z:f1}): {(empty ? "EMPTY (flyable)" : "SOLID (blocked)")}"
            + $"{(voxel == VoxelMap.InvalidVoxel ? " - outside the volume" : "")}");

        var nearest = VoxelSearch.FindNearestEmptyVoxel(volume, point, new Vector3(5, 5, 5));
        Console.WriteLine(nearest == VoxelMap.InvalidVoxel
            ? "no empty voxel within 5m - solidly enclosed"
            : $"nearest empty voxel centre: {VoxelSearch.FindClosestVoxelPoint(volume, nearest, point):f1}");

        // A horizontal slice at the given height. Doorways are horizontal openings, so this is
        // the view that shows one - a vertical slice would just show floor and ceiling.
        int half = (int)MathF.Ceiling(radius / step);
        Console.WriteLine();
        Console.WriteLine($"horizontal slice at y={y:f1}  ({radius:f0}m radius, {step:f2}m cells)");
        Console.WriteLine();
        for (int gz = -half; gz <= half; ++gz)
        {
            var row = new System.Text.StringBuilder();
            for (int gx = -half; gx <= half; ++gx)
            {
                if (gx == 0 && gz == 0)
                {
                    row.Append('@');
                    continue;
                }
                var p = new Vector3(x + gx * step, y, z + gz * step);
                var (_, isEmpty) = volume.FindLeafVoxel(p);
                row.Append(isEmpty ? '.' : '#');
            }
            Console.WriteLine("  " + row);
        }
        Console.WriteLine();
        Console.WriteLine("  . flyable air    # solid    @ the point you asked about");
        return 0;
    }
}
