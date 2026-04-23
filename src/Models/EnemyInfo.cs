using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace TwitchOverlayMod.Models;

internal class EnemyInfo
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("currentHp")]
    public int CurrentHp { get; set; }

    [JsonPropertyName("maxHp")]
    public int MaxHp { get; set; }

    [JsonPropertyName("block")]
    public int Block { get; set; }

    [JsonPropertyName("intent")]
    public int? Intent { get; set; }

    [JsonPropertyName("powers")]
    public List<PowerInfo> Powers { get; set; } = [];

    [JsonPropertyName("x")]
    public float X { get; set; }

    [JsonPropertyName("y")]
    public float Y { get; set; }

    [JsonPropertyName("width")]
    public float Width { get; set; }

    [JsonPropertyName("height")]
    public float Height { get; set; }
}
