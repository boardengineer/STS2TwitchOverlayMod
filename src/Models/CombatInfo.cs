using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace TwitchOverlayMod.Models;

internal class CombatInfo
{
    [JsonPropertyName("enemies")]
    public List<EnemyInfo> Enemies { get; set; } = [];

    [JsonPropertyName("hand")]
    public List<int> Hand { get; set; } = [];

    [JsonPropertyName("drawPile")]
    public List<int> DrawPile { get; set; } = [];

    [JsonPropertyName("discardPile")]
    public List<int> DiscardPile { get; set; } = [];

    [JsonPropertyName("exhaustPile")]
    public List<int> ExhaustPile { get; set; } = [];

    [JsonPropertyName("energy")]
    public int Energy { get; set; }

    [JsonPropertyName("maxEnergy")]
    public int MaxEnergy { get; set; }

    [JsonPropertyName("block")]
    public int Block { get; set; }

    [JsonPropertyName("powers")]
    public List<PowerInfo> Powers { get; set; } = [];

    [JsonPropertyName("drawPileCount")]
    public int DrawPileCount { get; set; }

    [JsonPropertyName("discardPileCount")]
    public int DiscardPileCount { get; set; }

    [JsonPropertyName("drawPileButtonX")]
    public float DrawPileButtonX { get; set; }

    [JsonPropertyName("drawPileButtonY")]
    public float DrawPileButtonY { get; set; }

    [JsonPropertyName("drawPileButtonWidth")]
    public float DrawPileButtonWidth { get; set; }

    [JsonPropertyName("drawPileButtonHeight")]
    public float DrawPileButtonHeight { get; set; }

    [JsonPropertyName("discardPileButtonX")]
    public float DiscardPileButtonX { get; set; }

    [JsonPropertyName("discardPileButtonY")]
    public float DiscardPileButtonY { get; set; }

    [JsonPropertyName("discardPileButtonWidth")]
    public float DiscardPileButtonWidth { get; set; }

    [JsonPropertyName("discardPileButtonHeight")]
    public float DiscardPileButtonHeight { get; set; }

    [JsonPropertyName("exhaustPileButtonX")]
    public float ExhaustPileButtonX { get; set; }

    [JsonPropertyName("exhaustPileButtonY")]
    public float ExhaustPileButtonY { get; set; }

    [JsonPropertyName("exhaustPileButtonWidth")]
    public float ExhaustPileButtonWidth { get; set; }

    [JsonPropertyName("exhaustPileButtonHeight")]
    public float ExhaustPileButtonHeight { get; set; }

    [JsonPropertyName("playerHitboxX")]
    public float PlayerHitboxX { get; set; }

    [JsonPropertyName("playerHitboxY")]
    public float PlayerHitboxY { get; set; }

    [JsonPropertyName("playerHitboxWidth")]
    public float PlayerHitboxWidth { get; set; }

    [JsonPropertyName("playerHitboxHeight")]
    public float PlayerHitboxHeight { get; set; }
}
