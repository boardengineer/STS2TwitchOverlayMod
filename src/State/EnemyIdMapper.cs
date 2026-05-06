using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TwitchOverlayMod.State;

internal static class EnemyIdMapper
{
    private static Dictionary<string, int>? _map;

    internal static void Load()
    {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("TwitchOverlayMod.data.enemies.json");
        if (stream == null) return;

        using var reader = new StreamReader(stream);
        var json = reader.ReadToEnd();
        var enemies = JsonSerializer.Deserialize<List<EnemyEntry>>(json);
        if (enemies == null) return;

        _map = new Dictionary<string, int>();
        foreach (var enemy in enemies)
            _map.TryAdd(enemy.Name, enemy.Id);
    }

    internal static int? GetSequentialId(string name)
    {
        if (_map == null) return null;
        return _map.TryGetValue(name, out var id) ? id : null;
    }

    internal static void Register(string name, int id)
    {
        _map ??= new Dictionary<string, int>();
        _map.TryAdd(name, id);
    }

    private class EnemyEntry
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; } = "";
    }
}
