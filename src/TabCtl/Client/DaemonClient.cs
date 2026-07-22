using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TabCtl.Config;

namespace TabCtl.Client;

public sealed class DaemonException(string message) : Exception(message);

/// <summary>
/// Thin HTTP client for the daemon. Subcommands are short-lived processes, so this also
/// starts the daemon on demand — there is no separate "start the server first" step.
/// </summary>
public sealed class DaemonClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly int _port;

    private DaemonClient(int port, string token)
    {
        _port = port;
        _http = new HttpClient
        {
            BaseAddress = new Uri($"http://127.0.0.1:{port}"),
            Timeout = TimeSpan.FromMinutes(2),
        };
        _http.DefaultRequestHeaders.Add("X-Cdp-Token", token);
    }

    public static async Task<DaemonClient> ConnectAsync(int port, bool autostart = true)
    {
        var client = new DaemonClient(port, Paths.GetOrCreateToken());

        if (await client.IsAliveAsync()) return client;

        if (!autostart)
        {
            client.Dispose();
            throw new DaemonException($"daemon not running on port {port} (start it with: tabctl serve)");
        }

        Spawn(port);

        // Native AOT starts fast, but give Kestrel a moment to bind.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(150);
            if (await client.IsAliveAsync()) return client;
        }

        client.Dispose();
        throw new DaemonException(
            $"daemon failed to start on port {port} — see {Paths.LogFile}");
    }

    private static void Spawn(int port)
    {
        var exe = Environment.ProcessPath
            ?? throw new DaemonException("cannot determine tabctl's own path to spawn the daemon");

        Directory.CreateDirectory(Paths.Dir);

        // nohup + & so the daemon outlives this short-lived CLI process and survives the
        // terminal going away. Output goes to the log rather than the caller's stdout.
        var cmd = $"nohup {Quote(exe)} serve --foreground --port {port} >> {Quote(Paths.LogFile)} 2>&1 < /dev/null &";

        using var proc = Process.Start(new ProcessStartInfo("/bin/sh")
        {
            ArgumentList = { "-c", cmd },
            UseShellExecute = false,
        });
        proc?.WaitForExit(5000);
    }

    private static string Quote(string path) => "'" + path.Replace("'", "'\\''") + "'";

    private async Task<bool> IsAliveAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            using var res = await _http.GetAsync("/status", cts.Token);
            return res.StatusCode is HttpStatusCode.OK or HttpStatusCode.Unauthorized;
        }
        catch (HttpRequestException) { return false; }
        catch (OperationCanceledException) { return false; }
    }

    // ------------------------------------------------------------------ requests

    public Task<string> StatusAsync() => GetAsync("/status");

    public Task<string> EventsAsync(long since, string? filter)
    {
        var url = $"/events?since={since}";
        if (!string.IsNullOrWhiteSpace(filter)) url += $"&filter={Uri.EscapeDataString(filter)}";
        return GetAsync(url);
    }

    public async Task ShutdownAsync()
    {
        try { await PostAsync("/shutdown", "{}"); }
        catch (HttpRequestException) { /* it went away, which is the point */ }
    }

    /// <summary>Sends a CDP command; returns the raw JSON of its <c>result</c> object.</summary>
    public async Task<string> CdpAsync(string method, string? paramsJson = null, int? timeoutMs = null)
    {
        var sb = new StringBuilder("{\"method\":").Append(Json.Str(method));
        if (!string.IsNullOrWhiteSpace(paramsJson)) sb.Append(",\"params\":").Append(paramsJson);
        if (timeoutMs is not null) sb.Append(",\"timeoutMs\":").Append(timeoutMs.Value);
        sb.Append('}');

        var body = await PostAsync("/cdp", sb.ToString());

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.TryGetProperty("result", out var result) ? result.GetRawText() : "{}";
    }

    private async Task<string> GetAsync(string path)
    {
        using var res = await _http.GetAsync(path);
        return await ReadAsync(res);
    }

    private async Task<string> PostAsync(string path, string json)
    {
        using var content = new StringContent(json, Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var res = await _http.PostAsync(path, content);
        return await ReadAsync(res);
    }

    private static async Task<string> ReadAsync(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        if (res.IsSuccessStatusCode) return body;

        // The daemon reports failures as {"error": "..."}; surface that rather than a status code.
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var err))
                throw new DaemonException(err.GetString() ?? "unknown error");
        }
        catch (JsonException) { }

        throw new DaemonException($"daemon returned {(int)res.StatusCode}: {body}");
    }

    public void Dispose() => _http.Dispose();
}
