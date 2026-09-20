using System.Numerics;

namespace Mnemosyne.Core;

public enum TravelMode { Walk, Fly }

public sealed record PathLeg(TravelMode Mode, List<Vector3> Waypoints, float Length, float Seconds);

public sealed record TravelPlan(List<PathLeg> Legs, float TotalSeconds, string Summary)
{
    /// <summary>Every waypoint in order, legs joined at their shared transition points.</summary>
    public List<Vector3> Flatten()
    {
        var all = new List<Vector3>();
        foreach (var leg in Legs)
            foreach (var w in leg.Waypoints)
                if (all.Count == 0 || Vector3.DistanceSquared(all[^1], w) > 0.01f)
                    all.Add(w);
        return all;
    }
}

// PLAN milestone 11: decide the travel MODE instead of making the caller pick. Compares a
// pure ground route against an air-assisted one (walk out -> mount -> fly -> land -> walk in)
// on TIME, using the speeds calibrated from real play, and returns typed legs.
// The cave case falls out naturally: air directly above an indoor goal is blocked, so the
// landing lands on the surface and the tail walk routes in through the actual entrance.
public sealed class MultiModalPlanner(MeshPathfinder ground, FlightPathfinder? flight)
{
    /// <summary>How far below an airborne start to look for the ground the backbone runs on.
    /// Generous: cruising altitude over a valley is a long way up.</summary>
    public const float GroundSnapHeight = 200f;

    public const float MountSeconds = 3.0f;    // summon animation, roughly
    public const float DismountSeconds = 1.0f;
    private const float AscendStep = 4f;
    private const float MinClearHeight = 8f;   // ground clutter band you rise through anyway
    private const float CruiseHeight = 32f;    // altitude the flight legs use

    /// <summary>Why the air-assisted option was rejected (diagnostics).</summary>
    public string AirReason { get; private set; } = "";

    public TravelPlan? Plan(Vector3 from, Vector3 to, float groundSpeed, float flySpeed,
        IReadOnlyList<OverrideLink> links)
    {
        groundSpeed = MathF.Max(groundSpeed, 0.5f);
        flySpeed = MathF.Max(flySpeed, 0.5f);

        // option A: stay on the ground
        TravelPlan? walkPlan = null;
        var walkLeg = TryWalkLeg(from, to, links, groundSpeed);
        if (walkLeg is { } leg)
            walkPlan = new([leg], leg.Seconds, $"walk {leg.Length:f0}m");

        // option B: air-assisted
        var airPlan = PlanAirAssisted(from, to, walkLeg, groundSpeed, flySpeed, links);

        if (walkPlan == null && airPlan == null)
            return null;
        if (walkPlan == null)
            return airPlan;
        if (airPlan == null)
            return walkPlan;
        return airPlan.TotalSeconds < walkPlan.TotalSeconds ? airPlan : walkPlan;
    }

    // Uses the ground route as the backbone and replaces its middle with flight: walk until
    // the sky opens, fly the long haul, land at the last open-sky point on the route, walk the
    // rest. The tail is therefore a segment of an already-valid ground path - and the
    // cave/indoor case falls out for free, because the sky is closed near such goals so the
    // landing point slides back along the route to the entrance.
    private TravelPlan? PlanAirAssisted(Vector3 from, Vector3 to, PathLeg? walkLeg,
        float groundSpeed, float flySpeed, IReadOnlyList<OverrideLink> links)
    {
        if (flight == null)
        {
            AirReason = "no flight volume";
            return null;
        }

        var route = walkLeg?.Waypoints ?? [from, to]; // no ground route: gate-to-gate flight
        int takeoffIdx = -1, landingIdx = -1;
        for (int i = 0; i < route.Count; ++i)
            if (HasClearSky(route[i]))
            {
                takeoffIdx = i;
                break;
            }
        for (int i = route.Count - 1; i >= 0; --i)
            if (HasClearSky(route[i]))
            {
                landingIdx = i;
                break;
            }
        if (takeoffIdx < 0 || landingIdx <= takeoffIdx)
        {
            AirReason = takeoffIdx < 0 ? "no open sky along the route" : "open sky at only one point";
            return null;
        }

        var takeoffGround = route[takeoffIdx];
        var landingGround = route[landingIdx];
        var air = flight.FindPath(takeoffGround + new Vector3(0, CruiseHeight, 0),
            landingGround + new Vector3(0, CruiseHeight, 0), out var partial);
        if (partial || air.Count < 2)
        {
            AirReason = $"no air route ({flight.LastOutcome})";
            return null;
        }

        var legs = new List<PathLeg>();
        float seconds = MountSeconds + DismountSeconds;

        if (takeoffIdx > 0) // walk to the first spot with open sky
        {
            var head = MakeLeg(TravelMode.Walk, route.GetRange(0, takeoffIdx + 1), groundSpeed);
            legs.Add(head);
            seconds += head.Seconds;
        }

        List<Vector3> flightPoints = [takeoffGround, .. air, landingGround]; // climb, cruise, descend
        var flyLeg = MakeLeg(TravelMode.Fly, flightPoints, flySpeed);
        legs.Add(flyLeg);
        seconds += flyLeg.Seconds;

        float tailLength = 0;
        if (landingIdx < route.Count - 1) // walk the remainder of the ground route
        {
            var tail = MakeLeg(TravelMode.Walk, route.GetRange(landingIdx, route.Count - landingIdx), groundSpeed);
            legs.Add(tail);
            seconds += tail.Seconds;
            tailLength = tail.Length;
        }

        float headLength = legs.Where(l => l.Mode == TravelMode.Walk).Sum(l => l.Length) - tailLength;
        var summary = (headLength > 1 ? $"walk {headLength:f0}m + " : "")
            + $"mount + fly {flyLeg.Length:f0}m"
            + (tailLength > 1 ? $" + walk {tailLength:f0}m" : "");
        return new(legs, seconds, summary);
    }

    // is the sky above p open from the clutter band up to cruise altitude?
    private bool HasClearSky(Vector3 p)
    {
        if (flight == null)
            return false;
        for (float h = MinClearHeight; h <= CruiseHeight; h += AscendStep)
        {
            int leaf = flight.Nav.LeafAt(p + new Vector3(0, h, 0));
            if (leaf < 0 || flight.Nav.Nodes[leaf].State != FlightNav.StateEmpty)
                return false;
        }
        return true;
    }

    // A complete walk leg, or null when the goal isn't reachable on foot. Note the
    // degenerate case: two points inside the SAME polygon produce a single-waypoint
    // straight path - that's a success (a straight line within one convex poly is safe),
    // not a failure, and rejecting it once threw away otherwise-valid air routes.
    private PathLeg? TryWalkLeg(Vector3 from, Vector3 to, IReadOnlyList<OverrideLink> links, float groundSpeed)
    {
        var walk = ground.FindWalkPath(from, to, links);
        if (walk is not { Partial: false })
        {
            // The caller is usually already airborne when they ask to fly, so `from` is off
            // the mesh and there is no ground route at all. Without one the backbone collapses
            // to a straight [from, to] line, the landing point has nowhere to slide back to,
            // and the route flies through the front door of the building it should land
            // outside. Snapping to the ground under the start restores the backbone: you are
            // going to land somewhere anyway, so plan the walk from where that would be.
            if (!ground.TryNearestGround(from, GroundSnapHeight, out var landed))
                return null;
            walk = ground.FindWalkPath(landed, to, links);
            if (walk is not { Partial: false })
                return null;
            from = landed;
        }
        var points = walk.Waypoints.Count >= 2 ? walk.Waypoints : [from, to];
        return MakeLeg(TravelMode.Walk, points, groundSpeed);
    }


    private static PathLeg MakeLeg(TravelMode mode, List<Vector3> waypoints, float speed)
    {
        float length = 0;
        for (int i = 1; i < waypoints.Count; ++i)
            length += Vector3.Distance(waypoints[i - 1], waypoints[i]);
        return new(mode, waypoints, length, length / speed);
    }
}
