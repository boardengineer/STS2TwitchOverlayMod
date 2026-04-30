using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using TwitchOverlayMod.Utility;

namespace TwitchOverlayMod.Twitch;

internal static class CredentialManager
{
    private static readonly HttpClient Http = new();
    private static readonly string CredentialsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SlayTheSpire2", "TwitchOverlayMod.credentials.json");

    private static string? _jwt;
    private static string? _refreshToken;
    private static string? _channelId;
    private static string? _ownerId;
    private static DateTimeOffset _jwtExpiresAt;
    private static int _expiresInSeconds;
    private static CancellationTokenSource? _refreshCts;

    public static bool IsConnected => _jwt != null && _channelId != null && DateTimeOffset.UtcNow < _jwtExpiresAt;
    public static string? ChannelId => _channelId;
    public static string? OwnerId   => _ownerId;

    public static void LoadSaved()
    {
        if (!File.Exists(CredentialsPath)) return;
        try
        {
            var saved = JsonSerializer.Deserialize<SavedCredentials>(File.ReadAllText(CredentialsPath));
            if (saved?.RefreshToken == null) return;

            _refreshToken = saved.RefreshToken;
            _channelId    = saved.ChannelId;
            _ownerId      = saved.OwnerId;

            Task.Run(RefreshAsync);
        }
        catch (Exception ex)
        {
            Logging.Log($"Credentials load error: {ex.Message}");
        }
    }

    public static void StoreFromCallback(string jwt, string refreshToken, string channelId, string ownerId, int expiresIn)
    {
        _jwt            = jwt;
        _refreshToken   = refreshToken;
        _channelId      = channelId;
        _ownerId        = ownerId;
        _expiresInSeconds = expiresIn;
        _jwtExpiresAt   = DateTimeOffset.UtcNow.AddSeconds(expiresIn);

        SaveCredentials();
        ScheduleRefresh();
        Logging.Log($"Credentials stored for channel {channelId}, JWT expires in {expiresIn}s.");
    }

    public static string? GetCurrentJwt() =>
        _jwt != null && DateTimeOffset.UtcNow < _jwtExpiresAt ? _jwt : null;

    private static async Task RefreshAsync()
    {
        if (_refreshToken == null || Plugin.Config == null) return;

        try
        {
            var body     = JsonSerializer.Serialize(new { refresh_token = _refreshToken });
            var response = await Http.PostAsync(
                $"{Plugin.Config.EbsUrl}/.netlify/functions/refresh",
                new StringContent(body, Encoding.UTF8, "application/json"));

            if (!response.IsSuccessStatusCode)
            {
                Logging.Log($"JWT refresh failed ({response.StatusCode}): {await response.Content.ReadAsStringAsync()}");
                return;
            }

            var result = JsonSerializer.Deserialize<RefreshResult>(await response.Content.ReadAsStringAsync());
            if (result == null) return;

            _jwt              = result.Jwt;
            _expiresInSeconds = result.ExpiresIn;
            _jwtExpiresAt     = DateTimeOffset.UtcNow.AddSeconds(result.ExpiresIn);

            ScheduleRefresh();
            Logging.Log($"JWT refreshed, next refresh in {_expiresInSeconds * 0.8:F0}s.");
        }
        catch (Exception ex)
        {
            Logging.Log($"JWT refresh error: {ex.Message}");
        }
    }

    private static void ScheduleRefresh()
    {
        _refreshCts?.Cancel();
        _refreshCts?.Dispose();
        _refreshCts = new CancellationTokenSource();
        var token = _refreshCts.Token;
        var delay = TimeSpan.FromSeconds(_expiresInSeconds * 0.8);

        Task.Run(async () =>
        {
            await Task.Delay(delay, token);
            if (!token.IsCancellationRequested)
                await RefreshAsync();
        }, token);
    }

    private static void SaveCredentials()
    {
        try
        {
            File.WriteAllText(CredentialsPath, JsonSerializer.Serialize(new SavedCredentials
            {
                RefreshToken = _refreshToken,
                ChannelId    = _channelId,
                OwnerId      = _ownerId,
            }));
        }
        catch (Exception ex)
        {
            Logging.Log($"Credentials save error: {ex.Message}");
        }
    }

    private sealed record SavedCredentials
    {
        [JsonPropertyName("refreshToken")] public string? RefreshToken { get; init; }
        [JsonPropertyName("channelId")]    public string? ChannelId    { get; init; }
        [JsonPropertyName("ownerId")]      public string? OwnerId      { get; init; }
    }

    private sealed record RefreshResult
    {
        [JsonPropertyName("jwt")]        public string Jwt       { get; init; } = "";
        [JsonPropertyName("expires_in")] public int    ExpiresIn { get; init; }
    }
}
