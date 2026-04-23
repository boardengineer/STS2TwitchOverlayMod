using System.Text.Json.Serialization;

namespace TwitchOverlayMod.Models;

internal class PotionInfo
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("x")]
    public float X { get; set; }

    [JsonPropertyName("y")]
    public float Y { get; set; }

    [JsonPropertyName("width")]
    public float Width { get; set; }

    [JsonPropertyName("height")]
    public float Height { get; set; }
}
