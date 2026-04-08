using System.Text.Json.Serialization;

namespace TwitchOverlayMod.Models;

internal class CardInfo
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("cost")]
    public int Cost { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("upgraded")]
    public bool Upgraded { get; set; }
}
