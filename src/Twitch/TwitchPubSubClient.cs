using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using TwitchOverlayMod.Config;
using TwitchOverlayMod.Utility;

namespace TwitchOverlayMod.Twitch;

internal static class TwitchPubSubClient
{
    private static readonly HttpClient HttpClient = new();

    internal static async Task BroadcastAsync(string messageJson, string jwt, ModConfig config)
    {
        try
        {
            var body = JsonSerializer.Serialize(new
            {
                target = new[] { "broadcast" },
                broadcaster_id = config.ChannelId,
                message = messageJson
            });

            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.twitch.tv/helix/extensions/pubsub");
            request.Headers.Add("Authorization", $"Bearer {jwt}");
            request.Headers.Add("Client-Id", config.ExtensionClientId);
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");

            var response = await HttpClient.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                Logging.Log($"PubSub sent: {messageJson}");
            }
            else
            {
                var responseBody = await response.Content.ReadAsStringAsync();
                Logging.Log($"PubSub broadcast failed ({response.StatusCode}): {responseBody}");
            }
        }
        catch (Exception ex)
        {
            Logging.Log($"PubSub broadcast error: {ex.Message}");
        }
    }
}
