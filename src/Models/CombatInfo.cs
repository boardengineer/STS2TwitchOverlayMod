using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace TwitchOverlayMod.Models;

internal class CombatInfo
{
    [JsonPropertyName("enemies")]
    public List<EnemyInfo> Enemies { get; set; } = [];

    [JsonPropertyName("hand")]
    public List<int> Hand { get; set; } = [];

    [JsonPropertyName("energy")]
    public int Energy { get; set; }

    [JsonPropertyName("maxEnergy")]
    public int MaxEnergy { get; set; }

    [JsonPropertyName("drawPileCount")]
    public int DrawPileCount { get; set; }

    [JsonPropertyName("discardPileCount")]
    public int DiscardPileCount { get; set; }
}
