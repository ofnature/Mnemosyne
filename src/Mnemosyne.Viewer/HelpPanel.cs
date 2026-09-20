using Raylib_cs;

namespace Mnemosyne.Viewer;

// Full-command overlay. H cycles: hint line -> this panel -> hidden.
public static class HelpPanel
{
    private static readonly (string Section, (string Key, string What)[] Rows)[] Sections =
    [
        ("Camera", [
            ("RMB drag", "look around"),
            ("W A S D", "move"),
            ("Q / E  (Ctrl/Space)", "down / up"),
            ("Shift / Alt", "fast / slow"),
            ("wheel", "dolly"),
            ("MMB drag", "pan"),
        ]),
        ("Zones", [
            ("Tab", "master zone list (all 590 zones)"),
            ("click / Enter", "load zone - or build it from game files if uncached"),
            ("type / wheel", "filter / scroll the list"),
            ("P", "follow player + auto zone switch"),
        ]),
        ("Pathfinding", [
            ("LMB", "place start, then destination"),
            ("F", "mode: walk / fly / auto (multi-modal)"),
            ("C", "clear path"),
            ("T", "travel the path at real movement speed"),
            ("Shift+T", "chase camera rides the traveler"),
            (", / .", "playback speed (0.25x - 16x)"),
        ]),
        ("Display", [
            ("N", "navmesh overlay on/off"),
            ("F2", "navmesh: solid+wires / solid / wires"),
            ("V", "flying volume on/off"),
            ("G", "zone geometry on/off"),
            ("F3", "collision geometry - what Recast rasterized"),
            ("O", "object markers - aetherytes, NPCs, exits, treasure"),
        ]),
        ("Edit mode (E)", [
            ("X / U", "block / unblock brush"),
            ("[ ]", "brush size"),
            ("B", "box tool (two clicks)"),
            ("K", "prune: click seed, everything unreachable gets blocked"),
            ("L", "link tool (two clicks, green arc)"),
            ("click a door", "toggle its footprint blocked"),
            ("R", "remove last edit"),
            ("Ctrl+S", "save overrides"),
        ]),
        ("Other", [
            ("H", "help: hint line / this panel / hidden"),
            ("Esc", "quit (or close picker)"),
        ]),
    ];

    public static void Draw()
    {
        int w = 620;
        int rows = Sections.Sum(s => s.Rows.Length + 2);
        int h = rows * 22 + 20;
        int x = Raylib.GetScreenWidth() - w - 20;
        int y = 70;
        Raylib.DrawRectangle(x, y, w, h, new Color(15, 18, 25, 235));
        Raylib.DrawRectangleLines(x, y, w, h, new Color(90, 120, 160, 255));

        int cy = y + 10;
        foreach (var (section, sectionRows) in Sections)
        {
            Raylib.DrawText(section, x + 14, cy, 18, new Color(120, 190, 255, 255));
            cy += 24;
            foreach (var (key, what) in sectionRows)
            {
                Raylib.DrawText(key, x + 30, cy, 18, new Color(255, 220, 130, 255));
                Raylib.DrawText(what, x + 220, cy, 18, new Color(190, 200, 210, 255));
                cy += 22;
            }
            cy += 20;
        }
    }
}
