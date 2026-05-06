using System.Text.Json.Serialization;

namespace TwitchOverlayMod.Models;

internal class GameStatePayload
{
    [JsonPropertyName("t")]
    public string Type { get; } = "s";

    [JsonPropertyName("player")]
    public PlayerInfo? Player { get; set; }

    [JsonPropertyName("combat")]
    public CombatInfo? Combat { get; set; }

    [JsonPropertyName("ui")]
    public UiInfo? Ui { get; set; }

    [JsonPropertyName("map")]
    public MapInfo? Map { get; set; }

    [JsonPropertyName("timestamp")]
    public long Timestamp { get; set; }
}
