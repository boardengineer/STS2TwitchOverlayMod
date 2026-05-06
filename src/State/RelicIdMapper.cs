using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TwitchOverlayMod.State;

internal static class RelicIdMapper
{
    private static Dictionary<string, int>? _map;

    internal static void Load()
    {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("TwitchOverlayMod.data.relics.json");
        if (stream == null) return;

        using var reader = new StreamReader(stream);
        var json = reader.ReadToEnd();
        var relics = JsonSerializer.Deserialize<List<RelicEntry>>(json);
        if (relics == null) return;

        _map = new Dictionary<string, int>();
        foreach (var relic in relics)
            _map.TryAdd(relic.GameId, relic.Id);
    }

    internal static int? GetSequentialId(string gameId)
    {
        if (_map == null) return null;
        return _map.TryGetValue(gameId, out var id) ? id : null;
    }

    internal static void Register(string gameId, int id)
    {
        _map ??= new Dictionary<string, int>();
        _map.TryAdd(gameId, id);
    }

    private class RelicEntry
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("game_id")]
        public string GameId { get; set; } = "";
    }
}
