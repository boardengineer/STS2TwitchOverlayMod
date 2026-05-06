using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Nodes.Debug;
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
                var chunk = _backfill.DequeueChunk();
                if (chunk != null)
                {
                    ConsolePrint($"[Backfill] tick {_tickCount}: {DescribeChunk(chunk)} ({System.Text.Encoding.UTF8.GetByteCount(chunk)} bytes)");
                    Task.Run(() => TwitchPubSubClient.BroadcastAsync(chunk, jwt, _config, channelId));
                }
                else
                {
                    ConsolePrint($"[Backfill] tick {_tickCount}: no chunks remaining, restarting cycle");
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

    private static string DescribeChunk(string chunk)
    {
        try
        {
            using var doc   = JsonDocument.Parse(chunk);
            var root        = doc.RootElement;
            var cat         = root.GetProperty("cat").GetString() ?? "?";
            var items       = root.GetProperty("items");
            var count       = items.GetArrayLength();
            var names       = new List<string>(count);
            foreach (var item in items.EnumerateArray())
            {
                if (item.TryGetProperty("name", out var n))
                    names.Add(n.GetString() ?? "?");
            }
            return $"{cat} ×{count}: {string.Join(", ", names)}";
        }
        catch { return "?"; }
    }

    private static void ConsolePrint(string message)
    {
        NDevConsole.Instance.GetNode<RichTextLabel>("OutputContainer/OutputBuffer").Text += message + "\n";
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
