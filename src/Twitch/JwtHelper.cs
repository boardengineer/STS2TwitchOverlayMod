using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TwitchOverlayMod.Twitch;

internal static class JwtHelper
{
    internal static string CreateToken(string base64Secret, string channelId, string ownerId)
    {
        var header = JsonSerializer.Serialize(new { alg = "HS256", typ = "JWT" });

        var now = DateTimeOffset.UtcNow;
        var exp = now.ToUnixTimeSeconds() + 60;

        var payload = JsonSerializer.Serialize(new
        {
            exp,
            user_id = ownerId,
            role = "external",
            channel_id = channelId,
            pubsub_perms = new { send = new[] { "broadcast" } }
        });

        var headerB64 = Base64UrlEncode(Encoding.UTF8.GetBytes(header));
        var payloadB64 = Base64UrlEncode(Encoding.UTF8.GetBytes(payload));
        var signingInput = $"{headerB64}.{payloadB64}";

        var key = Convert.FromBase64String(base64Secret);
        using var hmac = new HMACSHA256(key);
        var signature = hmac.ComputeHash(Encoding.UTF8.GetBytes(signingInput));
        var signatureB64 = Base64UrlEncode(signature);

        return $"{headerB64}.{payloadB64}.{signatureB64}";
    }

    private static string Base64UrlEncode(byte[] input)
    {
        return Convert.ToBase64String(input)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
