using System.Text.Json.Serialization;

namespace TwitchOverlayMod.Models;

internal class GameStatePayload
{
    [JsonPropertyName("player")]
    public PlayerInfo? Player { get; set; }

    [JsonPropertyName("combat")]
    public CombatInfo? Combat { get; set; }

    [JsonPropertyName("timestamp")]
    public long Timestamp { get; set; }
}
