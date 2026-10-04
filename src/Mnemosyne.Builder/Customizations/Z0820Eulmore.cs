using System.Numerics;

namespace Navmesh.Customizations;

// Mnemosyne's own, not vendored from vnavmesh (2026-10-02).
//
// The Canopy's curved staircases up to the round platforms climb in steps between 0.75 and 1.0 y,
// over the 0.5 y a character may step up by default, so every step is a ledge and the platform
// tops are islands of their own. Questionable's quest data walks those stairs with its own
// movement (DisableNavmesh) for that reason, and Odysseus stalled on the step after them: the mesh
// sent Miso'rry to the floor under the platform. Built from her capture at 0.75 the stairs still
// break; at 1.0 the route from the floor reaches the top (7 waypoints, 13 m). Across the zone that
// joins 16 small islands and adds ~6,000 m2 of walkable ground; the large areas stay apart.
// Same lever vnavmesh pulls for Amdapor's web bridges (Z0519, 0.75).
//
// v2: the quest barrier across the Skyfront passage to the aetheryte platform (two 10 x 12 y boxes
// in planevent QB_LucKme102_002) is left out. It is active by default until that quest is done,
// so the mesh walled the passage off for everyone; a character past the quest walks straight
// through, and one mesh serves the whole fleet. Reported by the Odysseus session the same day.
//
// v3: the 1.0 y step applies only around the stair it is for. Zone-wide, it joined ~500 other
// spots (climbcomb: 313 ledge shortcuts, 190 new connections, 141 of those on the Derelicts'
// rubble) - Grakur stalled at one, a Mainstay ramp's side. Other platforms' stairs get a region
// each as they are found; outside the regions the mesh is the 0.5 y one.
[CustomizationTerritory(820)]
internal class Z0820Eulmore : NavmeshCustomization
{
    public override int Version => 4;

    public override (Vector3 Center, Vector3 HalfExtent)[] ClimbRegions =>
    [
        (new(62.4f, 84.8f, -38.6f), new(4f, 3f, 3f)), // Canopy, the platform Miso'rry could not climb to
    ];

    public override string[] DropColliderLayers => ["QB_LucKme102_002"];

    public Z0820Eulmore()
    {
        Settings.AgentMaxClimb = 1.0f;
    }
}
