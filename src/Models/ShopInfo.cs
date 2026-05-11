using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace TwitchOverlayMod.Models;

internal class ShopInfo
{
    [JsonPropertyName("cards")]
    public List<ShopItemInfo> Cards { get; set; } = [];

    [JsonPropertyName("relics")]
    public List<ShopItemInfo> Relics { get; set; } = [];

    [JsonPropertyName("potions")]
    public List<ShopItemInfo> Potions { get; set; } = [];

    [JsonPropertyName("cardRemoval")]
    public ShopItemInfo? CardRemoval { get; set; }

    [JsonPropertyName("cardRemovalTitle")]
    public string? CardRemovalTitle { get; set; }

    [JsonPropertyName("cardRemovalDesc")]
    public string? CardRemovalDesc { get; set; }

    [JsonPropertyName("cardRemovalCost")]
    public int? CardRemovalCost { get; set; }
}
