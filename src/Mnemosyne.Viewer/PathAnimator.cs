using Raylib_cs;
using System.Numerics;

namespace Mnemosyne.Viewer;

// Travels a computed path in real time at the calibrated movement speed: a walker for
// ground paths, a flier for volume paths. Purely a visualization of what the ETA means -
// the traveler follows the same waypoints Ariadne would.
public sealed class PathAnimator
{
    // used only until the service has calibrated the real thing (base run / basic flight)
    public const float DefaultGroundSpeed = 6.0f;
    public const float DefaultFlySpeed = 20.0f;

    public bool Playing { get; private set; }
    public bool Chase;          // camera rides along
    public float Rate = 1f;     // playback multiplier
    public float Traveled { get; private set; }
    public float TotalLength { get; private set; }
    public Vector3 Position { get; private set; }
    public Vector3 Heading { get; private set; } = Vector3.UnitX;

    public bool HasPath => TotalLength > 0.01f;
    public bool Finished => HasPath && Traveled >= TotalLength;

    private object? _pathToken;

    /// <summary>Re-arms when the tool hands us a different path instance.</summary>
    public void Sync(List<Vector3> path)
    {
        if (ReferenceEquals(path, _pathToken) && (path.Count >= 2) == HasPath)
            return;
        _pathToken = path;
        TotalLength = 0;
        for (int i = 1; i < path.Count; ++i)
            TotalLength += Vector3.Distance(path[i - 1], path[i]);
        Traveled = 0;
        Playing = false;
        if (path.Count > 0)
            Position = path[0];
    }

    public void Toggle()
    {
        if (!HasPath)
            return;
        if (Finished)
            Traveled = 0; // replay
        Playing = !Playing;
    }

    public void Stop()
    {
        Playing = false;
        Traveled = 0;
    }

    public float CurrentSpeed { get; private set; }

    public void Update(List<Vector3> path, Func<float, float> speedAt, float dt)
    {
        Sync(path);
        if (!HasPath)
            return;
        // speed is sampled at the current distance so multi-modal legs travel at their own
        // pace: the traveler visibly speeds up on mounting and slows down after landing
        float speed = speedAt(Traveled);
        CurrentSpeed = speed;
        if (Playing)
        {
            Traveled += speed * Rate * dt;
            if (Traveled >= TotalLength)
            {
                Traveled = TotalLength;
                Playing = false;
            }
        }
        Sample(path, Traveled);
    }

    // walk the polyline to the given arc length
    private void Sample(List<Vector3> path, float distance)
    {
        float remaining = distance;
        for (int i = 1; i < path.Count; ++i)
        {
            var a = path[i - 1];
            var b = path[i];
            float segment = Vector3.Distance(a, b);
            if (segment <= 1e-4f)
                continue;
            if (remaining <= segment)
            {
                float t = remaining / segment;
                Position = Vector3.Lerp(a, b, t);
                Heading = (b - a) / segment;
                return;
            }
            remaining -= segment;
        }
        Position = path[^1];
        if (path.Count >= 2)
        {
            var d = path[^1] - path[^2];
            if (d.LengthSquared() > 1e-6f)
                Heading = Vector3.Normalize(d);
        }
    }

    public string Status(float totalSeconds, bool calibrated)
    {
        if (!HasPath)
            return "";
        var state = Playing ? "travel" : Finished ? "arrived" : "paused";
        var rate = MathF.Abs(Rate - 1f) < 0.01f ? "" : $" {Rate:0.##}x";
        var progress = TotalLength > 0 ? Traveled / TotalLength : 0;
        return $"{state}: {Traveled:f0}/{TotalLength:f0}m  {progress * totalSeconds:f0}/{totalSeconds:f0}s"
            + $"  now {CurrentSpeed:f1} y/s ({(calibrated ? "calibrated" : "default")}){rate}{(Chase ? "  [chase]" : "")}";
    }

    public void Draw(bool flying)
    {
        if (!HasPath)
            return;
        // colour follows the mode actually in use right now, so the mount/dismount is visible
        var color = flying ? new Color(255, 130, 255, 255) : new Color(255, 200, 60, 255);
        var lift = new Vector3(0, 0.4f, 0);
        var p = Position + lift;
        Raylib.DrawSphere(p, 1.1f, color);
        Raylib.DrawCylinderEx(p, p + Heading * 4.5f, 0.5f, 0.02f, 10, color); // heading arrow
        Raylib.DrawLine3D(Position, Position + new Vector3(0, flying ? -12 : 8, 0), color);
    }
}
