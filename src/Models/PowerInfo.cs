using System.Text.Json.Serialization;

namespace TwitchOverlayMod.Models;

internal class PowerInfo
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("amount")]
    public int Amount { get; set; }
}
