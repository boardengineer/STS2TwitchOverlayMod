using Godot;
using System;
using System.Threading;
using System.Threading.Tasks;
using TwitchOverlayMod.Twitch;
using TwitchOverlayMod.Utility;

namespace TwitchOverlayMod.UI;

internal static class MainMenuTwitchButton
{
    private enum AuthState { Idle, Connecting, Connected, Error }

    private static Button? _button;
    private static bool    _connecting;

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
        if (_button != null && GodotObject.IsInstanceValid(_button)) return;

        _button     = null;
        _connecting = false;

        _button = new Button
        {
            CustomMinimumSize = new Vector2(44f, 44f),
            Icon              = CreateTwitchIcon(),
            ExpandIcon        = false,
            IconAlignment     = HorizontalAlignment.Center,
            // Fallback top-left position; overwritten once layout is ready.
            AnchorLeft   = 0f, AnchorRight  = 0f,
            AnchorTop    = 0f, AnchorBottom = 0f,
            OffsetLeft   = 10f, OffsetRight  = 54f,
            OffsetTop    = 10f, OffsetBottom = 54f,
        };

        SetIconColor(CredentialManager.IsConnected ? AuthState.Connected : AuthState.Idle);
        _button.Pressed += OnPressed;

        // Add directly to the menu node — no CanvasLayer — so the transition
        // overlay (NTransition, a ColorRect in the normal scene tree) covers us
        // and the button fades in/out with everything else.
        mainMenu.AddChild(_button);

        // Defer positioning until the layout pass has completed.
        Callable.From(() => PositionBelowProfileButton(mainMenu)).CallDeferred();
    }

    private static void PositionBelowProfileButton(Node mainMenu)
    {
        if (_button == null || !GodotObject.IsInstanceValid(_button)) return;

        var profileBtn = mainMenu.GetNodeOrNull<Control>("%ChangeProfileButton");
        if (profileBtn == null || !GodotObject.IsInstanceValid(profileBtn)) return;

        var rect = profileBtn.GetGlobalRect();
        _button.AnchorLeft   = 0f; _button.AnchorRight  = 0f;
        _button.AnchorTop    = 0f; _button.AnchorBottom = 0f;
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
            SetIconColor(AuthState.Connecting);
        }).CallDeferred();
        Task.Run(ConnectAsync);
    }

    private static async Task ConnectAsync()
    {
        if (string.IsNullOrEmpty(Plugin.Config.EbsUrl))
        {
            Logging.Log("Cannot connect: EbsUrl is not configured.");
            FinishConnect(AuthState.Error);
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
                // User closed the browser / timed out — return to idle, not an error.
                FinishConnect(AuthState.Idle);
                return;
            }

            CredentialManager.StoreFromCallback(
                result.Value.Jwt, result.Value.RefreshToken,
                result.Value.ChannelId, result.Value.OwnerId,
                result.Value.ExpiresIn);

            FinishConnect(AuthState.Connected);
        }
        catch (Exception ex)
        {
            Logging.Log($"Connect error: {ex.Message}");
            FinishConnect(AuthState.Error);
        }
    }

    private static void FinishConnect(AuthState state)
    {
        _connecting = false;
        Callable.From(() =>
        {
            if (_button == null || !GodotObject.IsInstanceValid(_button)) return;
            SetIconColor(state);
        }).CallDeferred();
    }

    private static void SetIconColor(AuthState state)
    {
        if (_button == null) return;
        var color = state switch
        {
            AuthState.Connecting => new Color(1.0f, 0.85f, 0.0f), // yellow — in progress
            AuthState.Connected  => new Color(0.0f, 1.0f,  0.2f), // green  — authenticated
            AuthState.Error      => new Color(1.0f, 0.1f,  0.1f), // red    — error
            _                    => new Color(0.7f, 0.0f,  1.0f), // purple — idle
        };
        _button.AddThemeColorOverride("icon_normal_color", color);
    }

    private static Texture2D CreateTwitchIcon()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(TwitchSvg);
        var image = new Image();
        image.LoadSvgFromBuffer(bytes, scale: 1.2f);
        return ImageTexture.CreateFromImage(image);
    }
}
