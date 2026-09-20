using Mnemosyne.Protocol;
using Mnemosyne.Service;
using System.Diagnostics;

namespace Mnemosyne.Cli;

// findpath — one path question, timed, against the op as the service answers it.
//
// Drives ZoneService.Handle in process (the same dispatch, validation, zone loading and DTO the
// pipe server uses), so it runs while a service is already serving a live game session:
// restarting that service to measure a change is not an option, and killing it mid-raid is
// worse. Built for the bounded-search work (PLAN item 6) — a cold fly request now answers
// meshNotReady while the volume loads off-request, so this polls the way a consumer should and
// prints every attempt with its cost.
//
// usage: Mnemosyne.Cli findpath <zone-substring> <fromX> <fromY> <fromZ> <toX> <toY> <toZ> [fly] [--repeat N]
public static class FindPath
{
    public static int Run(string[] args)
    {
        if (args.Length < 8 || !float.TryParse(args[2], out var fx) || !float.TryParse(args[3], out var fy)
            || !float.TryParse(args[4], out var fz) || !float.TryParse(args[5], out var tx)
            || !float.TryParse(args[6], out var ty) || !float.TryParse(args[7], out var tz))
        {
            Console.WriteLine("usage: findpath <zone-substring> <fromX> <fromY> <fromZ> <toX> <toY> <toZ> [fly] [--repeat N]");
            return 1;
        }

        var fly = false;
        if (args.Length > 8 && !args[8].StartsWith("--"))
            fly = string.Equals(args[8], "fly", StringComparison.OrdinalIgnoreCase);

        var repeat = 1;
        var flag = Array.IndexOf(args, "--repeat");
        if (flag >= 0 && flag + 1 < args.Length && int.TryParse(args[flag + 1], out var n))
            repeat = Math.Max(1, n);

        if (Probe.ResolveZoneOrReport(args[1]) is not { } entry)
            return 1;

        var service = new ZoneService();
        Console.WriteLine($"zone: {entry.Key}");
        Console.WriteLine($"mode: {(fly ? "fly" : "walk")}   from ({fx:f1}, {fy:f1}, {fz:f1})   to ({tx:f1}, {ty:f1}, {tz:f1})");
        Console.WriteLine();

        var request = new Request
        {
            Op = "findPath",
            CacheKey = entry.Key,
            From = [fx, fy, fz],
            To = [tx, ty, tz],
            Fly = fly,
        };

        var total = Stopwatch.StartNew();
        for (var attempt = 1; attempt <= 60; ++attempt)
        {
            var sw = Stopwatch.StartNew();
            var resp = service.Handle(request);
            sw.Stop();

            // meshNotReady is a *wait*: the zone or its volume is still loading, and the client
            // is meant to poll rather than sit inside a request past its timeout.
            if (resp is { Ok: false } err && err.Result == "meshNotReady")
            {
                Console.WriteLine($"  wait {attempt,2}   {sw.ElapsedMilliseconds,6} ms  {err.Error}");
                Thread.Sleep(500);
                continue;
            }

            Console.WriteLine($"  first     {sw.ElapsedMilliseconds,6} ms  {Describe(resp)}");
            for (var i = 1; i < repeat; ++i)
            {
                var sw2 = Stopwatch.StartNew();
                var again = service.Handle(request);
                sw2.Stop();
                Console.WriteLine($"  warm {i,-4} {sw2.ElapsedMilliseconds,6} ms  {Describe(again)}");
            }
            Console.WriteLine($"  total including the wait: {total.ElapsedMilliseconds} ms");
            return resp.Ok ? 0 : 2;
        }

        Console.WriteLine("  gave up waiting for meshNotReady to clear");
        return 1;
    }

    private static string Describe(Response? resp)
    {
        if (resp is not FindPathResponse p)
            return resp == null ? "no response" : $"ok={resp.Ok} result={resp.Result ?? "(none)"} error={resp.Error}";

        var nearest = p.Nearest is { Length: >= 3 } n ? $"   nearest ({n[0]:f1}, {n[1]:f1}, {n[2]:f1})" : "";
        var eta = p.EtaSeconds is { } e ? $"   eta {e:f1}s" : "";
        var legs = p.Legs is { Count: > 0 } l ? $"   legs {string.Join(">", l.Select(x => x.Mode))}" : "";
        return $"ok={p.Ok} result={p.Result ?? "(none)"} partial={p.Partial} waypoints={p.Waypoints?.Length ?? 0}{nearest}{eta}{legs}";
    }
}
