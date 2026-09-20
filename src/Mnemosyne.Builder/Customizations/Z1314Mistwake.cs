namespace Navmesh.Customizations;

// Mistwake (territory 1314) fragments into 1,075 islands for 8,837 polys, and the cause is the
// rasterizer's ledge filter - not missing geometry, and not erosion.
//
// Evidence, measured 2026-09-20:
//  - The floor is built from separate platform pieces (`bgparts/x6d9_a2_plt*`), and the layout has
//    no door-like objects at all (`doors x6d9` reports none), so nothing here is a doorway to link.
//  - Neighbouring floor islands sit 1.2-2.3 m apart with a 0-2 m drop (`components x6d9`).
//  - Halving the agent radius made it *worse*, not better: 13,021 polys in 1,412 islands. So the
//    seams are not erosion arithmetic either.
//
// What is left is `FilterLedgeSpans`: it marks a span non-walkable when its neighbour column is too
// far below, and the gap between two floor pieces reads exactly like that - so every piece boundary
// loses both edges and a continuous floor becomes a chain of islands. A duty route then dies at a
// seam the player cannot even see.
//
// This is the same call two other dungeons already ship in their customizations
// (Z1044 The Praetorium, Z1142 The Sirensong Sea - "this allows mesh to go down the bowsprit to the
// land from the boat"). The trade is deliberate and duty-shaped: without the filter a path may run
// to a ledge the agent could fall off, where a route that exists beats a route that refuses, and the
// game's own collision still decides what actually happens.
[CustomizationTerritory(1314)]
class Z1314Mistwake : NavmeshCustomization
{
    public override int Version => 1;

    public Z1314Mistwake()
    {
        Settings.Filtering -= NavmeshSettings.Filter.LedgeSpans;
    }
}
