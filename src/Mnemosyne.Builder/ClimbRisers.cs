using DotRecast.Detour;
using System.Numerics;

namespace Navmesh;

// The steep "riser" polys a raised step height (AgentMaxClimb) adds to a mesh, for `Mnemosyne.Cli
// climbcomb`, which lists what a raise joined. Added 2026-10-02 after Eulmore's 1.0 y raise for one
// staircase joined ~500 other spots (Z0820Eulmore). It misses wide, gentle joins (a poly spanning floor
// and a 1 m flower box), so the builder limits a raise on the cells instead (LimitClimbToRegions).
public static class ClimbRisers
{
    /// <summary>A poly climbing at least this much...</summary>
    public const float Rise = 0.6f;

    /// <summary>...at 1 in 2 or steeper is a riser. A ramp is gentler.</summary>
    public const float Slope = 0.5f;

    public readonly record struct Riser(long Ref, Vector3 Center, Vector3 Bottom, Vector3 Top);

    public static IEnumerable<Riser> All(DtNavMesh mesh)
    {
        for (int t = 0; t < mesh.GetMaxTiles(); ++t)
        {
            var tile = mesh.GetTile(t);
            var data = tile?.data;
            if (data?.header == null)
                continue;
            long refBase = mesh.GetPolyRefBase(tile);
            for (int p = 0; p < data.header.polyCount; ++p)
            {
                var poly = data.polys[p];
                if (poly.GetPolyType() != 0 || poly.vertCount == 0)
                    continue;
                var verts = new Vector3[poly.vertCount];
                for (int i = 0; i < poly.vertCount; ++i)
                {
                    var vi = poly.verts[i] * 3;
                    verts[i] = new Vector3(data.verts[vi], data.verts[vi + 1], data.verts[vi + 2]);
                }
                var low = verts.MinBy(v => v.Y);
                var high = verts.MaxBy(v => v.Y);
                var run = MathF.Max(0.1f, MathF.Max(verts.Max(v => v.X) - verts.Min(v => v.X), verts.Max(v => v.Z) - verts.Min(v => v.Z)));
                if (high.Y - low.Y >= Rise && (high.Y - low.Y) / run >= Slope)
                    yield return new Riser(refBase | (uint)p, verts.Aggregate(Vector3.Zero, (a, v) => a + v) / verts.Length, low, high);
            }
        }
    }

    /// <summary>The raised mesh's risers that the base mesh has no riser beside (within 0.5 m):
    /// what the raise added.</summary>
    public static List<Riser> Added(DtNavMesh baseMesh, DtNavMesh raisedMesh)
    {
        var existing = All(baseMesh).Select(r => r.Center).ToList();
        return [.. All(raisedMesh).Where(r => !existing.Any(c => Vector3.Distance(c, r.Center) < 0.5f))];
    }
}
