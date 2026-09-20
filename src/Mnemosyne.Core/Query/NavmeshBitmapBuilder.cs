using DotRecast.Detour;
using Navmesh;
using System.Numerics;

namespace Mnemosyne.Core;

// Serves vnavmesh's Nav.BuildBitmap* gates (Olympus uses the bounded forms): flood-fill
// the walkable polys reachable from the starting points, clip to optional map bounds, and
// rasterize them into vnavmesh's 1-bit bitmap format. Algorithm mirrors
// NavmeshManager.BuildBitmap so consumers get the same image.
public static class NavmeshBitmapBuilder
{
    public static NavmeshBitmap Build(DtNavMesh mesh, IReadOnlyList<Vector3> startingPoints,
        float pixelSize, Vector3? minBounds, Vector3? maxBounds)
    {
        var query = new DtNavMeshQuery(mesh);
        var filter = new DtQueryDefaultFilter();

        var seeds = new List<long>();
        foreach (var p in startingPoints)
        {
            query.FindNearestPoly(p.SystemToRecast(), new(5, 5, 5), filter, out var polyRef, out _, out _);
            if (polyRef != 0)
                seeds.Add(polyRef);
        }

        var reachable = FloodFillPolys(mesh, seeds);

        bool InBounds(Vector3 v) =>
            (minBounds is not { } lo || (v.X >= lo.X && v.Y >= lo.Y && v.Z >= lo.Z))
            && (maxBounds is not { } hi || (v.X <= hi.X && v.Y <= hi.Y && v.Z <= hi.Z));

        var inbounds = new List<long>();
        var min = new Vector3(1024);
        var max = new Vector3(-1024);
        foreach (var polyRef in reachable)
        {
            mesh.GetTileAndPolyByRefUnsafe(polyRef, out var tile, out var poly);
            var verts = new Vector3[poly.vertCount];
            bool ok = true;
            for (int i = 0; i < poly.vertCount; ++i)
            {
                verts[i] = NavmeshBitmap.GetVertex(tile, poly.verts[i]);
                if (!InBounds(verts[i]))
                {
                    ok = false;
                    break;
                }
            }
            if (!ok)
                continue;
            foreach (var v in verts)
            {
                min = Vector3.Min(min, v);
                max = Vector3.Max(max, v);
            }
            inbounds.Add(polyRef);
        }

        if (inbounds.Count == 0) // nothing reachable in bounds: hand back a minimal empty image
            (min, max) = (Vector3.Zero, new Vector3(pixelSize));

        var bitmap = new NavmeshBitmap(min, max, pixelSize);
        foreach (var polyRef in inbounds)
            bitmap.RasterizePolygon(mesh, polyRef);
        return bitmap;
    }

    // walk poly links from the seeds (vnavmesh's FindReachableMeshPolys)
    private static HashSet<long> FloodFillPolys(DtNavMesh mesh, List<long> seeds)
    {
        var result = new HashSet<long>();
        var queue = new Stack<long>(seeds.Where(s => s != 0));
        while (queue.Count > 0)
        {
            var next = queue.Pop();
            if (!result.Add(next))
                continue;
            mesh.GetTileAndPolyByRefUnsafe(next, out var tile, out var poly);
            for (int i = tile.polyLinks[poly.index]; i != DtNavMesh.DT_NULL_LINK; i = tile.links[i].next)
            {
                var neighbour = tile.links[i].refs;
                if (neighbour != 0)
                    queue.Push(neighbour);
            }
        }
        return result;
    }
}
