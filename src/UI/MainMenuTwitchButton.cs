using Godot;
using System;
using System.Threading;
using System.Threading.Tasks;
using TwitchOverlayMod.Twitch;
using TwitchOverlayMod.Utility;

namespace TwitchOverlayMod.UI;

internal static class MainMenuTwitchButton
{
    private static CanvasLayer? _layer;
    private static Button?      _button;
    private static bool         _connecting;

    internal static void SetupIfNeeded(Node parent)
    {
        if (_layer != null) return;

        _layer = new CanvasLayer { Layer = 100 };

        _button = new Button
        {
            Text              = "Twitch",
            CustomMinimumSize = new Vector2(120f, 40f),
        };

        // Anchor to top-right corner with a small margin
        _button.AnchorLeft   = 1f;
        _button.AnchorRight  = 1f;
        _button.AnchorTop    = 0f;
        _button.AnchorBottom = 0f;
        _button.OffsetLeft   = -130f;
        _button.OffsetRight  = -10f;
        _button.OffsetTop    = 10f;
        _button.OffsetBottom = 50f;

        ApplyTwitchStyle(_button, connected: false);
        _button.Pressed += OnPressed;

        _layer.AddChild(_button);
        parent.AddChild(_layer);

        if (CredentialManager.IsConnected)
            UpdateLabel("Connected");
    }

    private static void OnPressed()
    {
        if (_connecting) return;
        _connecting = true;
        UpdateLabel("Connecting...");
        Task.Run(ConnectAsync);
    }

    private static async Task ConnectAsync()
    {
        if (Plugin.Config == null || string.IsNullOrEmpty(Plugin.Config.EbsUrl))
        {
            Logging.Log("Cannot connect: EbsUrl is not configured.");
            FinishConnect(null, "No config");
            return;
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var port      = LocalCallbackServer.FindAvailablePort();
        var authUrl   = $"{Plugin.Config.EbsUrl}/.netlify/functions/auth?port={port}";

        Logging.Log($"Opening auth URL: {authUrl}");
        Callable.From(() => OS.ShellOpen(authUrl)).CallDeferred();

        try
        {
            var result = await LocalCallbackServer.WaitForCallbackAsync(port, cts.Token);

            if (result == null)
            {
                FinishConnect(null, "Cancelled");
                return;
            }

            CredentialManager.StoreFromCallback(
                result.Value.Jwt, result.Value.RefreshToken,
                result.Value.ChannelId, result.Value.OwnerId,
                result.Value.ExpiresIn);

            FinishConnect(result.Value.Login, null);
        }
        catch (Exception ex)
        {
            Logging.Log($"Connect error: {ex.Message}");
            FinishConnect(null, "Error");
        }
    }

    private static void FinishConnect(string? login, string? errorLabel)
    {
        _connecting = false;
        var text    = login != null ? $"✓ {login}" : (errorLabel ?? "Twitch");
        Callable.From(() =>
        {
            if (_button == null) return;
            _button.Text = text;
            ApplyTwitchStyle(_button, connected: login != null);
        }).CallDeferred();
    }

    private static void UpdateLabel(string text) =>
        Callable.From(() => { if (_button != null) _button.Text = text; }).CallDeferred();

    private static void ApplyTwitchStyle(Button button, bool connected)
    {
        var bg = new StyleBoxFlat
        {
            BgColor                = connected ? new Color(0.22f, 0.59f, 0.22f) : new Color(0.569f, 0.275f, 1.0f),
            CornerRadiusTopLeft    = 4,
            CornerRadiusTopRight   = 4,
            CornerRadiusBottomLeft = 4,
            CornerRadiusBottomRight = 4,
            ContentMarginLeft      = 8f,
            ContentMarginRight     = 8f,
        };
        button.AddThemeStyleboxOverride("normal", bg);
        button.AddThemeColorOverride("font_color", Colors.White);
    }
}
