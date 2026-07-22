using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TabCtl.Config;

namespace TabCtl.Server;

public static class RelayServer
{
    public static async Task<int> RunAsync(int port, CancellationToken ct)
    {
        var token = Paths.GetOrCreateToken();
        var state = new RelayState();

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.ListenLocalhost(port));

        var app = builder.Build();
        app.UseWebSockets();

        MapGuard(app, token);
        MapExtensionSocket(app, state);
        MapApi(app, state);

        try
        {
            await app.StartAsync(ct);
        }
        catch (IOException ex)
        {
            await Console.Error.WriteLineAsync($"Error: cannot bind 127.0.0.1:{port} — {ex.Message}");
            return 1;
        }

        Console.WriteLine($"tabctl daemon listening on http://127.0.0.1:{port}");
        await app.WaitForShutdownAsync(ct);
        return 0;
    }

    /// <summary>
    /// Token + Host checks for everything except the extension socket.
    ///
    /// The token lives in a custom header on purpose — custom headers force a CORS preflight
    /// that we never answer, so a web page cannot reach these endpoints even though they sit
    /// on localhost. The Host check blocks DNS-rebinding attempts.
    /// </summary>
    private static void MapGuard(WebApplication app, string token)
    {
        var expected = Encoding.UTF8.GetBytes(token);

        app.Use(async (ctx, next) =>
        {
            if (ctx.Request.Path.StartsWithSegments("/ext"))
            {
                await next();
                return;
            }

            var host = ctx.Request.Host.Host;
            if (host is not ("127.0.0.1" or "localhost" or "::1" or "[::1]"))
            {
                ctx.Response.StatusCode = 403;
                await ctx.Response.WriteAsync("{\"error\":\"bad host\"}");
                return;
            }

            var supplied = Encoding.UTF8.GetBytes(ctx.Request.Headers["X-Cdp-Token"].ToString());
            if (!CryptographicOperations.FixedTimeEquals(supplied, expected))
            {
                ctx.Response.StatusCode = 401;
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsync("{\"error\":\"bad or missing X-Cdp-Token\"}");
                return;
            }

            await next();
        });
    }

    private static void MapExtensionSocket(WebApplication app, RelayState state)
    {
        app.Map("/ext", async (HttpContext ctx) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = 400;
                return;
            }

            // A malicious page *can* open a WebSocket to localhost — WS has no CORS preflight —
            // but it cannot forge Origin, so this check is what keeps pages out.
            var origin = ctx.Request.Headers.Origin.ToString();
            if (!origin.StartsWith("chrome-extension://", StringComparison.Ordinal))
            {
                ctx.Response.StatusCode = 403;
                return;
            }

            using var sock = await ctx.WebSockets.AcceptWebSocketAsync();
            await state.RunExtensionAsync(sock, ctx.RequestAborted);
        });
    }

    private static void MapApi(WebApplication app, RelayState state)
    {
        app.MapGet("/status", () => Results.Text(state.StatusJson(), "application/json"));

        app.MapPost("/cdp", async (HttpContext ctx) =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            var body = await reader.ReadToEndAsync(ctx.RequestAborted);

            string method;
            var paramsJson = "{}";
            var timeout = TimeSpan.FromSeconds(30);

            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                method = root.TryGetProperty("method", out var m) ? m.GetString() ?? "" : "";
                if (root.TryGetProperty("params", out var p)) paramsJson = p.GetRawText();
                if (root.TryGetProperty("timeoutMs", out var t) && t.TryGetInt32(out var ms))
                    timeout = TimeSpan.FromMilliseconds(ms);
            }
            catch (JsonException ex)
            {
                return Error(400, $"invalid request body: {ex.Message}");
            }

            if (string.IsNullOrWhiteSpace(method)) return Error(400, "missing 'method'");

            try
            {
                var result = await state.SendCommandAsync(method, paramsJson, timeout, ctx.RequestAborted);
                return Results.Text($"{{\"result\":{result}}}", "application/json");
            }
            catch (CdpException ex)
            {
                return Error(409, ex.Message);
            }
        });

        app.MapGet("/events", (HttpContext ctx) =>
        {
            var since = long.TryParse(ctx.Request.Query["since"], out var s) ? s : 0;
            var filter = ctx.Request.Query["filter"].ToString();
            var events = state.EventsSince(since, filter);

            var sb = new StringBuilder("{\"nextSeq\":").Append(state.Seq).Append(",\"events\":[");
            for (var i = 0; i < events.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"seq\":").Append(events[i].Seq)
                  .Append(",\"method\":").Append(Json.Str(events[i].Method))
                  .Append(",\"params\":").Append(events[i].ParamsJson)
                  .Append('}');
            }
            sb.Append("]}");
            return Results.Text(sb.ToString(), "application/json");
        });

        app.MapPost("/shutdown", (IHostApplicationLifetime life) =>
        {
            life.StopApplication();
            return Results.Text("{\"ok\":true}", "application/json");
        });
    }

    private static IResult Error(int status, string message) =>
        Results.Text($"{{\"error\":{Json.Str(message)}}}", "application/json", statusCode: status);
}
