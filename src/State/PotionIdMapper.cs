using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TwitchOverlayMod.State;

internal static class PotionIdMapper
{
    private static Dictionary<string, int>? _map;

    internal static void Load()
    {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("TwitchOverlayMod.data.potions.json");
        if (stream == null) return;

        using var reader = new StreamReader(stream);
        var json = reader.ReadToEnd();
        var potions = JsonSerializer.Deserialize<List<PotionEntry>>(json);
        if (potions == null) return;

        _map = new Dictionary<string, int>();
        foreach (var potion in potions)
            _map.TryAdd(potion.GameId, potion.Id);
    }

    internal static int? GetSequentialId(string gameId)
    {
        if (_map == null) return null;
        return _map.TryGetValue(gameId, out var id) ? id : null;
    }

    private class PotionEntry
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("game_id")]
        public string GameId { get; set; } = "";
    }
}
