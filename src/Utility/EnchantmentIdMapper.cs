using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TwitchOverlayMod.State;

internal static class EnchantmentIdMapper
{
    private static Dictionary<string, int>? _map;

    internal static int MaxPackagedId { get; private set; }

    internal static void Load()
    {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("TwitchOverlayMod.data.enchantments.json");
        if (stream == null) return;

        using var reader = new StreamReader(stream);
        var json = reader.ReadToEnd();
        var items = JsonSerializer.Deserialize<List<EnchantmentEntry>>(json);
        if (items == null) return;

        _map = new Dictionary<string, int>();
        foreach (var item in items)
            _map.TryAdd(item.GameId, item.Id);

        MaxPackagedId = _map.Count > 0 ? _map.Values.Max() : 0;
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

    private class EnchantmentEntry
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("game_id")]
        public string GameId { get; set; } = "";
    }
}
