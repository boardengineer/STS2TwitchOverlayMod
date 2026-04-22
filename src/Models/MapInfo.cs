using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace TwitchOverlayMod.Models;

internal class MapInfo
{
    [JsonPropertyName("currentCol")]
    public int? CurrentCol { get; set; }

    [JsonPropertyName("currentRow")]
    public int? CurrentRow { get; set; }

    [JsonPropertyName("cols")]
    public List<int> Cols { get; set; } = [];

    [JsonPropertyName("rows")]
    public List<int> Rows { get; set; } = [];

    [JsonPropertyName("xs")]
    public List<float> Xs { get; set; } = [];

    [JsonPropertyName("ys")]
    public List<float> Ys { get; set; } = [];

    [JsonPropertyName("types")]
    public List<int> Types { get; set; } = [];

    [JsonPropertyName("states")]
    public List<int> States { get; set; } = [];

    [JsonPropertyName("children")]
    public List<List<int[]>> Children { get; set; } = [];
}

internal class MapPointInfo
{
    public int Col { get; set; }
    public int Row { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public int Type { get; set; }
    public int State { get; set; }
    public List<int[]> Children { get; set; } = [];
}
