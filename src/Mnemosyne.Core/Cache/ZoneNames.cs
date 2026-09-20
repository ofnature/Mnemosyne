using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mnemosyne.Core;

public sealed record ZoneInfo(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("bg")] string Bg);

// Maps a cache key's bg-path prefix (e.g. "ffxiv_sea_s1_twn_s1t1_level_s1t1") to the zone's
// display name ("Limsa Lominsa Upper Decks") and original slashed bg path (needed to load
// zone geometry from game data). Baked from TerritoryType/PlaceName into Resources/zone_names.json.
public static class ZoneNames
{
    private static readonly Lazy<Dictionary<string, ZoneInfo>> Map = new(Load);

    private static Dictionary<string, ZoneInfo> Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Mnemosyne.Core.Resources.zone_names.json");
        if (stream == null)
            return [];
        using var reader = new StreamReader(stream);
        return JsonSerializer.Deserialize<Dictionary<string, ZoneInfo>>(reader.ReadToEnd()) ?? [];
    }

    // every known zone: underscored bg key -> (name, slashed bg path)
    public static IReadOnlyDictionary<string, ZoneInfo> All => Map.Value;

    public static ZoneInfo? Lookup(string cacheKey)
    {
        var sep = cacheKey.IndexOf("__", StringComparison.Ordinal);
        var bg = sep > 0 ? cacheKey[..sep] : cacheKey;
        return Map.Value.GetValueOrDefault(bg);
    }

    public static string? Resolve(string cacheKey) =>
        Lookup(cacheKey)?.Name is { Length: > 0 } name ? name : null;
}
