using Raylib_cs;
using System.Numerics;

namespace Mnemosyne.Viewer;

// Draws gameplay object markers. Zones hold thousands, so shapes are always drawn but
// text labels only appear for the nearest few - otherwise the screen is unreadable soup.
public static class ObjectMarkers
{
    private const float LabelRange = 90f;
    private const int MaxLabels = 30;

    public static (Color Color, float Size) Style(MarkerKind kind) => kind switch
    {
        MarkerKind.Aetheryte => (new Color(120, 220, 255, 255), 2.2f),
        MarkerKind.EventNpc => (new Color(120, 255, 150, 255), 1.1f),
        MarkerKind.BattleNpc => (new Color(255, 110, 110, 255), 1.0f),
        MarkerKind.EventObject => (new Color(255, 210, 90, 255), 1.1f),
        MarkerKind.Treasure => (new Color(255, 170, 40, 255), 1.4f),
        MarkerKind.PopRange => (new Color(160, 140, 220, 255), 0.8f),
        _ => (new Color(200, 200, 200, 255), 1.4f), // ExitRange
    };

    public static void Draw3D(List<ObjectMarker> markers, Vector3 cameraPos)
    {
        Rlgl.DrawRenderBatchActive();
        Rlgl.DisableDepthTest(); // markers matter more than occlusion in a debug view
        foreach (var m in markers)
        {
            var (color, size) = Style(m.Kind);
            var p = m.Pos + new Vector3(0, size, 0);
            // a zone holds hundreds of NPCs and raylib draws immediate-mode: full spheres
            // here cost ~500 tris each and halved the frame rate. Cubes for the crowd,
            // (low-tessellation) spheres and beacons only for the handful of landmarks.
            if (m.Kind is MarkerKind.Aetheryte or MarkerKind.ExitRange or MarkerKind.Treasure)
            {
                Raylib.DrawSphereEx(p, size, 6, 6, color);
                Raylib.DrawLine3D(m.Pos, m.Pos + new Vector3(0, 14, 0), color);
            }
            else
            {
                Raylib.DrawCubeV(p, new Vector3(size * 1.6f), color);
            }
        }
        Rlgl.DrawRenderBatchActive();
        Rlgl.EnableDepthTest();
    }

    public static void DrawLabels(List<ObjectMarker> markers, Camera3D camera)
    {
        var near = markers
            .Select(m => (Marker: m, Dist: Vector3.Distance(m.Pos, camera.Position)))
            .Where(x => x.Dist < LabelRange)
            .OrderBy(x => x.Dist)
            .Take(MaxLabels);
        foreach (var (marker, dist) in near)
        {
            var (color, size) = Style(marker.Kind);
            var screen = Raylib.GetWorldToScreen(marker.Pos + new Vector3(0, size * 2 + 1, 0), camera);
            // GetWorldToScreen projects points behind the camera too; cull those
            if (Vector3.Dot(marker.Pos - camera.Position, camera.Target - camera.Position) <= 0)
                continue;
            if (screen.X is < -200 or > 4000 || screen.Y is < -200 or > 4000)
                continue;
            int fade = (int)(255 * (1 - dist / LabelRange));
            Raylib.DrawText(marker.Label, (int)screen.X + 6, (int)screen.Y - 8, 16,
                new Color(color.R, color.G, color.B, (byte)Math.Clamp(fade + 90, 0, 255)));
        }
    }
}
