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

    [JsonPropertyName("ascensionWidgetX")]
    public float AscensionWidgetX { get; set; }

    [JsonPropertyName("ascensionWidgetY")]
    public float AscensionWidgetY { get; set; }

    [JsonPropertyName("ascensionWidgetWidth")]
    public float AscensionWidgetWidth { get; set; }

    [JsonPropertyName("ascensionWidgetHeight")]
    public float AscensionWidgetHeight { get; set; }

    [JsonPropertyName("language")]
    public string? Language { get; set; }

    [JsonPropertyName("settingsButtonX")]
    public float SettingsButtonX { get; set; }

    [JsonPropertyName("settingsButtonY")]
    public float SettingsButtonY { get; set; }

    [JsonPropertyName("settingsButtonWidth")]
    public float SettingsButtonWidth { get; set; }

    [JsonPropertyName("settingsButtonHeight")]
    public float SettingsButtonHeight { get; set; }
}
