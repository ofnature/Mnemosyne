using Mnemosyne.Core;
using Raylib_cs;

namespace Mnemosyne.Viewer;

// Master zone list: every known zone (from the baked TerritoryType map), tagged by status -
// cached (vnavmesh), built (Mnemosyne's own store), or buildable (game files only).
// Fully mouse-driven: hover, click to load/build, wheel scrolls, click outside closes;
// keyboard (type to filter, arrows, Enter, Esc) still works.
public sealed class ZonePicker
{
    public sealed record Item(string Name, string SubText, string? Path, string Bg, string Status, long SizeBytes)
    {
        public bool NeedsBuild => Path == null;
    }

    public bool Open;

    private List<Item> _all = [];
    private string _filter = "";
    private int _selected;
    private int _scroll;
    private int _hover = -1;

    private const int RowHeight = 22;
    private const int VisibleRows = 28;
    private int _panelX, _panelY, _panelW, _panelH, _listY;

    public void Refresh()
    {
        // best current-version cache file per zone (bg prefix), newest wins
        var cached = new Dictionary<string, (string Path, long Size)>();
        foreach (var e in MeshCache.Enumerate().Where(e => e.IsSupported))
        {
            var bg = OverrideStore.BgKey(e.Key);
            var info = new FileInfo(e.Path);
            if (!cached.TryGetValue(bg, out var existing) || info.LastWriteTimeUtc > new FileInfo(existing.Path).LastWriteTimeUtc)
                cached[bg] = (e.Path, info.Length);
        }

        // Mnemosyne-built store
        var built = new Dictionary<string, (string Path, long Size)>();
        var builtDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mnemosyne", "built");
        if (Directory.Exists(builtDir))
        {
            foreach (var file in Directory.EnumerateFiles(builtDir, "*.navmesh"))
            {
                try
                {
                    var (magic, version, _) = MeshCache.ReadHeader(file);
                    if (magic != global::Navmesh.Navmesh.Magic || version != global::Navmesh.Navmesh.Version)
                        continue;
                }
                catch (IOException)
                {
                    continue;
                }
                var bg = OverrideStore.BgKey(Path.GetFileNameWithoutExtension(file));
                if (!built.ContainsKey(bg))
                    built[bg] = (file, new FileInfo(file).Length);
            }
        }

        var items = new List<Item>();
        foreach (var (bg, info) in ZoneNames.All)
        {
            var name = info.Name.Length > 0 ? info.Name : bg;
            if (cached.TryGetValue(bg, out var c))
                items.Add(new(name, bg, c.Path, info.Bg, "cached", c.Size));
            else if (built.TryGetValue(bg, out var b))
                items.Add(new(name, bg, b.Path, info.Bg, "built", b.Size));
            else
                items.Add(new(name, bg, null, info.Bg, "buildable", 0));
        }
        // cache files whose bg prefix isn't in the name map (oddballs) still deserve a row
        foreach (var (bg, c) in cached)
            if (!ZoneNames.All.ContainsKey(bg))
                items.Add(new(bg, bg, c.Path, "", "cached", c.Size));

        _all = [.. items
            .OrderBy(i => i.Status switch { "cached" => 0, "built" => 1, _ => 2 })
            .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)];
        _selected = 0;
        _scroll = 0;
    }

    private List<Item> Filtered =>
        _filter.Length == 0
            ? _all
            : [.. _all.Where(i => i.Name.Contains(_filter, StringComparison.OrdinalIgnoreCase)
                || i.SubText.Contains(_filter, StringComparison.OrdinalIgnoreCase))];

    // returns the confirmed item (click or Enter); Program loads or builds it
    public Item? Update()
    {
        if (Raylib.IsKeyPressed(KeyboardKey.Tab))
        {
            Open = !Open;
            if (Open)
                Refresh();
        }
        if (!Open)
            return null;

        for (int c = Raylib.GetCharPressed(); c > 0; c = Raylib.GetCharPressed())
        {
            if (c >= 32 && c < 127)
            {
                _filter += (char)c;
                _selected = 0;
                _scroll = 0;
            }
        }
        if (Raylib.IsKeyPressed(KeyboardKey.Backspace) && _filter.Length > 0)
        {
            _filter = _filter[..^1];
            _selected = 0;
            _scroll = 0;
        }

        var list = Filtered;
        if (list.Count == 0)
        {
            _hover = -1;
            return null;
        }

        bool moved = false;
        if (Raylib.IsKeyPressed(KeyboardKey.Down) || Raylib.IsKeyPressedRepeat(KeyboardKey.Down)) { _selected++; moved = true; }
        if (Raylib.IsKeyPressed(KeyboardKey.Up) || Raylib.IsKeyPressedRepeat(KeyboardKey.Up)) { _selected--; moved = true; }
        if (Raylib.IsKeyPressed(KeyboardKey.PageDown)) { _selected += VisibleRows; moved = true; }
        if (Raylib.IsKeyPressed(KeyboardKey.PageUp)) { _selected -= VisibleRows; moved = true; }
        _selected = Math.Clamp(_selected, 0, list.Count - 1);
        if (moved) // keep keyboard selection in view
            _scroll = Math.Clamp(_scroll, _selected - VisibleRows + 1, _selected);
        _scroll = Math.Clamp(_scroll, 0, Math.Max(0, list.Count - VisibleRows));

        // mouse
        var mouse = Raylib.GetMousePosition();
        bool inPanel = mouse.X >= _panelX && mouse.X <= _panelX + _panelW && mouse.Y >= _panelY && mouse.Y <= _panelY + _panelH;
        float wheel = Raylib.GetMouseWheelMove();
        if (wheel != 0 && inPanel)
            _scroll = Math.Clamp(_scroll - (int)(wheel * 3), 0, Math.Max(0, list.Count - VisibleRows));

        _hover = -1;
        if (inPanel && mouse.Y >= _listY)
        {
            int row = (int)(mouse.Y - _listY) / RowHeight;
            int index = _scroll + row;
            if (row >= 0 && row < VisibleRows && index < list.Count)
                _hover = index;
        }

        if (Raylib.IsMouseButtonPressed(MouseButton.Left))
        {
            if (!inPanel)
            {
                Open = false; // click outside closes
                return null;
            }
            if (_hover >= 0)
            {
                _selected = _hover;
                Open = false;
                return list[_hover];
            }
        }

        if (Raylib.IsKeyPressed(KeyboardKey.Enter))
        {
            Open = false;
            return list[_selected];
        }
        return null;
    }

    public void Draw()
    {
        if (!Open)
            return;

        var list = Filtered;
        _panelW = 860;
        _panelH = 70 + VisibleRows * RowHeight + 10;
        _panelX = 40;
        _panelY = 40;
        _listY = _panelY + 62;
        Raylib.DrawRectangle(_panelX, _panelY, _panelW, _panelH, new Color(15, 18, 25, 240));
        Raylib.DrawRectangleLines(_panelX, _panelY, _panelW, _panelH, new Color(90, 120, 160, 255));

        int cachedCount = _all.Count(i => i.Status == "cached");
        int builtCount = _all.Count(i => i.Status == "built");
        Raylib.DrawText($"Zones  {list.Count}/{_all.Count}  ({cachedCount} cached, {builtCount} built, rest buildable)  —  click to load, type to filter", _panelX + 12, _panelY + 10, 18, Color.RayWhite);
        Raylib.DrawText($"filter: {_filter}_", _panelX + 12, _panelY + 34, 18, new Color(150, 200, 255, 255));

        for (int i = _scroll; i < Math.Min(_scroll + VisibleRows, list.Count); ++i)
        {
            int rowY = _listY + (i - _scroll) * RowHeight;
            var item = list[i];
            if (i == _selected)
                Raylib.DrawRectangle(_panelX + 4, rowY - 2, _panelW - 8, RowHeight, new Color(60, 90, 130, 255));
            else if (i == _hover)
                Raylib.DrawRectangle(_panelX + 4, rowY - 2, _panelW - 8, RowHeight, new Color(45, 60, 85, 255));

            var (statusText, statusColor) = item.Status switch
            {
                "cached" => ($"cached {item.SizeBytes / 1024.0 / 1024.0:f1} MB", new Color(140, 220, 140, 255)),
                "built" => ($"built {item.SizeBytes / 1024.0 / 1024.0:f1} MB", new Color(120, 200, 255, 255)),
                _ => ("buildable", new Color(150, 150, 160, 255)),
            };
            var nameColor = item.NeedsBuild ? new Color(170, 175, 185, 255) : Color.White;
            Raylib.DrawText(item.Name, _panelX + 12, rowY, 18, i == _selected || i == _hover ? Color.White : nameColor);
            Raylib.DrawText(statusText, _panelX + 560, rowY, 18, statusColor);
        }

        // scrollbar
        if (list.Count > VisibleRows)
        {
            float frac = (float)VisibleRows / list.Count;
            int barH = Math.Max(20, (int)(VisibleRows * RowHeight * frac));
            int barY = _listY + (int)((VisibleRows * RowHeight - barH) * (_scroll / (float)Math.Max(1, list.Count - VisibleRows)));
            Raylib.DrawRectangle(_panelX + _panelW - 10, barY, 6, barH, new Color(90, 120, 160, 255));
        }
    }
}
