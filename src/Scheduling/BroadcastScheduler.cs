using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using TwitchOverlayMod.Config;
using TwitchOverlayMod.State;
using TwitchOverlayMod.Twitch;
using TwitchOverlayMod.Utility;

namespace TwitchOverlayMod.Scheduling;

internal static class BroadcastScheduler
{
    private static Timer? _timer;
    private static ModConfig? _config;
    private static readonly string DebugJsonPath = Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
        "SlayTheSpire2", "TwitchOverlayMod_latest.json");

    internal static void Start(Node parent, ModConfig config)
    {
        if (_timer != null) return;

        _config = config;

        _timer = new Timer();
        _timer.WaitTime = config.BroadcastIntervalSeconds;
        _timer.Autostart = true;
        _timer.Connect(Timer.SignalName.Timeout, Callable.From(OnTimeout));

        parent.AddChild(_timer);
        Logging.Log($"Broadcast scheduler started (interval: {config.BroadcastIntervalSeconds}s)");
    }

    private static void OnTimeout()
    {
        if (_config == null) return;

        try
        {
            var payload = GameStateCollector.Collect();
            var json = JsonSerializer.Serialize(payload);
#if DUMP_JSON
            File.WriteAllText(DebugJsonPath, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
#endif
            var jwt = JwtHelper.CreateToken(_config.ExtensionSecret, _config.ChannelId, _config.ChannelOwnerId);
            Task.Run(() => TwitchPubSubClient.BroadcastAsync(json, jwt, _config));
        }
        catch (Exception ex)
        {
            Logging.Log($"Broadcast error: {ex.Message}");
        }
    }
}
