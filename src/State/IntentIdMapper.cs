using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TwitchOverlayMod.State;

internal static class IntentIdMapper
{
    private static Dictionary<string, int>? _map;

    internal static int MaxPackagedId { get; private set; }

    internal static void Load()
    {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("TwitchOverlayMod.data.intents.json");
        if (stream == null) return;

        using var reader = new StreamReader(stream);
        var json = reader.ReadToEnd();
        var intents = JsonSerializer.Deserialize<List<IntentEntry>>(json);
        if (intents == null) return;

        _map = new Dictionary<string, int>();
        foreach (var intent in intents)
            _map.TryAdd(intent.IntentType, intent.Id);

        MaxPackagedId = _map.Count > 0 ? _map.Values.Max() : 0;
    }

    internal static int? GetSequentialId(string intentType)
    {
        if (_map == null) return null;
        return _map.TryGetValue(intentType, out var id) ? id : null;
    }

    internal static void Register(string intentType, int id)
    {
        _map ??= new Dictionary<string, int>();
        _map.TryAdd(intentType, id);
    }

    private class IntentEntry
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("intent_type")]
        public string IntentType { get; set; } = "";
    }
}
