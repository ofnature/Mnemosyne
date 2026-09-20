using System.Text.Json;

namespace Mnemosyne.Core;

public static class GamePaths
{
    // <XIVLauncher GamePath>\game\sqpack, or null when no game install is found
    public static string? FindSqpackDir()
    {
        try
        {
            var cfg = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XIVLauncher", "launcherConfigV3.json");
            if (!File.Exists(cfg))
                return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(cfg));
            if (doc.RootElement.TryGetProperty("GamePath", out var gp) && gp.GetString() is { Length: > 0 } root)
            {
                var p = Path.Combine(root, "game", "sqpack");
                if (Directory.Exists(p))
                    return p;
            }
        }
        catch
        {
            // unreadable config - treat as no game install
        }
        return null;
    }
}
