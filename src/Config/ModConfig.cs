using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TwitchOverlayMod.Config;

internal class ModConfig
{
    [JsonPropertyName("extensionSecret")]
    public string ExtensionSecret { get; set; } = "";

    [JsonPropertyName("extensionClientId")]
    public string ExtensionClientId { get; set; } = "";

    [JsonPropertyName("channelId")]
    public string ChannelId { get; set; } = "";

    [JsonPropertyName("channelOwnerId")]
    public string ChannelOwnerId { get; set; } = "";

    [JsonPropertyName("broadcastIntervalSeconds")]
    public double BroadcastIntervalSeconds { get; set; } = 2.0;

    internal static ModConfig? Load()
    {
        var appData = System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData);
        var configPath = Path.Combine(appData, "SlayTheSpire2", "TwitchOverlayMod.config.json");
        if (!File.Exists(configPath)) return null;

        var json = File.ReadAllText(configPath);
        return JsonSerializer.Deserialize<ModConfig>(json);
    }
}
