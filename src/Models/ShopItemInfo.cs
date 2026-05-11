using System.Text.Json.Serialization;

namespace TwitchOverlayMod.Models;

internal class ShopItemInfo
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("x")]
    public float X { get; set; }

    [JsonPropertyName("y")]
    public float Y { get; set; }

    [JsonPropertyName("w")]
    public float Width { get; set; }

    [JsonPropertyName("h")]
    public float Height { get; set; }
}
