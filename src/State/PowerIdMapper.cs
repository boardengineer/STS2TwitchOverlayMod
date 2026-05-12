using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TwitchOverlayMod.State;

internal static class PowerIdMapper
{
    private static Dictionary<string, int>? _map;

    internal static int MaxPackagedId { get; private set; }

    internal static void Load()
    {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("TwitchOverlayMod.data.powers.json");
        if (stream == null) return;

        using var reader = new StreamReader(stream);
        var json = reader.ReadToEnd();
        var powers = JsonSerializer.Deserialize<List<PowerEntry>>(json);
        if (powers == null) return;

        _map = new Dictionary<string, int>();
        foreach (var power in powers)
            _map.TryAdd(power.GameId, power.Id);

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

    private class PowerEntry
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("game_id")]
        public string GameId { get; set; } = "";
    }
}
