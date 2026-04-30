using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TwitchOverlayMod.Utility;

namespace TwitchOverlayMod.Twitch;

internal static class LocalCallbackServer
{
    internal readonly record struct CallbackResult(
        string Jwt, string RefreshToken, string ChannelId, string OwnerId, string Login, int ExpiresIn);

    internal static async Task<CallbackResult?> WaitForCallbackAsync(int port, CancellationToken cancellationToken)
    {
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();

        try
        {
            var contextTask = listener.GetContextAsync();
            await Task.WhenAny(contextTask, Task.Delay(Timeout.Infinite, cancellationToken));

            if (cancellationToken.IsCancellationRequested)
                return null;

            var context = await contextTask;

            var responseHtml = Encoding.UTF8.GetBytes(
                "<html><body style='font-family:sans-serif;background:#0e0e10;color:#efeff1;display:flex;" +
                "align-items:center;justify-content:center;height:100vh;margin:0'>" +
                "<h2>Connected! You can close this window.</h2></body></html>");

            context.Response.ContentType     = "text/html; charset=utf-8";
            context.Response.ContentLength64 = responseHtml.Length;
            await context.Response.OutputStream.WriteAsync(responseHtml);
            context.Response.Close();

            var query = ParseQueryString(context.Request.Url?.Query ?? "");
            query.TryGetValue("jwt",           out var jwt);
            query.TryGetValue("refresh_token", out var refresh);
            query.TryGetValue("channel_id",    out var channelId);
            query.TryGetValue("owner_id",      out var ownerId);
            query.TryGetValue("login",         out var login);
            query.TryGetValue("expires_in",    out var expiresInStr);

            if (jwt == null || refresh == null || channelId == null || ownerId == null)
            {
                Logging.Log("Callback missing required parameters.");
                return null;
            }

            int.TryParse(expiresInStr, out var expiresIn);
            return new CallbackResult(jwt, refresh, channelId, ownerId, login ?? channelId, expiresIn > 0 ? expiresIn : 900);
        }
        finally
        {
            listener.Stop();
        }
    }

    internal static int FindAvailablePort()
    {
        using var tmp = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        tmp.Start();
        var port = ((IPEndPoint)tmp.LocalEndpoint).Port;
        tmp.Stop();
        return port;
    }

    private static Dictionary<string, string> ParseQueryString(string query) =>
        query.TrimStart('?')
             .Split('&', StringSplitOptions.RemoveEmptyEntries)
             .Select(pair => pair.Split('=', 2))
             .ToDictionary(
                 p => Uri.UnescapeDataString(p[0]),
                 p => p.Length > 1 ? Uri.UnescapeDataString(p[1]) : string.Empty,
                 StringComparer.OrdinalIgnoreCase);
}
