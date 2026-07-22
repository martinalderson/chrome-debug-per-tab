using System.Text;
using System.Text.Json;
using TabCtl.Client;

namespace TabCtl.Commands;

/// <summary>Commands that read daemon state rather than drive the tab.</summary>
public static class InspectCommands
{
    public static async Task<int> StatusAsync(DaemonClient client, ArgParser args)
    {
        var json = await client.StatusAsync();

        if (args.Get("format", "json") == "json")
        {
            Console.WriteLine(Json.Pretty(json));
            return 0;
        }

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var attached = root.TryGetProperty("attached", out var a) && a.GetBoolean();

        if (!attached)
        {
            Console.WriteLine("No tab armed. Click the tabctl toolbar button on the tab you want to drive.");
            return 0;
        }

        Console.WriteLine($"tab      {Json.PathString(root, "tabId")}");
        Console.WriteLine($"title    {Json.PathString(root, "title")}");
        Console.WriteLine($"url      {Json.PathString(root, "url")}");
        Console.WriteLine($"seq      {Json.PathString(root, "seq")}");
        return 0;
    }

    private const string LogFilter = "Runtime.consoleAPICalled,Runtime.exceptionThrown,Log.entryAdded";
    private const string NetFilter = "Network.requestWillBeSent,Network.responseReceived,Network.loadingFailed";

    public static async Task<int> LogsAsync(DaemonClient client, ArgParser args)
    {
        var since = args.GetLong("since", 0);
        var body = await client.EventsAsync(since, LogFilter);

        if (args.Has("json"))
        {
            Console.WriteLine(Json.Pretty(body));
            return 0;
        }

        using var doc = JsonDocument.Parse(body);
        foreach (var evt in doc.RootElement.GetProperty("events").EnumerateArray())
        {
            var seq = evt.GetProperty("seq").GetInt64();
            var method = evt.GetProperty("method").GetString();
            var p = evt.GetProperty("params");
            Console.WriteLine($"[{seq}] {FormatLogEvent(method, p)}");
        }

        Console.Error.WriteLine($"-- nextSeq={doc.RootElement.GetProperty("nextSeq").GetInt64()}");
        return 0;
    }

    private static string FormatLogEvent(string? method, JsonElement p) => method switch
    {
        "Runtime.consoleAPICalled" => $"{Json.PathString(p, "type")?.ToUpperInvariant()} {ConsoleArgs(p)}",
        "Runtime.exceptionThrown" =>
            "EXCEPTION " + (Json.PathString(p, "exceptionDetails.exception.description")
                            ?? Json.PathString(p, "exceptionDetails.text")
                            ?? "(no detail)"),
        "Log.entryAdded" =>
            $"{Json.PathString(p, "entry.level")?.ToUpperInvariant()} {Json.PathString(p, "entry.text")}",
        _ => p.GetRawText(),
    };

    private static string ConsoleArgs(JsonElement p)
    {
        if (!p.TryGetProperty("args", out var argsEl) || argsEl.ValueKind != JsonValueKind.Array)
            return "";

        var parts = new List<string>();
        foreach (var arg in argsEl.EnumerateArray())
        {
            if (arg.TryGetProperty("value", out var v)) parts.Add(Json.Display(v));
            else if (arg.TryGetProperty("description", out var d)) parts.Add(d.GetString() ?? "");
            else if (arg.TryGetProperty("type", out var t)) parts.Add(t.GetString() ?? "");
        }
        return string.Join(" ", parts);
    }

    public static async Task<int> NetAsync(DaemonClient client, ArgParser args)
    {
        var since = args.GetLong("since", 0);
        var body = await client.EventsAsync(since, NetFilter);

        if (args.Has("json"))
        {
            Console.WriteLine(Json.Pretty(body));
            return 0;
        }

        using var doc = JsonDocument.Parse(body);
        foreach (var evt in doc.RootElement.GetProperty("events").EnumerateArray())
        {
            var seq = evt.GetProperty("seq").GetInt64();
            var p = evt.GetProperty("params");
            var line = evt.GetProperty("method").GetString() switch
            {
                "Network.requestWillBeSent" =>
                    $"→ {Json.PathString(p, "request.method"),-6} {Json.PathString(p, "request.url")}",
                "Network.responseReceived" =>
                    $"← {Json.PathString(p, "response.status"),-6} {Json.PathString(p, "response.url")}",
                "Network.loadingFailed" =>
                    $"✗ {"FAIL",-6} {Json.PathString(p, "errorText")}",
                _ => p.GetRawText(),
            };
            Console.WriteLine($"[{seq}] {line}");
        }

        Console.Error.WriteLine($"-- nextSeq={doc.RootElement.GetProperty("nextSeq").GetInt64()}");
        return 0;
    }

    public static async Task<int> RawCdpAsync(DaemonClient client, ArgParser args)
    {
        var method = args.Required(0, "Domain.method");
        var paramsJson = args.Get("params") ?? (args.Positional.Count > 1 ? args.Positional[1] : null);

        if (paramsJson is not null && !Json.IsValid(paramsJson))
            throw new InvalidOperationException($"--params is not valid JSON: {paramsJson}");

        var result = await client.CdpAsync(method, paramsJson, args.GetInt("timeout", 30) * 1000);
        Console.WriteLine(Json.Pretty(result));
        return 0;
    }

    public static async Task<int> StopAsync(DaemonClient client)
    {
        await client.ShutdownAsync();
        Console.WriteLine("daemon stopped");
        return 0;
    }
}
