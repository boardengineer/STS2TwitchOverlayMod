using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TwitchOverlayMod.Utility;

namespace TwitchOverlayMod.Twitch;

internal static class LocalBroadcastServer
{
    private static HttpListener?                        _listener;
    private static readonly List<HttpListenerResponse>  _clients = new();
    private static readonly object                      _lock    = new();
    private static CancellationTokenSource?             _cts;

    internal static bool IsRunning => _listener?.IsListening == true;

    internal static void Start(int port)
    {
        if (IsRunning) return;

        _cts      = new CancellationTokenSource();
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://localhost:{port}/");
        _listener.Start();

        _ = Task.Run(() => AcceptLoop(_cts.Token));
        Logging.Log($"Local broadcast server started on port {port}");
    }

    internal static void Stop()
    {
        _cts?.Cancel();
        _listener?.Stop();
        _listener = null;
        lock (_lock) _clients.Clear();
    }

    internal static void Broadcast(string json)
    {
        var bytes = Encoding.UTF8.GetBytes($"data: {json}\n\n");
        lock (_lock)
        {
            var dead = new List<HttpListenerResponse>();
            foreach (var client in _clients)
            {
                try
                {
                    client.OutputStream.Write(bytes, 0, bytes.Length);
                    client.OutputStream.Flush();
                }
                catch
                {
                    dead.Add(client);
                }
            }
            foreach (var d in dead) _clients.Remove(d);
        }
    }

    private static async Task AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener?.IsListening == true)
        {
            try
            {
                var ctx = await _listener.GetContextAsync();
                _ = Task.Run(() => HandleRequest(ctx, ct), ct);
            }
            catch
            {
                break;
            }
        }
    }

    private static void HandleRequest(HttpListenerContext ctx, CancellationToken ct)
    {
        var req  = ctx.Request;
        var resp = ctx.Response;

        resp.AddHeader("Access-Control-Allow-Origin",  "*");
        resp.AddHeader("Access-Control-Allow-Methods", "GET, OPTIONS");
        resp.AddHeader("Access-Control-Allow-Headers", "Content-Type");

        if (req.HttpMethod == "OPTIONS")
        {
            resp.StatusCode = 204;
            resp.Close();
            return;
        }

        if (req.Url?.AbsolutePath == "/events" && req.HttpMethod == "GET")
        {
            resp.ContentType = "text/event-stream; charset=utf-8";
            resp.AddHeader("Cache-Control",     "no-cache");
            resp.AddHeader("X-Accel-Buffering", "no");
            resp.SendChunked = true;

            lock (_lock) _clients.Add(resp);

            var heartbeat = Encoding.UTF8.GetBytes(": ping\n\n");
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    Thread.Sleep(15_000);
                    lock (_lock)
                    {
                        if (!_clients.Contains(resp)) break;
                        resp.OutputStream.Write(heartbeat, 0, heartbeat.Length);
                        resp.OutputStream.Flush();
                    }
                }
            }
            catch
            {
                lock (_lock) _clients.Remove(resp);
            }
        }
        else
        {
            resp.StatusCode = 404;
            resp.Close();
        }
    }
}
