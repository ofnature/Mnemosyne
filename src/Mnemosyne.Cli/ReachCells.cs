using Mnemosyne.Core;
using Mnemosyne.Protocol;
using Mnemosyne.Service;
using System.Numerics;
using System.Text.Json;

namespace Mnemosyne.Cli;

// reachableCells, rendered — the op as the service answers it, drawn as a picture, with the
// window, the stacked surfaces and the zone-wide component stats the protocol promises. It
// drives ZoneService.Handle in process (the same dispatch, validation, zone loading and DTO the
// pipe server uses) rather than the pipe, so it can run while a service is already serving a
// live game session; `ipc-test` is the end-to-end harness over the pipe.
//
// The spec asks for exactly this: the CLI should render the op's own answer, so the
// pathfind-per-cell picture (`reachmap`) and this one can be compared. Note they answer subtly
// different questions — reachmap snaps ground within 20 y of each probe point, while
// reachableCells samples the cell centre, as specified — so they agree wherever the centre is
// meshed and diverge in the fringes the snap reaches.
//
// usage: Mnemosyne.Cli reachcells <zone-substring> [x y z] [radius] [cellSize] [minY] [maxY]
//
// With no coordinates it starts from the middle of the walkable mass, which makes the command
// usable headlessly (and is how it gets smoke-tested without the game running).
public static class ReachCells
{
    public static int Run(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("usage: reachcells <zone-substring> [x y z] [radius] [cellSize] [minY] [maxY]");
            return 1;
        }

        float x = 0, y = 0, z = 0;
        var hasPoint = args.Length >= 5
            && float.TryParse(args[2], out x) && float.TryParse(args[3], out y) && float.TryParse(args[4], out z);
        var numericFrom = hasPoint ? 5 : 2;
        var radius = Arg(args, numericFrom, 120f);
        var cellSize = Arg(args, numericFrom + 1, 2f);
        float? minY = args.Length > numericFrom + 2 && float.TryParse(args[numericFrom + 2], out var lo) ? lo : null;
        float? maxY = args.Length > numericFrom + 3 && float.TryParse(args[numericFrom + 3], out var hi) ? hi : null;

        if (Probe.ResolveZoneOrReport(args[1]) is not { } entry)
            return 1;
        Console.WriteLine($"zone: {entry.Key}");

        Vector3 point;
        if (hasPoint)
        {
            point = new Vector3(x, y, z);
        }
        else
        {
            point = MiddleOfTheMesh(Probe.LoadServed(entry.Path, entry.Key));
        }
        Console.WriteLine($"from: ({point.X:f1}, {point.Y:f1}, {point.Z:f1}){(hasPoint ? "" : "  (picked: middle of the walkable mass)")}"
            + $"  radius {radius:f0}y  cell {cellSize:f1}y"
            + (minY is not null || maxY is not null ? $"  band {(minY is { } l ? l.ToString("f1") : "-inf")} .. {(maxY is { } h ? h.ToString("f1") : "+inf")}" : ""));

        var request = new Request
        {
            Id = 1,
            Op = "reachableCells",
            CacheKey = entry.Key,
            From = [point.X, point.Y, point.Z],
            Radius = radius,
            CellSize = cellSize,
            MinY = minY,
            MaxY = maxY,
        };

        var service = new ZoneService();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var first = service.Handle(request);
        var coldMs = sw.Elapsed.TotalMilliseconds;
        sw.Restart();
        service.Handle(request); // identical call: the flood half is cached per start poly
        var warmMs = sw.Elapsed.TotalMilliseconds;

        if (first is not ReachableCellsResponse cells)
        {
            Console.WriteLine($"op refused: {first.Error} [{first.Result}]");
            return 1;
        }
        if (!cells.Ok)
        {
            Console.WriteLine($"op refused: {cells.Error} [{cells.Result}]"
                + (cells.Nearest is { Length: 3 } n ? $"  nearest ({n[0]:f1}, {n[1]:f1}, {n[2]:f1})" : ""));
            return 1;
        }

        var start = new Vector3(cells.Start![0], cells.Start[1], cells.Start[2]);
        var origins = cells.Origin!;
        Console.WriteLine($"start: ({start.X:f1}, {start.Y:f1}, {start.Z:f1})   "
            + $"component {cells.Stats!.ReachablePolys} polys of {cells.Stats.WalkablePolys} walkable   "
            + $"op {coldMs:f0} ms cold, {warmMs:f0} ms warm");
        Console.WriteLine($"grid: {cells.Width}x{cells.Depth} @ {cells.CellSize:f1}y origin ({origins[0]:f0}, {origins[1]:f0})   "
            + $"{cells.Columns!.Length} surfaces   reachableOutside {cells.ReachableOutside}");

        // the wire shape Ariadne's client binds to: camelCase keys, grid arrays present
        var json = JsonSerializer.Serialize(cells, MnemosynePipe.JsonOptions);
        var keys = new[] { "start", "origin", "cellSize", "width", "depth", "columns", "heights", "states", "reachableOutside", "stats" }
            .Where(k => json.Contains($"\"{k}\":", StringComparison.Ordinal));
        Console.WriteLine($"wire: {json.Length / 1024} KB, keys present — {string.Join(", ", keys)}");

        // Per column, the surface within the agent height of the query point's Y — the same
        // question `reachmap` asks — so the two pictures can be compared cell for cell. A column
        // whose only surfaces are other storeys shows 'o': the grid has them, this picture does
        // not claim them as the floor you are on.
        var perColumn = new Dictionary<int, List<(float Height, byte State)>>();
        for (var i = 0; i < cells.Columns.Length; ++i)
        {
            if (!perColumn.TryGetValue(cells.Columns[i], out var surfaces))
                perColumn[cells.Columns[i]] = surfaces = [];
            surfaces.Add((cells.Heights![i], (byte)cells.States![i]));
        }

        var startColumn = ColumnOf(cells, starts: start);
        int reachableCols = 0, cutOffCols = 0, cutOffAtOwnHeight = 0, otherStorey = 0, noMesh = 0;
        Console.WriteLine();
        for (var zi = 0; zi < cells.Depth; ++zi)
        {
            var row = new System.Text.StringBuilder();
            for (var xi = 0; xi < cells.Width; ++xi)
            {
                var column = zi * cells.Width + xi;
                if (!perColumn.TryGetValue(column, out var surfaces))
                {
                    ++noMesh;
                    row.Append(' ');
                    continue;
                }

                var here = surfaces
                    .Where(s => MathF.Abs(s.Height - point.Y) < ReachableCellsQuery.SurfaceMergeHeight)
                    .OrderByDescending(s => s.Height)
                    .ToList();
                if (here.Count == 0)
                {
                    ++otherStorey;
                    row.Append('o');
                    continue;
                }

                var best = here[0];
                if (best.State == ReachableCellsQuery.StateCutOff)
                {
                    ++cutOffCols;
                    if (MathF.Abs(best.Height - point.Y) <= 1f)
                        ++cutOffAtOwnHeight;
                    row.Append('#');
                }
                else
                {
                    ++reachableCols;
                    row.Append(column == startColumn ? '@' : '.');
                }
            }
            Console.WriteLine("  " + row);
        }

        Console.WriteLine();
        Console.WriteLine($"  {reachableCols} reachable columns, {cutOffCols} cut off, {otherStorey} another storey only, {noMesh} with no walkable surface");
        if (cutOffCols > 0)
        {
            // Cut off *at your own height* is the interesting case: same floor, no route. Cut off
            // well above or below is another storey doing its job.
            Console.WriteLine($"  of the cut off: {cutOffAtOwnHeight} at your own height (+-1y), "
                + $"{cutOffCols - cutOffAtOwnHeight} on another level");
        }
        Console.WriteLine($"  @ you   . walkable & reachable   # walkable but cut off from here"
            + $"   o other storey only (outside +-{ReachableCellsQuery.SurfaceMergeHeight:f0}y)   (blank) no walkable surface");

        if (perColumn.TryGetValue(startColumn, out var stack))
        {
            Console.WriteLine();
            Console.WriteLine($"surfaces in the start column ({stack.Count}):");
            foreach (var surface in stack.OrderByDescending(s => s.Height))
                Console.WriteLine($"    y {surface.Height,9:f1}  {(surface.State == ReachableCellsQuery.StateCutOff ? "cutOff" : "reachable")}"
                    + $"  ({surface.Height - start.Y:+0.0;-0.0;0.0} from the snap)");
        }
        return 0;
    }

    /// <summary>A point in the middle of the walkable mass: the poly centre nearest the average
    /// of every poly centre. A convex poly's centre is inside it, so this always starts on the
    /// mesh — a vertex average does not (it lands between floors on stacked-ring layouts).</summary>
    private static Vector3 MiddleOfTheMesh(global::Navmesh.Navmesh navmesh)
    {
        var filter = new DotRecast.Detour.DtQueryDefaultFilter();
        var centres = new List<Vector3>();
        var sum = Vector3.Zero;
        for (var t = 0; t < navmesh.Mesh.GetMaxTiles(); ++t)
        {
            var tile = navmesh.Mesh.GetTile(t);
            var data = tile?.data;
            if (data?.header == null)
                continue;
            var refBase = navmesh.Mesh.GetPolyRefBase(tile);
            for (var p = 0; p < data.header.polyCount; ++p)
            {
                var poly = data.polys[p];
                if (poly.GetPolyType() != 0 || !filter.PassFilter(refBase | (uint)p, tile, poly))
                    continue;
                var centre = Vector3.Zero;
                for (var v = 0; v < poly.vertCount; ++v)
                {
                    var i = poly.verts[v] * 3;
                    centre += new Vector3(data.verts[i], data.verts[i + 1], data.verts[i + 2]);
                }
                centre /= poly.vertCount;
                centres.Add(centre);
                sum += centre;
            }
        }
        if (centres.Count == 0)
            return Vector3.Zero;

        var mean = sum / centres.Count;
        var best = centres[0];
        var bestDistance = float.MaxValue;
        foreach (var centre in centres)
        {
            var distance = Vector3.DistanceSquared(centre, mean);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = centre;
            }
        }
        return best;
    }

    private static int ColumnOf(ReachableCellsResponse cells, Vector3 starts)
    {
        var xi = (int)MathF.Floor((starts.X - cells.Origin![0]) / cells.CellSize);
        var zi = (int)MathF.Floor((starts.Z - cells.Origin[1]) / cells.CellSize);
        return zi * cells.Width + xi;
    }

    private static float Arg(string[] args, int index, float fallback) =>
        args.Length > index && float.TryParse(args[index], out var v) ? v : fallback;
}

