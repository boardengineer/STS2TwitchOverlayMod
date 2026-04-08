using System.Text.Json.Serialization;

namespace TwitchOverlayMod.Models;

internal class PotionInfo
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";
}
