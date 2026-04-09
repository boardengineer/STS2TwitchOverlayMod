using System.Text.Json.Serialization;

namespace TwitchOverlayMod.Models;

internal class RelicInfo
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("x")]
    public float X { get; set; }

    [JsonPropertyName("y")]
    public float Y { get; set; }
}
