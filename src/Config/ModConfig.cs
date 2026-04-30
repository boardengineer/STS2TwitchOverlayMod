using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TwitchOverlayMod.Config;

internal class ModConfig
{
    [JsonPropertyName("ebsUrl")]
    public string EbsUrl { get; set; } = "https://spiffy-moonbeam-925327.netlify.app";

    [JsonPropertyName("extensionClientId")]
    public string ExtensionClientId { get; set; } = "zenmih23y97hx55rke73ecwyepd5vl";

    [JsonPropertyName("broadcastIntervalSeconds")]
    public double BroadcastIntervalSeconds { get; set; } = 2.0;

    internal static ModConfig Load()
    {
        var appData    = System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData);
        var configPath = Path.Combine(appData, "SlayTheSpire2", "TwitchOverlayMod.config.json");

        if (!File.Exists(configPath))
        {
            var defaults = new ModConfig();
            var json     = JsonSerializer.Serialize(defaults, new JsonSerializerOptions { WriteIndented = true });
            Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
            File.WriteAllText(configPath, json);
            return defaults;
        }

        var existing = File.ReadAllText(configPath);
        return JsonSerializer.Deserialize<ModConfig>(existing) ?? new ModConfig();
    }
}
