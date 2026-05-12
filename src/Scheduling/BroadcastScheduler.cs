using System;
using System.Diagnostics;
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
    private static Node?            _parent;
    private static ModConfig?       _config;
    private static BackfillManager? _backfill;
    private static int              _tickCount;
    private static int              _stateSeq;
    private static readonly string DebugJsonPath = Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
        "SlayTheSpire2", "TwitchOverlayMod_latest.json");

    internal static void Start(Node parent, ModConfig config, BackfillManager? backfill = null)
    {
        if (_timer != null) return;

        _parent   = parent;
        _config   = config;
        _backfill = backfill;

        _timer = new Timer();
        _timer.WaitTime = config.BroadcastIntervalSeconds;
        _timer.Autostart = true;
        _timer.Connect(Timer.SignalName.Timeout, Callable.From(OnTimeout));

        parent.AddChild(_timer);
        Logging.Log($"Broadcast scheduler started (interval: {config.BroadcastIntervalSeconds}s)");
    }

    internal static void SetBackfill(BackfillManager backfill) => _backfill = backfill;

    private static void OnTimeout()
    {
        if (_config == null) return;

        var jwt       = CredentialManager.GetCurrentJwt();
        var channelId = CredentialManager.ChannelId;
        var hasTwitch = jwt != null && channelId != null;

        // Skip the tick entirely when neither local server nor Twitch is active.
        if (!hasTwitch && !LocalBroadcastServer.IsRunning) return;

        _tickCount++;

        try
        {
            // Schedule string: S=state, M=metadata, A=art, L=large. Default "SMSASL".
            // Any unknown character or when backfill is disabled falls back to S.
            var schedule = (_config.BroadcastSchedule ?? "SMSASL").ToUpperInvariant();
            if (schedule.Length == 0) schedule = "S";
            var ch = schedule[(_tickCount - 1) % schedule.Length];

            if (!_config.EnableBackfill || _backfill == null || (ch != 'M' && ch != 'A' && ch != 'L'))
            {
                BroadcastGameState(jwt, channelId, _config, hasTwitch);
                return;
            }

            if (ch == 'M')
            {
                var state = GameStateCollector.Collect();
                _backfill.TriggerCaptureForActive(_parent!, state);

                if (!_backfill.HasMetadata)
                {
                    ConsolePrint("rebuild metadata");
                    _backfill.BuildMetadataChunks(state);
                }

                var (chunk, _, label) = _backfill.DequeueMetadata();
                if (chunk != null)
                {
                    ConsolePrint($"-> meta [{label}] ({chunk.Length}b, {_backfill.MetadataCount} remaining)");
                    if (LocalBroadcastServer.IsRunning) LocalBroadcastServer.Broadcast(chunk);
                    if (hasTwitch) Task.Run(() => TwitchPubSubClient.BroadcastAsync(chunk, jwt!, _config, channelId!));
                }
            }
            else if (ch == 'A')
            {
                if (!_backfill.HasArt)
                {
                    ConsolePrint("rebuild art");
                    _backfill.BuildArtChunks(GameStateCollector.Collect());
                }

                var (chunk, label) = _backfill.DequeueArt();
                if (chunk != null)
                {
                    ConsolePrint($"-> art [{label}] ({chunk.Length}b, {_backfill.ArtCount} remaining)");
                    if (LocalBroadcastServer.IsRunning) LocalBroadcastServer.Broadcast(chunk);
                    if (hasTwitch) Task.Run(() => TwitchPubSubClient.BroadcastAsync(chunk, jwt!, _config, channelId!));
                }
            }
            else // pos == 6
            {
                if (!_backfill.HasLarge)
                {
                    ConsolePrint("rebuild large");
                    _backfill.BuildLargeChunks(GameStateCollector.Collect());
                }

                var (chunk, label) = _backfill.DequeueLarge();
                if (chunk != null)
                {
                    ConsolePrint($"-> large [{label}] ({chunk.Length}b, {_backfill.LargeCount} remaining)");
                    if (LocalBroadcastServer.IsRunning) LocalBroadcastServer.Broadcast(chunk);
                    if (hasTwitch) Task.Run(() => TwitchPubSubClient.BroadcastAsync(chunk, jwt!, _config, channelId!));
                }
            }
        }
        catch (Exception ex)
        {
            Logging.Log($"Broadcast error: {ex.Message}");
        }
    }

    private static void BroadcastGameState(string? jwt, string? channelId, ModConfig config, bool hasTwitch)
    {
        var payload = GameStateCollector.Collect();
        var json    = JsonSerializer.Serialize(payload);
#if DUMP_JSON
        File.WriteAllText(DebugJsonPath, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
#endif

        const int MaxDirect = 4700;
        const int MaxSlice  = 4600;

        if (json.Length <= MaxDirect)
        {
            ConsolePrint($"-> state ({json.Length}b)");
            if (LocalBroadcastServer.IsRunning) LocalBroadcastServer.Broadcast(json);
            if (hasTwitch) Task.Run(() => TwitchPubSubClient.BroadcastAsync(json, jwt!, config, channelId!));
            return;
        }

        var b64   = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json));
        var seq   = ++_stateSeq;
        var total = (b64.Length + MaxSlice - 1) / MaxSlice;
        ConsolePrint($"-> state chunked ({json.Length}b → {total} parts)");

        for (var i = 0; i < total; i++)
        {
            var slice = b64.Substring(i * MaxSlice, Math.Min(MaxSlice, b64.Length - i * MaxSlice));
            var chunk = JsonSerializer.Serialize(new { t = "sc", seq, part = i + 1, of = total, data = slice });
            if (LocalBroadcastServer.IsRunning) LocalBroadcastServer.Broadcast(chunk);
            if (hasTwitch) { var c = chunk; Task.Run(() => TwitchPubSubClient.BroadcastAsync(c, jwt!, config, channelId!)); }
        }
    }

    [Conditional("VERBOSE_LOGGING")]
    private static void ConsolePrint(string message)
    {
        try
        {
            var buf = NDevConsole.Instance.GetNode<RichTextLabel>("OutputContainer/OutputBuffer");
            buf.Text += $"[TwitchOverlay] {message}\n";
        }
        catch { }
    }
}
