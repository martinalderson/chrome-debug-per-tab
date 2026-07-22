using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace TabCtl.Server;

public sealed record TabInfo(long TabId, string? Url, string? Title);

public sealed record BufferedEvent(long Seq, string Method, string ParamsJson);

public sealed class CdpException(string message) : Exception(message);

/// <summary>
/// Holds the single extension WebSocket, multiplexes CDP commands over it, and buffers the
/// events it pushes so short-lived CLI invocations can read history they weren't around for.
/// </summary>
public sealed class RelayState
{
    private const int RingCapacity = 2000;
    private const int MaxMessageBytes = 64 * 1024 * 1024; // screenshots are big; runaways are not
    private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(20);

    private readonly Lock _lock = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<CdpReply>> _pending = new();
    private readonly BufferedEvent[] _ring = new BufferedEvent[RingCapacity];

    private WebSocket? _ext;
    private TabInfo? _tab;
    private int _nextId;
    private long _seq;
    private string? _lastDetachReason;
    private DateTimeOffset? _lastDetachAt;

    /// <summary>Timestamped to the daemon log — the only window into why a session ended.</summary>
    private static void Log(string message) =>
        Console.WriteLine($"{DateTimeOffset.Now:HH:mm:ss} {message}");

    private readonly record struct CdpReply(string? Result, string? Error);

    public bool ExtensionConnected
    {
        get { lock (_lock) return _ext is { State: WebSocketState.Open }; }
    }

    public TabInfo? Tab
    {
        get { lock (_lock) return _tab; }
    }

    public long Seq
    {
        get { lock (_lock) return _seq; }
    }

    // ------------------------------------------------------------------ outbound commands

    public async Task<string> SendCommandAsync(string method, string paramsJson, TimeSpan timeout, CancellationToken ct)
    {
        WebSocket sock;
        lock (_lock)
        {
            if (_ext is not { State: WebSocketState.Open })
                throw new CdpException("extension not connected — arm a tab by clicking the tabctl toolbar button");
            if (_tab is null)
                throw new CdpException("no tab is armed — click the tabctl toolbar button on the tab you want to drive");
            sock = _ext;
        }

        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<CdpReply>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        try
        {
            if (string.IsNullOrWhiteSpace(paramsJson)) paramsJson = "{}";
            var frame = $"{{\"id\":{id},\"method\":{Json.Str(method)},\"params\":{paramsJson}}}";
            await SendRawAsync(sock, frame, ct);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);

            CdpReply reply;
            try
            {
                reply = await tcs.Task.WaitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new CdpException($"'{method}' timed out after {timeout.TotalSeconds:0.#}s");
            }

            if (reply.Error is not null) throw new CdpException(reply.Error);
            return reply.Result ?? "{}";
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task SendRawAsync(WebSocket sock, string text, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await _sendLock.WaitAsync(ct);
        try
        {
            await sock.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    // ------------------------------------------------------------------ event buffer

    public IReadOnlyList<BufferedEvent> EventsSince(long since, string? filter)
    {
        var matcher = EventFilter.Parse(filter);
        var result = new List<BufferedEvent>();

        lock (_lock)
        {
            // Anything older than the ring's window is simply gone.
            var oldest = Math.Max(since + 1, _seq - RingCapacity + 1);
            if (oldest < 1) oldest = 1;

            for (var s = oldest; s <= _seq; s++)
            {
                var evt = _ring[(s - 1) % RingCapacity];
                if (evt is not null && evt.Seq == s && matcher(evt.Method)) result.Add(evt);
            }
        }

        return result;
    }

    private void Buffer(string method, string paramsJson)
    {
        lock (_lock)
        {
            _seq++;
            _ring[(_seq - 1) % RingCapacity] = new BufferedEvent(_seq, method, paramsJson);
        }
    }

    // ------------------------------------------------------------------ extension session

    public async Task RunExtensionAsync(WebSocket sock, CancellationToken ct)
    {
        lock (_lock)
        {
            _ext?.Abort();
            _ext = sock;
        }
        Log("extension connected");

        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pinger = PingLoopAsync(sock, sessionCts.Token);

        try
        {
            while (sock.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var message = await ReceiveMessageAsync(sock, ct);
                if (message is null) break;
                HandleMessage(message);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        finally
        {
            await sessionCts.CancelAsync();
            try { await pinger; } catch { /* shutting down */ }

            bool stillArmed;
            lock (_lock)
            {
                if (ReferenceEquals(_ext, sock)) _ext = null;
                // _tab is deliberately left intact: a dropped socket usually means the MV3
                // worker was recycled, and the CDP session outlives it. Only an explicit
                // "detached" message clears the armed tab.
                stillArmed = _tab is not null;
            }

            Log($"extension disconnected (tab still armed: {stillArmed})");
            FailPending("extension disconnected");
        }
    }

    private async Task PingLoopAsync(WebSocket sock, CancellationToken ct)
    {
        // This is load-bearing, not a health check: WebSocket traffic is what resets Chrome's
        // 30s MV3 idle timer, so without it the extension's worker dies mid-session.
        try
        {
            while (!ct.IsCancellationRequested && sock.State == WebSocketState.Open)
            {
                await Task.Delay(PingInterval, ct);
                if (sock.State != WebSocketState.Open) break;
                await SendRawAsync(sock, "{\"type\":\"ping\"}", ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
    }

    private static async Task<string?> ReceiveMessageAsync(WebSocket sock, CancellationToken ct)
    {
        var buffer = new byte[32 * 1024];
        using var acc = new MemoryStream();

        while (true)
        {
            var res = await sock.ReceiveAsync(buffer, ct);
            if (res.MessageType == WebSocketMessageType.Close) return null;

            acc.Write(buffer, 0, res.Count);
            if (acc.Length > MaxMessageBytes)
                throw new WebSocketException($"message exceeded {MaxMessageBytes} bytes");

            if (res.EndOfMessage) break;
        }

        return Encoding.UTF8.GetString(acc.GetBuffer(), 0, (int)acc.Length);
    }

    private void HandleMessage(string raw)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(raw); }
        catch (JsonException) { return; }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;

            // Command reply.
            if (root.TryGetProperty("id", out var idEl) && idEl.TryGetInt32(out var id))
            {
                if (!_pending.TryRemove(id, out var tcs)) return;
                var error = root.TryGetProperty("error", out var errEl) ? errEl.GetString() : null;
                var result = root.TryGetProperty("result", out var resEl) ? resEl.GetRawText() : null;
                tcs.TrySetResult(new CdpReply(result, error));
                return;
            }

            var type = root.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : null;
            switch (type)
            {
                case "event":
                    var method = root.TryGetProperty("method", out var mEl) ? mEl.GetString() : null;
                    if (method is null) return;
                    var p = root.TryGetProperty("params", out var pEl) ? pEl.GetRawText() : "{}";
                    Buffer(method, p);
                    break;

                case "attached":
                    TabInfo attached;
                    lock (_lock)
                    {
                        attached = new TabInfo(
                            root.TryGetProperty("tabId", out var tEl) && tEl.TryGetInt64(out var tid) ? tid : -1,
                            root.TryGetProperty("url", out var uEl) ? uEl.GetString() : null,
                            root.TryGetProperty("title", out var ttEl) ? ttEl.GetString() : null);
                        _tab = attached;
                        _lastDetachReason = null;
                    }
                    Log($"attached tab {attached.TabId} — {attached.Url}");
                    break;

                case "detached":
                    var reason = root.TryGetProperty("reason", out var rEl) ? rEl.GetString() : null;
                    lock (_lock)
                    {
                        _tab = null;
                        _lastDetachReason = reason ?? "unknown";
                        _lastDetachAt = DateTimeOffset.Now;
                    }
                    Log($"detached — reason: {reason ?? "unknown"}");
                    FailPending($"tab detached: {reason ?? "unknown"}");
                    break;
            }
        }
    }

    private void FailPending(string reason)
    {
        foreach (var key in _pending.Keys)
        {
            if (_pending.TryRemove(key, out var tcs))
                tcs.TrySetResult(new CdpReply(null, reason));
        }
    }

    // ------------------------------------------------------------------ status

    public string StatusJson()
    {
        lock (_lock)
        {
            var attached = _tab is not null;
            var connected = _ext is { State: WebSocketState.Open };
            var sb = new StringBuilder("{");
            sb.Append("\"attached\":").Append(Json.Bool(attached));
            sb.Append(",\"extensionConnected\":").Append(Json.Bool(connected));
            sb.Append(",\"seq\":").Append(_seq);
            if (_tab is not null)
            {
                sb.Append(",\"tabId\":").Append(_tab.TabId);
                sb.Append(",\"url\":").Append(Json.Str(_tab.Url));
                sb.Append(",\"title\":").Append(Json.Str(_tab.Title));
            }
            if (_lastDetachReason is not null)
            {
                sb.Append(",\"lastDetachReason\":").Append(Json.Str(_lastDetachReason));
                sb.Append(",\"lastDetachAt\":").Append(Json.Str(_lastDetachAt?.ToString("HH:mm:ss")));
            }
            sb.Append('}');
            return sb.ToString();
        }
    }
}

internal static class EventFilter
{
    /// <summary>Comma-separated exact names or <c>Domain.*</c> prefixes; null matches everything.</summary>
    public static Func<string, bool> Parse(string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter)) return _ => true;

        var parts = filter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return _ => true;

        return method =>
        {
            foreach (var part in parts)
            {
                if (part.EndsWith('*'))
                {
                    if (method.StartsWith(part[..^1], StringComparison.OrdinalIgnoreCase)) return true;
                }
                else if (method.Equals(part, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        };
    }
}
