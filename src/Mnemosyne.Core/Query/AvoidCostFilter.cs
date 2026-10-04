using DotRecast.Core.Numerics;
using DotRecast.Detour;
using System.Numerics;

namespace Mnemosyne.Core;

// Soft avoidance, for findPath `avoid` (Ariadne's hunt-mark circles, 2026-10-02): every poly stays
// walkable, but a step that passes inside a circle costs Penalty times as much, so routes bend
// around a circle wherever a way round exists and go through only when none does.
//
// Not AvoidRadiusFilter's hard exclusion, which rejects any poly whose bounding disc touches the
// circle. That suits a small, strict dodge circle, but a field zone's polys are tens of yalms
// across: a 15 y circle beside a route through Kholusia excluded whole slopes and sealed the
// corridor, and a circle shrunk to keep clear of the goal still covered the goal's own poly. Both
// answered "avoidIgnored" with the route unchanged.
public sealed class AvoidCostFilter(IDtQueryFilter inner, IReadOnlyList<(Vector3 Center, float Radius)> circles,
    float penalty = 25f) : IDtQueryFilter
{
    private readonly (float X, float Z, float Radius)[] _circles = [.. circles.Select(c => (c.Center.X, c.Center.Z, c.Radius))];

    public bool PassFilter(long refs, DtMeshTile tile, DtPoly poly) => inner.PassFilter(refs, tile, poly);

    public float GetCost(RcVec3f pa, RcVec3f pb, long prevRef, DtMeshTile prevTile, DtPoly prevPoly,
        long curRef, DtMeshTile curTile, DtPoly curPoly, long nextRef, DtMeshTile nextTile, DtPoly nextPoly)
    {
        var cost = inner.GetCost(pa, pb, prevRef, prevTile, prevPoly, curRef, curTile, curPoly, nextRef, nextTile, nextPoly);
        foreach (var (x, z, radius) in _circles)
            if (DistanceToSegment(x, z, pa.X, pa.Z, pb.X, pb.Z) < radius)
                return cost * penalty;
        return cost;
    }

    /// <summary>Whether a finished route still passes inside any circle - the route the service
    /// then labels "avoidIgnored".</summary>
    public static bool RouteEnters(IReadOnlyList<Vector3> waypoints, IReadOnlyList<(Vector3 Center, float Radius)> circles)
    {
        for (int i = 1; i < waypoints.Count; ++i)
            foreach (var (c, r) in circles)
                if (DistanceToSegment(c.X, c.Z, waypoints[i - 1].X, waypoints[i - 1].Z, waypoints[i].X, waypoints[i].Z) < r)
                    return true;
        return false;
    }

    private static float DistanceToSegment(float px, float pz, float ax, float az, float bx, float bz)
    {
        var abx = bx - ax;
        var abz = bz - az;
        var lenSq = abx * abx + abz * abz;
        var t = lenSq < 1e-6f ? 0f : Math.Clamp(((px - ax) * abx + (pz - az) * abz) / lenSq, 0f, 1f);
        var dx = ax + abx * t - px;
        var dz = az + abz * t - pz;
        return MathF.Sqrt(dx * dx + dz * dz);
    }
}
