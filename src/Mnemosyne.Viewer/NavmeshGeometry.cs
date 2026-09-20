using DotRecast.Detour;
using System.Numerics;

namespace Mnemosyne.Viewer;

// CPU-side triangle soup built from a DtNavMesh (detail meshes, so heights are exact).
// Built on a background thread; uploaded to the GPU on the main thread.
public sealed class NavmeshGeometry
{
    public float[] Positions = [];  // xyz, 3 verts per tri
    public byte[] Colors = [];      // rgba per vert
    public Vector3 BoundsMin;
    public Vector3 BoundsMax;
    public int Tiles;
    public int Polys;
    public int Tris;

    private static readonly Vector3 LightDir = Vector3.Normalize(new(0.35f, 0.8f, 0.45f));

    public static NavmeshGeometry Build(DtNavMesh mesh)
    {
        var positions = new List<float>(1 << 20);
        var colors = new List<byte>(1 << 20);
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        int tiles = 0, polys = 0, tris = 0;

        for (int t = 0; t < mesh.GetMaxTiles(); ++t)
        {
            var tile = mesh.GetTile(t);
            var data = tile?.data;
            if (data?.header == null)
                continue;
            ++tiles;

            for (int p = 0; p < data.header.polyCount; ++p)
            {
                var poly = data.polys[p];
                if (poly.GetPolyType() != 0) // skip off-mesh connection polys
                    continue;
                ++polys;

                bool unreachable = (poly.flags & global::Navmesh.Navmesh.FLAG_UNREACHABLE) != 0;
                bool blocked = poly.flags == 0; // override-blocked (or filtered out entirely)
                var (br, bg, bb) = BaseColor(t, p, unreachable, blocked);

                ref readonly var detail = ref data.detailMeshes[p];
                for (int dt = 0; dt < detail.triCount; ++dt)
                {
                    int ti = (detail.triBase + dt) * 4;
                    var a = DetailVertex(data, poly, detail, data.detailTris[ti]);
                    var b = DetailVertex(data, poly, detail, data.detailTris[ti + 1]);
                    var c = DetailVertex(data, poly, detail, data.detailTris[ti + 2]);

                    var normal = Vector3.Cross(b - a, c - a);
                    float len = normal.Length();
                    float shade = len > 1e-6f ? 0.55f + 0.45f * MathF.Abs(Vector3.Dot(normal / len, LightDir)) : 1.0f;

                    byte r = (byte)(br * shade), g = (byte)(bg * shade), bl = (byte)(bb * shade);
                    foreach (var v in (ReadOnlySpan<Vector3>)[a, b, c])
                    {
                        positions.Add(v.X); positions.Add(v.Y); positions.Add(v.Z);
                        colors.Add(r); colors.Add(g); colors.Add(bl); colors.Add(255);
                        min = Vector3.Min(min, v);
                        max = Vector3.Max(max, v);
                    }
                    ++tris;
                }
            }
        }

        return new()
        {
            Positions = [.. positions],
            Colors = [.. colors],
            BoundsMin = min,
            BoundsMax = max,
            Tiles = tiles,
            Polys = polys,
            Tris = tris,
        };
    }

    private static Vector3 DetailVertex(DtMeshData data, DtPoly poly, in DtPolyDetail detail, int index)
    {
        int vi = index < poly.vertCount
            ? poly.verts[index] * 3
            : (detail.vertBase + index - poly.vertCount) * 3;
        var verts = index < poly.vertCount ? data.verts : data.detailVerts;
        return new(verts[vi], verts[vi + 1], verts[vi + 2]);
    }

    // steel-blue walkable / red unreachable / dark red blocked-by-override, with a small
    // per-poly jitter so poly boundaries read
    private static (byte r, byte g, byte b) BaseColor(int tileIndex, int polyIndex, bool unreachable, bool blocked)
    {
        uint h = (uint)(tileIndex * 73856093 ^ polyIndex * 19349663);
        h = (h ^ (h >> 13)) * 0x5bd1e995;
        int jitter = (int)(h % 31) - 15;
        if (blocked)
            return ((byte)Math.Clamp(150 + jitter, 0, 255), (byte)Math.Clamp(45 + jitter, 0, 255), (byte)Math.Clamp(45 + jitter, 0, 255));
        return unreachable
            ? ((byte)Math.Clamp(190 + jitter, 0, 255), (byte)Math.Clamp(70 + jitter, 0, 255), (byte)Math.Clamp(60 + jitter, 0, 255))
            : ((byte)Math.Clamp(95 + jitter, 0, 255), (byte)Math.Clamp(145 + jitter, 0, 255), (byte)Math.Clamp(200 + jitter, 0, 255));
    }
}
