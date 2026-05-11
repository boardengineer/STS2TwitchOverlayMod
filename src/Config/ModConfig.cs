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

    [JsonPropertyName("enableBackfill")]
    public bool EnableBackfill { get; set; } = true;

    [JsonPropertyName("backfillEveryN")]
    public int BackfillEveryN { get; set; } = 4;

    [JsonPropertyName("broadcastSchedule")]
    public string BroadcastSchedule { get; set; } = "SMSASL";

    [JsonPropertyName("enableLocalServer")]
    public bool EnableLocalServer { get; set; } = false;

    [JsonPropertyName("localServerPort")]
    public int LocalServerPort { get; set; } = 9001;

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
        var config   = JsonSerializer.Deserialize<ModConfig>(existing) ?? new ModConfig();
        File.WriteAllText(configPath, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
        return config;
    }
}
