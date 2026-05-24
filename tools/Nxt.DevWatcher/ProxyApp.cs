using System.Net;
using System.Net.WebSockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Nxt.DevWatcher;

/// <summary>
/// Kestrel-based front end. The user's browser only ever talks to this; we proxy HTTP/WS to
/// the backend <c>dotnet watch</c> process. When the backend is unreachable or supervisor
/// reports unhealthy, we serve a placeholder page so the user never sees raw bind errors or
/// unhandled-exception stack traces from a wedged backend.
/// </summary>
public static class ProxyApp
{
    public static WebApplication Build(string frontUrl, State state)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls(frontUrl);

        builder.Services.AddSingleton(state);
        builder.Services.AddSingleton(new HttpClient(new SocketsHttpHandler
        {
            // The backend may legitimately take a moment to respond mid-rebuild; don't auto-retry,
            // we handle that ourselves with the placeholder.
            AllowAutoRedirect = false,
            UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(3),
        })
        {
            Timeout = TimeSpan.FromMinutes(2),
        });

        var app = builder.Build();

        // WebSocket support — without this, IsWebSocketRequest returns false and SignalR
        // (/_blazor for Blazor Server) and any other WS endpoints can't upgrade through us.
        app.UseWebSockets(new WebSocketOptions
        {
            KeepAliveInterval = TimeSpan.FromMinutes(2),
        });

        // Babysitter status endpoint — polled by our injected reload script.
        app.MapGet("/__babysitter/status", (State s) => Results.Json(new
        {
            epoch = s.Epoch,
            healthy = s.Healthy,
            lastError = s.LastError,
        }));

        // Catch-all proxy.
        app.Map("/{**path}", async (HttpContext ctx, HttpClient http, State s) =>
        {
            if (ctx.WebSockets.IsWebSocketRequest)
                await ProxyWebSocketAsync(ctx, s.BackendPort);
            else
                await ProxyHttpAsync(ctx, http, s);
        });

        return app;
    }

    private static async Task ProxyHttpAsync(HttpContext ctx, HttpClient http, State state)
    {
        var target = $"http://127.0.0.1:{state.BackendPort}{ctx.Request.Path}{ctx.Request.QueryString}";
        using var upstreamReq = new HttpRequestMessage(new HttpMethod(ctx.Request.Method), target);

        // Body
        if (HasBody(ctx.Request))
        {
            upstreamReq.Content = new StreamContent(ctx.Request.Body);
            CopyContentHeaders(ctx.Request.Headers, upstreamReq.Content.Headers);
        }
        // Request headers
        foreach (var (key, value) in ctx.Request.Headers)
        {
            if (IsHopByHop(key)) continue;
            if (key.Equals("Host", StringComparison.OrdinalIgnoreCase))
                upstreamReq.Headers.Host = $"127.0.0.1:{state.BackendPort}";
            else
                upstreamReq.Headers.TryAddWithoutValidation(key, value.ToArray());
        }

        HttpResponseMessage upstream;
        try
        {
            upstream = await http.SendAsync(upstreamReq, HttpCompletionOption.ResponseHeadersRead, ctx.RequestAborted);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Backend down or extremely slow — serve placeholder.
            await ServePlaceholderAsync(ctx, state);
            return;
        }

        using (upstream)
        {
            // Backend returned 5xx — for browser navigations, intercept and serve the
            // placeholder instead of letting DeveloperExceptionPage / stack traces leak
            // through. API/XHR calls still get the original error (the app code may want it).
            var isBrowserNavigation = ctx.Request.Headers.Accept.ToString()
                .Contains("text/html", StringComparison.OrdinalIgnoreCase);
            if (isBrowserNavigation && (int)upstream.StatusCode >= 500)
            {
                string errorBody = "";
                try { errorBody = await upstream.Content.ReadAsStringAsync(ctx.RequestAborted); } catch { }
                var summary = ExtractErrorSummary(errorBody, (int)upstream.StatusCode);
                state.MarkDown($"backend returned {(int)upstream.StatusCode}: {summary}");
                await ServePlaceholderAsync(ctx, state);
                return;
            }

            ctx.Response.StatusCode = (int)upstream.StatusCode;
            foreach (var (key, value) in upstream.Headers)
            {
                if (IsHopByHop(key)) continue;
                ctx.Response.Headers[key] = value.ToArray();
            }
            foreach (var (key, value) in upstream.Content.Headers)
            {
                if (IsHopByHop(key)) continue;
                ctx.Response.Headers[key] = value.ToArray();
            }
            ctx.Response.Headers.Remove("Content-Length");
            ctx.Response.Headers.Remove("transfer-encoding");

            var mediaType = upstream.Content.Headers.ContentType?.MediaType ?? "";
            if (mediaType.Equals("text/html", StringComparison.OrdinalIgnoreCase))
            {
                var bytes = await upstream.Content.ReadAsByteArrayAsync(ctx.RequestAborted);
                var injected = InjectReloadScript(bytes);
                ctx.Response.ContentLength = injected.Length;
                await ctx.Response.Body.WriteAsync(injected, ctx.RequestAborted);
            }
            else
            {
                await upstream.Content.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
            }
        }
    }

    /// <summary>
    /// Pulls a one-line summary out of a 5xx body. For ASP.NET's DeveloperExceptionPage, that
    /// is the first <c>&lt;title&gt;</c> or the first exception message we can find. Falls back
    /// to a generic "HTTP &lt;code&gt;" if nothing parseable.
    /// </summary>
    private static string ExtractErrorSummary(string body, int statusCode)
    {
        if (string.IsNullOrWhiteSpace(body)) return $"HTTP {statusCode}";

        // DeveloperExceptionPage's <h2>: <h2 class="stackerror">InvalidOperationException: foo</h2>
        var h2Match = System.Text.RegularExpressions.Regex.Match(body,
            @"<h2[^>]*class=""stackerror""[^>]*>([^<]+)</h2>",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (h2Match.Success)
            return Truncate(WebUtility.HtmlDecode(h2Match.Groups[1].Value).Trim(), 500);

        // <title> fallback. Skip generic "Internal Server Error" placeholders.
        var titleMatch = System.Text.RegularExpressions.Regex.Match(body,
            @"<title>([^<]+)</title>", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (titleMatch.Success)
        {
            var t = WebUtility.HtmlDecode(titleMatch.Groups[1].Value).Trim();
            if (t.Length > 0 && !t.Equals("Internal Server Error", StringComparison.OrdinalIgnoreCase))
                return Truncate(t, 500);
        }

        // Fully-qualified exception type + message, anchored to a real Exception class name
        // (avoids matching CSS like `.showRawException:hover {`).
        var excMatch = System.Text.RegularExpressions.Regex.Match(body,
            @"\b((?:[A-Z][\w]*\.)+[A-Z]\w*Exception)\s*:\s*([^\r\n<]+)");
        if (excMatch.Success)
            return Truncate($"{excMatch.Groups[1].Value}: {excMatch.Groups[2].Value.Trim()}", 500);

        return $"HTTP {statusCode}";
    }

    private static string Truncate(string s, int max)
    {
        // HTML-decode first — DeveloperExceptionPage encodes newlines as &#xA; inside attributes,
        // and we want to collapse on actual line breaks. Then strip to the first non-empty line.
        var decoded = WebUtility.HtmlDecode(s);
        foreach (var line in decoded.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;
            return trimmed.Length <= max ? trimmed : trimmed[..max] + "…";
        }
        return decoded.Trim();
    }

    private static async Task ProxyWebSocketAsync(HttpContext ctx, int backendPort)
    {
        var path = ctx.Request.Path + ctx.Request.QueryString;
        var upstreamUri = new Uri($"ws://127.0.0.1:{backendPort}{path}");

        using var client = new ClientWebSocket();
        foreach (var sp in ctx.WebSockets.WebSocketRequestedProtocols)
            client.Options.AddSubProtocol(sp);

        // Forward client headers so SignalR sees the right cookies/auth/etc. Skip WS-handshake
        // headers (ClientWebSocket sets those itself) and hop-by-hop headers.
        foreach (var (key, value) in ctx.Request.Headers)
        {
            if (IsHopByHop(key) || IsWebSocketHandshakeHeader(key) || key == "Host") continue;
            try { client.Options.SetRequestHeader(key, value.ToString()); } catch { }
        }

        try { await client.ConnectAsync(upstreamUri, ctx.RequestAborted); }
        catch { ctx.Response.StatusCode = 502; return; }

        using var server = await ctx.WebSockets.AcceptWebSocketAsync(
            client.SubProtocol);

        var c2s = PumpAsync(server, client, ctx.RequestAborted);
        var s2c = PumpAsync(client, server, ctx.RequestAborted);
        await Task.WhenAny(c2s, s2c);

        try { await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); } catch { }
        try { await server.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); } catch { }
    }

    private static async Task PumpAsync(WebSocket src, WebSocket dst, CancellationToken ct)
    {
        var buffer = new byte[8192];
        try
        {
            while (src.State == WebSocketState.Open && dst.State == WebSocketState.Open)
            {
                var result = await src.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await dst.CloseOutputAsync(result.CloseStatus ?? WebSocketCloseStatus.NormalClosure,
                        result.CloseStatusDescription, ct);
                    return;
                }
                await dst.SendAsync(new ArraySegment<byte>(buffer, 0, result.Count),
                    result.MessageType, result.EndOfMessage, ct);
            }
        }
        catch { /* peer gone */ }
    }

    private static async Task ServePlaceholderAsync(HttpContext ctx, State state)
    {
        ctx.Response.StatusCode = 503;
        ctx.Response.Headers.RetryAfter = "1";
        ctx.Response.ContentType = "text/html; charset=utf-8";
        var html = BuildPlaceholderHtml(state);
        await ctx.Response.WriteAsync(html, ctx.RequestAborted);
    }

    private static string BuildPlaceholderHtml(State state)
    {
        return $$$"""
            <!DOCTYPE html>
            <html><head>
              <title>Rebuilding…</title>
              <meta charset="utf-8" />
              <style>
                body{font-family:system-ui;background:#111;color:#eee;display:flex;align-items:center;
                  justify-content:center;min-height:100vh;margin:0}
                .card{background:#1c1c1c;border:1px solid #2a2a2a;padding:2rem 2.5rem;border-radius:8px;
                  text-align:center;display:flex;align-items:center;gap:1rem}
                .spinner{width:20px;height:20px;border:3px solid #333;
                  border-top-color:#6cf;border-radius:50%;animation:s 1s linear infinite}
                @keyframes s{to{transform:rotate(360deg)}}
                h2{margin:0;font-weight:500;font-size:1.05rem;color:#ccc}
              </style>
            </head><body>
              <div class="card"><div class="spinner"></div><h2>Rebuilding…</h2></div>
              {{{BabysitterScript}}}
            </body></html>
            """;
    }

    private static byte[] InjectReloadScript(byte[] html)
    {
        var bodyClose = Encoding.UTF8.GetBytes("</body>");
        var idx = IndexOfIgnoreCase(html, bodyClose);
        var script = Encoding.UTF8.GetBytes(BabysitterScript);
        if (idx < 0)
        {
            var combined = new byte[html.Length + script.Length];
            html.CopyTo(combined.AsSpan());
            script.CopyTo(combined.AsSpan(html.Length));
            return combined;
        }
        var output = new byte[html.Length + script.Length];
        html.AsSpan(0, idx).CopyTo(output.AsSpan(0));
        script.CopyTo(output.AsSpan(idx));
        html.AsSpan(idx).CopyTo(output.AsSpan(idx + script.Length));
        return output;
    }

    /// <summary>
    /// Polls the babysitter status endpoint. Shows an overlay while unhealthy; reloads the page
    /// when the backend comes back AND the epoch differs from what was last seen healthy.
    /// </summary>
    private const string BabysitterScript = """
        <script>(function(){
          if(window.__bb_installed)return;window.__bb_installed=true;
          var lastHealthyEpoch=null,downSince=null,overlay=null;
          function showOverlay(){
            if(overlay)return;
            overlay=document.createElement('div');
            overlay.style.cssText='position:fixed;inset:0;background:rgba(0,0,0,.85);color:#ccc;'+
              'display:flex;align-items:center;justify-content:center;z-index:2147483647;'+
              'font:14px/1.4 system-ui';
            overlay.innerHTML='<div style="background:#1c1c1c;border:1px solid #2a2a2a;padding:1.25rem 1.75rem;'+
              'border-radius:8px;display:flex;align-items:center;gap:1rem">'+
              '<div style="width:20px;height:20px;border:3px solid #333;border-top-color:#6cf;'+
              'border-radius:50%;animation:bbspin 1s linear infinite"></div>'+
              '<span>Rebuilding…</span></div>'+
              '<style>@keyframes bbspin{to{transform:rotate(360deg)}}</style>';
            document.body.appendChild(overlay);
          }
          function hideOverlay(){ if(overlay){overlay.remove();overlay=null;} }
          async function poll(){
            try{
              var r=await fetch('/__babysitter/status',{cache:'no-store'});
              var d=await r.json();
              if(!d.healthy){
                downSince=downSince||Date.now();
                showOverlay();
              } else {
                if(lastHealthyEpoch===null){lastHealthyEpoch=d.epoch;}
                else if(d.epoch!==lastHealthyEpoch){ location.reload(); return; }
                if(downSince){ location.reload(); return; }
                hideOverlay();
              }
            }catch(e){
              downSince=downSince||Date.now();
              showOverlay();
            }
            setTimeout(poll,500);
          }
          poll();
        })();</script>
        """;

    private static int IndexOfIgnoreCase(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                var a = haystack[i + j];
                var b = needle[j];
                if (a is >= (byte)'A' and <= (byte)'Z') a = (byte)(a + 32);
                if (b is >= (byte)'A' and <= (byte)'Z') b = (byte)(b + 32);
                if (a != b) { match = false; break; }
            }
            if (match) return i;
        }
        return -1;
    }

    private static bool HasBody(HttpRequest r) =>
        r.ContentLength is > 0 ||
        (r.Headers.TryGetValue("Transfer-Encoding", out var te) && te.ToString().Length > 0);

    private static bool IsHopByHop(string name) => name.ToLowerInvariant() switch
    {
        "connection" or "keep-alive" or "proxy-authenticate" or "proxy-authorization" or "te"
            or "trailers" or "transfer-encoding" or "upgrade" or "content-length" => true,
        _ => false,
    };

    private static bool IsWebSocketHandshakeHeader(string name) => name.ToLowerInvariant() switch
    {
        "sec-websocket-key" or "sec-websocket-version" or "sec-websocket-extensions"
            or "sec-websocket-protocol" or "sec-websocket-accept" => true,
        _ => false,
    };

    private static void CopyContentHeaders(IHeaderDictionary src, System.Net.Http.Headers.HttpContentHeaders dst)
    {
        if (src.TryGetValue("Content-Type", out var ct))
            dst.TryAddWithoutValidation("Content-Type", ct.ToArray());
        if (src.TryGetValue("Content-Length", out var cl))
            dst.TryAddWithoutValidation("Content-Length", cl.ToArray());
    }
}
