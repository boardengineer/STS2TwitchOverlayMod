using System.Text.Json.Serialization;

namespace TwitchOverlayMod.Models;

internal class UiInfo
{
    [JsonPropertyName("windowWidth")]
    public int WindowWidth { get; set; }

    [JsonPropertyName("windowHeight")]
    public int WindowHeight { get; set; }

    [JsonPropertyName("deckButtonX")]
    public float DeckButtonX { get; set; }

    [JsonPropertyName("deckButtonY")]
    public float DeckButtonY { get; set; }

    [JsonPropertyName("deckButtonWidth")]
    public float DeckButtonWidth { get; set; }

    [JsonPropertyName("deckButtonHeight")]
    public float DeckButtonHeight { get; set; }

    [JsonPropertyName("mapButtonX")]
    public float MapButtonX { get; set; }

    [JsonPropertyName("mapButtonY")]
    public float MapButtonY { get; set; }

    [JsonPropertyName("mapButtonWidth")]
    public float MapButtonWidth { get; set; }

    [JsonPropertyName("mapButtonHeight")]
    public float MapButtonHeight { get; set; }
}
