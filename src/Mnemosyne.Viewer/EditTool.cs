using Mnemosyne.Builder;
using DotRecast.Detour;
using Mnemosyne.Core;
using Raylib_cs;
using System.Numerics;

namespace Mnemosyne.Viewer;


// Mesh edit mode (E): paint block/unblock shapes, box select, prune, custom links, and
// door toggles. Edits mutate the loaded DtNavMesh immediately (via OverrideStore.Apply)
// and persist as sidecar overrides with Ctrl+S.
public sealed class EditTool
{
    public bool Active;
    public bool Dirty;
    public string Mode = "block"; // block | unblock | box | link | prune
    public float BrushRadius = 6f;
    public ZoneOverrides Overrides { get; private set; } = new();
    public List<DoorMarker> Doors = [];

    /// <summary>What execution and the door audit learned about this zone. Shown, never
    /// applied: the operator decides which candidates become real edits.</summary>
    public ZoneEvidence Evidence = new();
    public event Action? MeshEdited;

    private DtNavMesh? _mesh;
    private string _cacheKey = "";
    private int[][] _flagSnapshot = [];
    private Vector3? _pendingFirst; // first corner (box) or first endpoint (link)

    public void SetZone(DtNavMesh mesh, string cacheKey, ZoneOverrides overrides, int[][] flagSnapshot, List<DoorMarker> doors)
    {
        _mesh = mesh;
        _cacheKey = cacheKey;
        Overrides = overrides;
        _flagSnapshot = flagSnapshot;
        Doors = doors;
        Evidence = EvidenceStore.Load(cacheKey);
        Dirty = false;
        _pendingFirst = null;
    }

    // capture pre-override flags so edits can be reverted and reapplied from scratch
    public static int[][] SnapshotFlags(DtNavMesh mesh)
    {
        var snapshot = new int[mesh.GetMaxTiles()][];
        for (int t = 0; t < mesh.GetMaxTiles(); ++t)
        {
            var data = mesh.GetTile(t)?.data;
            if (data?.header == null)
                continue;
            var flags = new int[data.header.polyCount];
            for (int p = 0; p < data.header.polyCount; ++p)
                flags[p] = data.polys[p].flags;
            snapshot[t] = flags;
        }
        return snapshot;
    }

    private void RestoreSnapshot()
    {
        if (_mesh == null)
            return;
        for (int t = 0; t < _flagSnapshot.Length; ++t)
        {
            var flags = _flagSnapshot[t];
            var data = _mesh.GetTile(t)?.data;
            if (flags == null || data?.header == null)
                continue;
            for (int p = 0; p < flags.Length && p < data.header.polyCount; ++p)
                data.polys[p].flags = flags[p];
        }
    }

    private void ReapplyAll()
    {
        if (_mesh == null)
            return;
        RestoreSnapshot();
        OverrideStore.Apply(_mesh, Overrides);
        Dirty = true;
        MeshEdited?.Invoke();
    }

    public void OnClick(Vector3 hit)
    {
        if (_mesh == null)
            return;

        // door markers win over painting when the click lands inside one
        var door = Doors.FirstOrDefault(d =>
            MathF.Abs(hit.X - d.Pos.X) <= d.HalfExtents.X + 1 &&
            MathF.Abs(hit.Y - d.Pos.Y) <= d.HalfExtents.Y + 3 &&
            MathF.Abs(hit.Z - d.Pos.Z) <= d.HalfExtents.Z + 1);
        if (door != null && Mode is "block" or "unblock")
        {
            ToggleDoor(door);
            return;
        }

        switch (Mode)
        {
            case "block" or "unblock":
                Overrides.FlagEdits.Add(new()
                {
                    Kind = "sphere",
                    Center = [hit.X, hit.Y, hit.Z],
                    Extent = [BrushRadius, BrushRadius, BrushRadius],
                    Block = Mode == "block",
                });
                ReapplyAll();
                break;

            case "box" when _pendingFirst == null:
                _pendingFirst = hit;
                break;
            case "box":
            {
                var a = _pendingFirst.Value;
                _pendingFirst = null;
                var center = (a + hit) * 0.5f;
                var extent = new Vector3(
                    MathF.Max(MathF.Abs(hit.X - a.X) * 0.5f, 1),
                    MathF.Max(MathF.Abs(hit.Y - a.Y) * 0.5f, 4),
                    MathF.Max(MathF.Abs(hit.Z - a.Z) * 0.5f, 1));
                Overrides.FlagEdits.Add(new()
                {
                    Kind = "box",
                    Center = [center.X, center.Y, center.Z],
                    Extent = [extent.X, extent.Y, extent.Z],
                    Block = true,
                });
                ReapplyAll();
                break;
            }

            case "link" when _pendingFirst == null:
                _pendingFirst = hit;
                break;
            case "link":
            {
                var a = _pendingFirst.Value;
                _pendingFirst = null;
                Overrides.Links.Add(new() { From = [a.X, a.Y, a.Z], To = [hit.X, hit.Y, hit.Z], Bidirectional = true });
                Dirty = true;
                break;
            }

            case "prune":
                Overrides.PruneSeed = [hit.X, hit.Y, hit.Z];
                ReapplyAll();
                break;
        }
    }

    private void ToggleDoor(DoorMarker door)
    {
        var existing = Overrides.FlagEdits.FirstOrDefault(s => s.InstanceId == door.InstanceId && s.InstanceId != 0);
        if (existing != null)
        {
            Overrides.FlagEdits.Remove(existing);
        }
        else
        {
            Overrides.FlagEdits.Add(new()
            {
                Kind = "box",
                Center = [door.Pos.X, door.Pos.Y, door.Pos.Z],
                Extent = [MathF.Max(door.HalfExtents.X, 2), MathF.Max(door.HalfExtents.Y, 3), MathF.Max(door.HalfExtents.Z, 2)],
                Block = true,
                Note = $"door: {door.Label}",
                AssetPath = door.AssetPath,
                InstanceId = door.InstanceId,
            });
        }
        ReapplyAll();
    }

    public void RemoveLast()
    {
        if (Overrides.Links.Count > 0 && Mode == "link")
        {
            Overrides.Links.RemoveAt(Overrides.Links.Count - 1);
            Dirty = true;
            return;
        }
        if (Overrides.PruneSeed != null && Mode == "prune")
        {
            Overrides.PruneSeed = null;
            ReapplyAll();
            return;
        }
        if (Overrides.FlagEdits.Count > 0)
        {
            Overrides.FlagEdits.RemoveAt(Overrides.FlagEdits.Count - 1);
            ReapplyAll();
        }
    }

    public void Save()
    {
        if (_cacheKey.Length == 0)
            return;
        OverrideStore.Save(_cacheKey, Overrides);
        Dirty = false;
    }

    private static void DrawCandidate(TraversalReport report, Color color)
    {
        if (report.From.Length < 3 || report.To.Length < 3)
            return;
        var a = new Vector3(report.From[0], report.From[1], report.From[2]);
        var b = new Vector3(report.To[0], report.To[1], report.To[2]);
        Raylib.DrawSphereWires(a, 0.6f, 6, 6, color);
        Raylib.DrawSphereWires(b, 0.6f, 6, 6, color);
        // dashed, so a candidate never looks like a committed link
        const int segments = 7;
        for (int i = 0; i < segments; i += 2)
        {
            var t0 = i / (float)segments;
            var t1 = (i + 1) / (float)segments;
            Raylib.DrawLine3D(Vector3.Lerp(a, b, t0), Vector3.Lerp(a, b, t1), color);
        }
    }

    public string StatusLine =>
        $"EDIT [{Mode}]  brush {BrushRadius:f0}m ([/])  X block  U unblock  B box  L link  K prune  R undo last  Ctrl+S save"
        + $"  |  {Overrides.FlagEdits.Count} shapes, {Overrides.Links.Count} links{(Overrides.PruneSeed != null ? ", pruned" : "")}"
        + (Evidence.IsEmpty ? "" : $"  |  {Evidence.LinkCandidates.Count} link / {Evidence.BlockCandidates.Count} block candidates")
        + (Dirty ? "  *UNSAVED*" : "");

    public void Draw()
    {
        Rlgl.DrawRenderBatchActive();
        Rlgl.DisableDepthTest();

        foreach (var d in Doors)
        {
            var blocked = Overrides.FlagEdits.Any(s => s.InstanceId == d.InstanceId && s.InstanceId != 0);
            var color = blocked ? new Color(255, 80, 40, 255) : new Color(255, 170, 40, 255);
            Raylib.DrawCubeWiresV(d.Pos, d.HalfExtents * 2, color);
            if (blocked)
                Raylib.DrawCubeV(d.Pos, d.HalfExtents * 2, new Color(255, 80, 40, 70));
        }

        if (Active)
        {
            // Evidence: reported traversals and audited doors, drawn as dashed candidates so
            // they read as suggestions rather than edits. Cyan = "the world let someone
            // through here"; amber = "a planned leg failed here".
            foreach (var c in Evidence.LinkCandidates)
                DrawCandidate(c, new Color(80, 220, 255, 200));
            foreach (var c in Evidence.BlockCandidates)
                DrawCandidate(c, new Color(255, 180, 60, 200));

            foreach (var s in Overrides.FlagEdits.Where(s => s.InstanceId == 0))
            {
                var c = new Vector3(s.Center[0], s.Center[1], s.Center[2]);
                var color = s.Block ? new Color(220, 60, 60, 90) : new Color(60, 220, 90, 90);
                if (s.Kind == "sphere")
                    Raylib.DrawSphereWires(c, s.Extent[0], 8, 8, color);
                else
                    Raylib.DrawCubeWiresV(c, new Vector3(s.Extent[0], s.Extent[1], s.Extent[2]) * 2, color);
            }
            if (_pendingFirst is { } pending)
                Raylib.DrawSphere(pending, 0.8f, Color.Orange);
        }

        // links always visible: green arcs
        foreach (var l in Overrides.Links)
        {
            var a = new Vector3(l.From[0], l.From[1], l.From[2]);
            var b = new Vector3(l.To[0], l.To[1], l.To[2]);
            var mid = (a + b) * 0.5f + new Vector3(0, MathF.Max(3, Vector3.Distance(a, b) * 0.15f), 0);
            Raylib.DrawCylinderEx(a, mid, 0.3f, 0.3f, 6, Color.Green);
            Raylib.DrawCylinderEx(mid, b, 0.3f, 0.3f, 6, Color.Green);
            Raylib.DrawSphere(a, 0.7f, Color.Green);
            Raylib.DrawSphere(b, 0.7f, Color.Green);
        }

        Rlgl.DrawRenderBatchActive();
        Rlgl.EnableDepthTest();
    }
}
