using DotRecast.Core.Numerics;
using DotRecast.Detour;
using System.Numerics;

namespace Mnemosyne.Core;

// Backs Nav.PathfindAvoid on ground paths. Rejects any poly whose disc (centre + extent)
// touches an XZ circle, so the route bends around a hazard instead of through it.
// Ported from vnavmesh's AvoidRadiusFilter so a relayed consumer gets the same shape of
// answer. Note: start and end must lie outside the circle or FindPath has nothing to
// stand on — SegmentEntersAvoid is what decides whether the filter is worth applying.
public sealed class AvoidRadiusFilter(Vector3 center, float radius) : IDtQueryFilter
{
    private readonly DtQueryDefaultFilter _inner = new();
    private readonly float _cx = center.X;
    private readonly float _cz = center.Z;

    public float GetCost(RcVec3f pa, RcVec3f pb, long prevRef, DtMeshTile prevTile, DtPoly prevPoly,
        long curRef, DtMeshTile curTile, DtPoly curPoly, long nextRef, DtMeshTile nextTile, DtPoly nextPoly)
        => _inner.GetCost(pa, pb, prevRef, prevTile, prevPoly, curRef, curTile, curPoly, nextRef, nextTile, nextPoly);

    public bool PassFilter(long refs, DtMeshTile tile, DtPoly poly)
    {
        if (poly.vertCount == 0)
            return true;

        float sumX = 0, sumZ = 0;
        for (int i = 0; i < poly.vertCount; ++i)
        {
            var vi = poly.verts[i] * 3;
            sumX += tile.data.verts[vi];
            sumZ += tile.data.verts[vi + 2];
        }
        var inv = 1f / poly.vertCount;
        var pcx = sumX * inv;
        var pcz = sumZ * inv;

        float extentSq = 0;
        for (int i = 0; i < poly.vertCount; ++i)
        {
            var vi = poly.verts[i] * 3;
            var dx = tile.data.verts[vi] - pcx;
            var dz = tile.data.verts[vi + 2] - pcz;
            extentSq = MathF.Max(extentSq, dx * dx + dz * dz);
        }

        var distX = pcx - _cx;
        var distZ = pcz - _cz;
        return MathF.Sqrt(distX * distX + distZ * distZ) >= radius + MathF.Sqrt(extentSq);
    }

    /// <summary>True when the straight XZ segment passes closer to the centre than the
    /// start already is — the cheap test that says whether avoid filtering can change
    /// anything. Starting inside the circle is allowed; it just means "no closer".</summary>
    public static bool SegmentEnters(Vector3 from, Vector3 to, Vector3 center, float radius)
    {
        var abx = to.X - from.X;
        var abz = to.Z - from.Z;
        var lenSq = abx * abx + abz * abz;
        float t = 0;
        if (lenSq >= 1e-6f)
            t = Math.Clamp(((center.X - from.X) * abx + (center.Z - from.Z) * abz) / lenSq, 0f, 1f);

        var dx = from.X + abx * t - center.X;
        var dz = from.Z + abz * t - center.Z;
        var fromDx = from.X - center.X;
        var fromDz = from.Z - center.Z;
        var minAllowedSq = MathF.Min(radius * radius, fromDx * fromDx + fromDz * fromDz);
        return dx * dx + dz * dz + 1e-3f < minAllowedSq;
    }
}
