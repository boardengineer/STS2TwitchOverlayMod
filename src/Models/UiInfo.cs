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
}
