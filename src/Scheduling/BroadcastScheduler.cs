using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using TwitchOverlayMod.Backfill;
using TwitchOverlayMod.Config;
using TwitchOverlayMod.State;
using TwitchOverlayMod.Twitch;
using TwitchOverlayMod.Utility;

namespace TwitchOverlayMod.Scheduling;

internal static class BroadcastScheduler
{
    private static Timer?           _timer;
    private static ModConfig?       _config;
    private static BackfillManager? _backfill;
    private static int              _tickCount;
    private static readonly string DebugJsonPath = Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
        "SlayTheSpire2", "TwitchOverlayMod_latest.json");

    internal static void Start(Node parent, ModConfig config, BackfillManager? backfill = null)
    {
        if (_timer != null) return;

        _config   = config;
        _backfill = backfill;

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

        var jwt       = CredentialManager.GetCurrentJwt();
        var channelId = CredentialManager.ChannelId;

        if (jwt == null || channelId == null) return;

        _tickCount++;

        try
        {
            if (_config.EnableBackfill
                && _backfill != null
                && _tickCount % _config.BackfillEveryN == 0)
            {
                var (chunk, notes) = _backfill.Dequeue();
                if (chunk != null)
                {
                    Task.Run(() => TwitchPubSubClient.BroadcastAsync(chunk, jwt, _config, channelId));
                }
                else
                {
                    _backfill.BuildChunks(GameStateCollector.Collect());
                    BroadcastGameState(jwt, _config, channelId);
                }
            }
            else
            {
                BroadcastGameState(jwt, _config, channelId);
            }
        }
        catch (Exception ex)
        {
            Logging.Log($"Broadcast error: {ex.Message}");
        }
    }

    private static void BroadcastGameState(string jwt, ModConfig config, string channelId)
    {
        var payload = GameStateCollector.Collect();
        var json    = JsonSerializer.Serialize(payload);
#if DUMP_JSON
        File.WriteAllText(DebugJsonPath, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
#endif
        Task.Run(() => TwitchPubSubClient.BroadcastAsync(json, jwt, config, channelId));
    }
}
