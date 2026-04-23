using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace TwitchOverlayMod.Models;

internal class PowerInfo
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("amount")]
    public int Amount { get; set; }

    [JsonPropertyName("x")]
    public float X { get; set; }

    [JsonPropertyName("y")]
    public float Y { get; set; }

    [JsonPropertyName("width")]
    public float Width { get; set; }

    [JsonPropertyName("height")]
    public float Height { get; set; }

    [JsonPropertyName("vars")]
    public Dictionary<string, float> Vars { get; set; } = new();
}
