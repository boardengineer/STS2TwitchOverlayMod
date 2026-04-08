using System.Text.Json.Serialization;

namespace TwitchOverlayMod.Models;

internal class PowerInfo
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("amount")]
    public int Amount { get; set; }
}
