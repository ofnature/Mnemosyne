using Navmesh.NavVolume;
using System.Numerics;

namespace Mnemosyne.Core;

// Straighten a raw voxel path.
//
// The voxel A* returns one waypoint per voxel step, so a flight route zigzags along the grid
// even through wide open air. vnavmesh ships the same raw output — its volume pathfind carries
// a "TODO: string-pulling support" and never got one — which is why flight paths look drunk.
// Measured in Thavnair: a 53 m hop came back as 64 waypoints covering 161 m, three times the
// straight-line distance, and it looked exactly that bad in game.
//
// The fix is the standard string pull: walk forward from each kept waypoint and skip as far
// as line of sight allows. The volume already answers line-of-sight queries, so this is cheap
// and it cannot produce a route through geometry - every retained segment has been tested.
public static class VolumePathSmoother
{
    /// <summary>Greedy string pull over a voxel path. Returns the kept waypoints, endpoints
    /// always included.</summary>
    public static List<Vector3> Smooth(VoxelMap volume, IReadOnlyList<(ulong Voxel, Vector3 Pos)> path)
    {
        if (path.Count <= 2)
            return [.. path.Select(p => p.Pos)];

        var result = new List<Vector3> { path[0].Pos };
        var anchor = 0;
        while (anchor < path.Count - 1)
        {
            // farthest visible point from the anchor; scanning backwards takes the biggest
            // legal skip first, which is what makes one pass enough
            var next = anchor + 1;
            for (int candidate = path.Count - 1; candidate > anchor + 1; --candidate)
            {
                if (!VoxelSearch.LineOfSight(volume, path[anchor].Voxel, path[candidate].Voxel,
                        path[anchor].Pos, path[candidate].Pos))
                    continue;
                next = candidate;
                break;
            }
            result.Add(path[next].Pos);
            anchor = next;
        }
        return result;
    }
}
