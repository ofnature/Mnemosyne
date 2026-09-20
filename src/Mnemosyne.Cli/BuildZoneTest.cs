using Lumina;
using Mnemosyne.Builder;
using Mnemosyne.Client;
using Mnemosyne.Core;
using Mnemosyne.Protocol;

namespace Mnemosyne.Cli;

// buildZone end to end through the pipe, which the conformance harness can only half-prove:
// it checks the ack, but a build already in flight makes the ack a no-op. This sends a real
// scene, waits for the build, and confirms a usable mesh comes out the far side.
//
// The capture is read from game files rather than the game process — the wire path and the
// build are identical either way (capture-test proves that), and this way it runs headless.
//
// usage: Mnemosyne.Cli buildzone-test [zone-substring]
public static class BuildZoneTest
{
    public static async Task<int> RunAsync(string? hint)
    {
        var sqpack = GamePaths.FindSqpackDir();
        if (sqpack == null)
        {
            Console.WriteLine("no game install found");
            return 1;
        }

        // A zone with no built file, so the build is real work and the result is unambiguous.
        var built = ZoneService_BuiltDirectory();
        var cached = MeshCache.Enumerate().Where(e => e.IsSupported).Select(e => e.Key).ToList();
        var target = ZoneNames.All
            .Where(z => z.Value.Bg is { Length: > 0 })
            .Where(z => hint is not { Length: > 0 } || z.Key.Contains(hint, StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault(z => !File.Exists(Path.Combine(built, z.Key + ".navmesh"))
                && !cached.Any(k => k.StartsWith(z.Key, StringComparison.OrdinalIgnoreCase)));
        if (target.Key == null)
        {
            Console.WriteLine("no unbuilt zone available to test with");
            return 1;
        }
        Console.WriteLine($"zone: {target.Value.Name} ({target.Key})");

        using var client = new MnemosyneClient();
        try
        {
            await client.ConnectAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"cannot connect: {ex.Message} - is Mnemosyne.Service running?");
            return 1;
        }

        var before = await client.ZoneStatusAsync(target.Key);
        Console.WriteLine($"before: status={before.Status}");
        if (before.Status == "cached")
        {
            Console.WriteLine("zone is already cached - pick another");
            return 1;
        }

        Console.WriteLine("reading the scene from game files...");
        var scene = CapturedScene.CaptureOffline(new GameData(sqpack), target.Value.Bg, target.Key);
        Console.WriteLine($"capture: {scene.InstanceCount} instances, {scene.Terrains.Length} terrains");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ack = await client.SendAsync<Response>(new Request { Op = "buildZone", CacheKey = target.Key, Scene = scene });
        var ackMs = sw.ElapsedMilliseconds; // the stopwatch keeps running for the build; latch it
        Console.WriteLine($"buildZone ack: ok={ack.Ok} result={ack.Result ?? "<none>"} in {ackMs} ms");
        if (!ack.Ok)
        {
            Console.WriteLine($"rejected: {ack.Error}");
            return 1;
        }
        if (ackMs >= 2000)
        {
            Console.WriteLine("FAIL: the ack blocked - buildZone must never hold the request");
            return 1;
        }

        Console.WriteLine("polling zoneStatus...");
        var deadline = DateTime.UtcNow.AddMinutes(5);
        var sawBuilding = false;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(3000);
            var status = await client.ZoneStatusAsync(target.Key);
            if (status.Status == "building" || status.Building)
            {
                sawBuilding = true;
                Console.WriteLine($"  building... {status.Progress * 100:f0}%");
                continue;
            }
            if (status.Status == "cached")
            {
                var mesh = await client.GetMeshAsync(target.Key);
                var valid = mesh is { Ok: true } && File.Exists(mesh.Path);
                Console.WriteLine();
                Console.WriteLine($"  [PASS] ack was immediate                     {ackMs} ms");
                Console.WriteLine($"  [{(sawBuilding ? "PASS" : "WARN")}] zoneStatus reported the build      {(sawBuilding ? "yes" : "finished before the first poll")}");
                Console.WriteLine($"  [{(valid ? "PASS" : "FAIL")}] a usable mesh came out             {mesh.Path ?? mesh.Error ?? ""}");
                if (valid)
                    Console.WriteLine($"         {new FileInfo(mesh.Path!).Length / 1024} KB, version {mesh.Version}, built in ~{sw.Elapsed.TotalSeconds:f0}s");
                Console.WriteLine();
                Console.WriteLine(valid ? "buildZone verified end to end" : "buildZone FAILED");
                return valid ? 0 : 1;
            }
            Console.WriteLine($"  status={status.Status}");
        }
        Console.WriteLine("timed out after 5 min");
        return 1;
    }

    private static string ZoneService_BuiltDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mnemosyne", "built");
}
