using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace TwitchOverlayMod.Models;

internal class PlayerInfo
{
    [JsonPropertyName("characterId")]
    public string? CharacterId { get; set; }

    [JsonPropertyName("currentHp")]
    public int CurrentHp { get; set; }

    [JsonPropertyName("maxHp")]
    public int MaxHp { get; set; }

    [JsonPropertyName("gold")]
    public int Gold { get; set; }

    [JsonPropertyName("currentAct")]
    public int CurrentAct { get; set; }

    [JsonPropertyName("actId")]
    public string? ActId { get; set; }

    [JsonPropertyName("actFloor")]
    public int ActFloor { get; set; }

    [JsonPropertyName("ascensionLevel")]
    public int AscensionLevel { get; set; }

    [JsonPropertyName("bossId")]
    public string? BossId { get; set; }

    [JsonPropertyName("ancientId")]
    public string? AncientId { get; set; }

    [JsonPropertyName("deck")]
    public List<int> Deck { get; set; } = [];

    [JsonPropertyName("relics")]
    public List<RelicInfo> Relics { get; set; } = [];

    [JsonPropertyName("potions")]
    public List<PotionInfo> Potions { get; set; } = [];
}
