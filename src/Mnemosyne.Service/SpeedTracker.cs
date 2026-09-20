using System.Numerics;
using System.Text.Json;

namespace Mnemosyne.Service;

// Derives movement speeds from the 10 Hz position stream: instantaneous speed from
// consecutive samples, plus self-calibrating sustained maxima per mode (ground/fly).
// A sample only calibrates after a short streak of consistent readings, which filters
// teleports, zone loads, knockbacks and lag spikes. Calibration persists to disk.
public sealed class SpeedTracker
{
    private sealed class Calibration
    {
        public float? Ground { get; set; }
        public float? Fly { get; set; }
    }

    private static readonly string PersistPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mnemosyne", "speeds.json");

    // Ground speed is measured HORIZONTALLY and capped tighter: a fall has constant terminal
    // velocity, which is exactly the "sustained consistent streak" calibration looks for, and
    // it once poisoned ground to 54.9 y/s. Real ground movement is essentially horizontal.
    private const float MaxGround = 25f;      // y/s; no ground mount comes close
    private const float MaxFly = 60f;         // flight is genuinely fast and genuinely 3D
    private const float MinCalibratable = 1f; // ignore idle drift
    private const int StreakNeeded = 3;       // consecutive ~1 s windows agreeing
    private const float StreakTolerance = 0.12f; // receive-time jitter makes per-sample dt noisy
    private const long WindowMs = 900;
    private const long MinWindowMs = 300;

    private readonly Calibration _calibration;
    private readonly object _lock = new();

    // Per-zone-key tracking state. Both the sample window AND the consistency streak must be
    // keyed: with a shared streak, a second pusher (fleet client, test harness, or the same
    // player idling in another zone) resets the streak on every alternating sample and
    // calibration never accumulates. Speed is measured across ~WindowMs so 10 Hz receive-time
    // jitter (which sank the original per-sample-delta approach) averages out.
    private sealed class Track
    {
        public readonly Queue<(Vector3 Pos, long TickMs)> Samples = new();
        public float StreakSpeed;
        public int StreakCount;
        public bool StreakFlying;
        public long LastSeenMs;
    }

    private readonly Dictionary<string, Track> _tracks = [];
    private long _lastSaveMs;

    public float CurrentSpeed { get; private set; }
    public float? GroundSpeed { get { lock (_lock) return _calibration.Ground; } }
    public float? FlySpeed { get { lock (_lock) return _calibration.Fly; } }

    public SpeedTracker()
    {
        try
        {
            _calibration = File.Exists(PersistPath)
                ? JsonSerializer.Deserialize<Calibration>(File.ReadAllText(PersistPath)) ?? new()
                : new();
        }
        catch
        {
            _calibration = new();
        }
    }

    public void OnSample(string? cacheKey, Vector3 pos, bool flying, float? pushedSpeed, long tickMs)
    {
        var key = cacheKey ?? "";
        if (!_tracks.TryGetValue(key, out var track))
            _tracks[key] = track = new();
        track.LastSeenMs = tickMs;
        var window = track.Samples;
        while (window.Count > 0 && tickMs - window.Peek().TickMs > WindowMs)
            window.Dequeue();
        (Vector3 Pos, long TickMs) oldest = window.Count > 0 ? window.Peek() : (Vector3.Zero, -1L);
        window.Enqueue((pos, tickMs));
        if (_tracks.Count > 8) // prune keys nobody is pushing anymore
            foreach (var stale in _tracks.Where(kv => tickMs - kv.Value.LastSeenMs > 30_000).Select(kv => kv.Key).ToList())
                _tracks.Remove(stale);

        float cap = flying ? MaxFly : MaxGround;
        float? measured = null;
        if (pushedSpeed is { } exact && exact >= 0 && exact <= cap)
        {
            measured = exact; // client knows better than position deltas
        }
        else if (oldest.TickMs >= 0 && tickMs - oldest.TickMs >= MinWindowMs)
        {
            float dt = (tickMs - oldest.TickMs) / 1000f;
            var delta = pos - oldest.Pos;
            if (!flying)
                delta.Y = 0; // ignore falls/drops; ground travel is horizontal
            float v = delta.Length() / dt;
            if (v <= cap)
                measured = v;
        }

        if (measured is not { } speed)
        {
            track.StreakCount = 0;
            return;
        }
        CurrentSpeed = speed;

        // calibrate only on a sustained streak of near-identical readings in one mode
        if (speed < MinCalibratable)
        {
            track.StreakCount = 0;
            return;
        }
        if (track.StreakCount > 0 && flying == track.StreakFlying
            && MathF.Abs(speed - track.StreakSpeed) <= track.StreakSpeed * StreakTolerance)
        {
            ++track.StreakCount;
            track.StreakSpeed = (track.StreakSpeed * (track.StreakCount - 1) + speed) / track.StreakCount;
        }
        else
        {
            track.StreakSpeed = speed;
            track.StreakCount = 1;
            track.StreakFlying = flying;
        }
        if (track.StreakCount < StreakNeeded)
            return;

        lock (_lock)
        {
            float? known = track.StreakFlying ? _calibration.Fly : _calibration.Ground;
            if (known == null || track.StreakSpeed > known.Value)
            {
                if (track.StreakFlying)
                    _calibration.Fly = track.StreakSpeed;
                else
                    _calibration.Ground = track.StreakSpeed;
                Console.WriteLine($"speed calibrated: {(track.StreakFlying ? "fly" : "ground")} = {track.StreakSpeed:f2} y/s");
                if (tickMs - _lastSaveMs > 5000)
                {
                    _lastSaveMs = tickMs;
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(PersistPath)!);
                        File.WriteAllText(PersistPath, JsonSerializer.Serialize(_calibration));
                    }
                    catch
                    {
                        // persistence is best-effort
                    }
                }
            }
        }
    }
}
