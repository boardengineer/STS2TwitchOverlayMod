using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TwitchOverlayMod.State;

internal static class CardIdMapper
{
    private static Dictionary<string, int>? _map;

    internal static void Load()
    {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("TwitchOverlayMod.data.cards.json");
        if (stream == null) return;

        using var reader = new StreamReader(stream);
        var json = reader.ReadToEnd();
        var cards = JsonSerializer.Deserialize<List<CardEntry>>(json);
        if (cards == null) return;

        _map = new Dictionary<string, int>();
        foreach (var card in cards)
        {
            var key = $"{card.GameId}:{card.UpgradeLevel}";
            _map.TryAdd(key, card.Id);
        }
    }

    internal static int? GetSequentialId(string gameId, int upgradeLevel)
    {
        if (_map == null) return null;
        var key = $"{gameId}:{upgradeLevel}";
        return _map.TryGetValue(key, out var id) ? id : null;
    }

    internal static void Register(string gameId, int upgradeLevel, int id)
    {
        _map ??= new Dictionary<string, int>();
        _map.TryAdd($"{gameId}:{upgradeLevel}", id);
    }

    private class CardEntry
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("game_id")]
        public string GameId { get; set; } = "";

        [JsonPropertyName("upgrade_level")]
        public int UpgradeLevel { get; set; }
    }
}
