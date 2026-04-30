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

    // The Twitch "glitch" logo as an inline SVG (white fill, transparent bg).
    private const string TwitchSvg = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 28">
        <path fill="white" d="M2.149 0l-1.612 4.119v16.836h5.731v3.045h3.224l3.045-3.045h4.657
             l6.269-6.269v-14.686h-21.314zm19.164 13.612l-3.582 3.582h-5.731l-3.045 3.045
             v-3.045h-4.836v-14.791h17.194v11.209zm-3.582-7.343v6.262h-2.149v-6.262h2.149z
             m-5.731 0v6.262h-2.149v-6.262h2.149z"/>
        </svg>
        """;

    internal static void SetupIfNeeded(Node mainMenu)
    {
        // Already set up and the main menu scene is still alive — nothing to do.
        if (_layer != null && GodotObject.IsInstanceValid(_layer)) return;

        // Layer was freed when the previous main menu scene exited — reset state.
        _layer      = null;
        _button     = null;
        _connecting = false;

        _layer = new CanvasLayer { Layer = 100 };

        _button = new Button
        {
            CustomMinimumSize = new Vector2(44f, 44f),
            Icon              = CreateTwitchIcon(),
            ExpandIcon        = false,
            IconAlignment     = HorizontalAlignment.Center,
        };

        // Fallback: top-left — overwritten by PositionBelowProfileButton once layout is ready.
        _button.AnchorLeft   = 0f;
        _button.AnchorRight  = 0f;
        _button.AnchorTop    = 0f;
        _button.AnchorBottom = 0f;
        _button.OffsetLeft   = 10f;
        _button.OffsetRight  = 54f;
        _button.OffsetTop    = 10f;
        _button.OffsetBottom = 54f;

        ApplyTwitchStyle(_button, connected: false, connecting: false);
        _button.Pressed += OnPressed;

        _layer.AddChild(_button);
        mainMenu.AddChild(_layer);  // owned by the main menu scene, freed with it

        // Defer so the scene layout is complete before we read the profile button's rect.
        Callable.From(() => PositionBelowProfileButton(mainMenu)).CallDeferred();

        if (CredentialManager.IsConnected)
            ApplyTwitchStyle(_button, connected: true, connecting: false);
    }

    private static void PositionBelowProfileButton(Node mainMenu)
    {
        if (_button == null || !GodotObject.IsInstanceValid(_button)) return;

        var profileBtn = mainMenu.GetNodeOrNull<Control>("%ChangeProfileButton");
        if (profileBtn == null || !GodotObject.IsInstanceValid(profileBtn)) return;

        var rect = profileBtn.GetGlobalRect();
        _button.AnchorLeft   = 0f;
        _button.AnchorRight  = 0f;
        _button.AnchorTop    = 0f;
        _button.AnchorBottom = 0f;
        _button.OffsetLeft   = rect.Position.X;
        _button.OffsetRight  = rect.Position.X + 44f;
        _button.OffsetTop    = rect.End.Y + 5f;
        _button.OffsetBottom = rect.End.Y + 49f;
    }

    private static void OnPressed()
    {
        if (_connecting) return;
        _connecting = true;
        Callable.From(() =>
        {
            if (_button == null || !GodotObject.IsInstanceValid(_button)) return;
            ApplyTwitchStyle(_button, connected: false, connecting: true);
        }).CallDeferred();
        Task.Run(ConnectAsync);
    }

    private static async Task ConnectAsync()
    {
        if (Plugin.Config == null || string.IsNullOrEmpty(Plugin.Config.EbsUrl))
        {
            Logging.Log("Cannot connect: EbsUrl is not configured.");
            FinishConnect(success: false);
            return;
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var port    = LocalCallbackServer.FindAvailablePort();
        var authUrl = $"{Plugin.Config.EbsUrl}/.netlify/functions/auth?port={port}";

        Logging.Log($"Opening auth URL: {authUrl}");
        Callable.From(() => OS.ShellOpen(authUrl)).CallDeferred();

        try
        {
            var result = await LocalCallbackServer.WaitForCallbackAsync(port, cts.Token);

            if (result == null)
            {
                FinishConnect(success: false);
                return;
            }

            CredentialManager.StoreFromCallback(
                result.Value.Jwt, result.Value.RefreshToken,
                result.Value.ChannelId, result.Value.OwnerId,
                result.Value.ExpiresIn);

            FinishConnect(success: true);
        }
        catch (Exception ex)
        {
            Logging.Log($"Connect error: {ex.Message}");
            FinishConnect(success: false);
        }
    }

    private static void FinishConnect(bool success)
    {
        _connecting = false;
        Callable.From(() =>
        {
            if (_button == null || !GodotObject.IsInstanceValid(_button)) return;
            ApplyTwitchStyle(_button, connected: success, connecting: false);
        }).CallDeferred();
    }

    private static void ApplyTwitchStyle(Button button, bool connected, bool connecting)
    {
        var bgColor = connecting  ? new Color(0.4f,  0.4f,  0.4f)    // grey while waiting
                    : connected   ? new Color(0.22f, 0.59f, 0.22f)   // green when authenticated
                                  : new Color(0.569f, 0.275f, 1.0f); // Twitch purple

        var bg = new StyleBoxFlat
        {
            BgColor                 = bgColor,
            CornerRadiusTopLeft     = 4,
            CornerRadiusTopRight    = 4,
            CornerRadiusBottomLeft  = 4,
            CornerRadiusBottomRight = 4,
        };
        button.AddThemeStyleboxOverride("normal", bg);
        button.AddThemeColorOverride("icon_normal_color", Colors.White);
    }

    private static Texture2D CreateTwitchIcon()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(TwitchSvg);
        var image = new Image();
        image.LoadSvgFromBuffer(bytes, scale: 1.2f);
        return ImageTexture.CreateFromImage(image);
    }
}
