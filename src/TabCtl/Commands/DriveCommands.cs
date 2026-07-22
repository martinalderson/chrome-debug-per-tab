using System.Text;
using System.Text.Json;
using TabCtl.Client;

namespace TabCtl.Commands;

/// <summary>Commands that act on the armed tab.</summary>
public static class DriveCommands
{
    public static async Task<int> EvalAsync(DaemonClient client, ArgParser args)
    {
        var expression = args.Required(0, "js");
        var result = await EvaluateAsync(client, expression);

        using var doc = JsonDocument.Parse(result);
        var root = doc.RootElement;

        if (root.TryGetProperty("exceptionDetails", out var ex))
        {
            var text = Json.PathString(ex, "exception.description")
                       ?? Json.PathString(ex, "text")
                       ?? ex.GetRawText();
            await Console.Error.WriteLineAsync($"Uncaught: {text}");
            return 1;
        }

        if (args.Has("json"))
        {
            Console.WriteLine(Json.Pretty(result));
            return 0;
        }

        if (Json.Path(root, "result.value") is { } value)
        {
            Console.WriteLine(Json.Display(value));
            return 0;
        }

        // No value: undefined, or an object that wouldn't serialise by value.
        Console.WriteLine(Json.PathString(root, "result.description")
                          ?? Json.PathString(root, "result.type")
                          ?? "undefined");
        return 0;
    }

    public static async Task<int> TextAsync(DaemonClient client, ArgParser args)
    {
        var selector = args.Positional.Count > 0 ? args.Positional[0] : "body";
        var expression =
            $"(() => {{ const el = document.querySelector({Json.Str(selector)}); " +
            "return el ? el.innerText : null; })()";

        var result = await EvaluateAsync(client, expression);
        using var doc = JsonDocument.Parse(result);

        if (Json.Path(doc.RootElement, "result.value") is not { } value || value.ValueKind == JsonValueKind.Null)
        {
            await Console.Error.WriteLineAsync($"No element matched '{selector}'");
            return 1;
        }

        Console.WriteLine(Json.Display(value));
        return 0;
    }

    public static async Task<int> NavigateAsync(DaemonClient client, ArgParser args)
    {
        var url = args.Required(0, "url");
        if (!url.Contains("://", StringComparison.Ordinal)) url = "https://" + url;

        await client.CdpAsync("Page.navigate", $"{{\"url\":{Json.Str(url)}}}");

        var timeout = TimeSpan.FromSeconds(args.GetInt("timeout", 30));
        if (!await WaitForLoadAsync(client, timeout))
        {
            await Console.Error.WriteLineAsync($"Warning: page did not finish loading within {timeout.TotalSeconds:0}s");
        }

        var result = await EvaluateAsync(client, "JSON.stringify({url: location.href, title: document.title})");
        using var doc = JsonDocument.Parse(result);
        Console.WriteLine(Json.Path(doc.RootElement, "result.value") is { } v
            ? Json.Pretty(Json.Display(v))
            : url);
        return 0;
    }

    private static async Task<bool> WaitForLoadAsync(DaemonClient client, TimeSpan timeout)
    {
        // The old document can still report "complete" for a moment after Page.navigate
        // returns, so let the navigation commit before polling.
        await Task.Delay(300);

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var res = await EvaluateAsync(client, "document.readyState");
                using var doc = JsonDocument.Parse(res);
                if (Json.PathString(doc.RootElement, "result.value") == "complete") return true;
            }
            catch (DaemonException)
            {
                // Evaluation fails while the context is being swapped mid-navigation.
            }
            await Task.Delay(200);
        }
        return false;
    }

    public static async Task<int> ClickAsync(DaemonClient client, ArgParser args)
    {
        var selector = args.Required(0, "selector");
        var nodeId = await QuerySelectorAsync(client, selector);
        if (nodeId is null)
        {
            await Console.Error.WriteLineAsync($"No element matched '{selector}'");
            return 1;
        }

        try
        {
            await client.CdpAsync("DOM.scrollIntoViewIfNeeded", $"{{\"nodeId\":{nodeId}}}");
        }
        catch (DaemonException)
        {
            // Not fatal — the element may already be in view.
        }

        var box = await client.CdpAsync("DOM.getBoxModel", $"{{\"nodeId\":{nodeId}}}");
        using var doc = JsonDocument.Parse(box);
        if (Json.Path(doc.RootElement, "model.content") is not { } quad || quad.GetArrayLength() < 8)
        {
            await Console.Error.WriteLineAsync($"'{selector}' has no layout box (hidden or zero-sized?)");
            return 1;
        }

        // content quad is [x1,y1, x2,y2, x3,y3, x4,y4] clockwise from top-left.
        var x = (quad[0].GetDouble() + quad[4].GetDouble()) / 2;
        var y = (quad[1].GetDouble() + quad[5].GetDouble()) / 2;

        // Real Input events rather than element.click(): synthetic DOM clicks are skipped by
        // handlers that check isTrusted, and driving the browser properly is the whole point.
        var at = $"\"x\":{x:0.##},\"y\":{y:0.##}";
        await client.CdpAsync("Input.dispatchMouseEvent", $"{{\"type\":\"mouseMoved\",{at}}}");
        await client.CdpAsync("Input.dispatchMouseEvent", $"{{\"type\":\"mousePressed\",{at},\"button\":\"left\",\"clickCount\":1}}");
        await client.CdpAsync("Input.dispatchMouseEvent", $"{{\"type\":\"mouseReleased\",{at},\"button\":\"left\",\"clickCount\":1}}");

        Console.WriteLine($"clicked {selector} at ({x:0.#}, {y:0.#})");
        return 0;
    }

    private static async Task<long?> QuerySelectorAsync(DaemonClient client, string selector)
    {
        var docResult = await client.CdpAsync("DOM.getDocument", "{\"depth\":0}");
        using var docJson = JsonDocument.Parse(docResult);
        if (Json.Path(docJson.RootElement, "root.nodeId") is not { } rootNode) return null;

        var found = await client.CdpAsync("DOM.querySelector",
            $"{{\"nodeId\":{rootNode.GetInt64()},\"selector\":{Json.Str(selector)}}}");
        using var foundJson = JsonDocument.Parse(found);

        if (!foundJson.RootElement.TryGetProperty("nodeId", out var nodeEl)) return null;
        var nodeId = nodeEl.GetInt64();
        return nodeId == 0 ? null : nodeId;
    }

    public static async Task<int> TypeAsync(DaemonClient client, ArgParser args)
    {
        var text = args.Required(0, "text");
        // insertText goes through the same path as IME/paste input, which is far more
        // reliable across frameworks than synthesising a keydown per character.
        await client.CdpAsync("Input.insertText", $"{{\"text\":{Json.Str(text)}}}");
        Console.WriteLine($"typed {text.Length} chars");
        return 0;
    }

    private static readonly Dictionary<string, (string Key, int Code, string? Text)> Keys =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Enter"] = ("Enter", 13, "\r"),
            ["Tab"] = ("Tab", 9, "\t"),
            ["Escape"] = ("Escape", 27, null),
            ["Backspace"] = ("Backspace", 8, null),
            ["Delete"] = ("Delete", 46, null),
            ["ArrowUp"] = ("ArrowUp", 38, null),
            ["ArrowDown"] = ("ArrowDown", 40, null),
            ["ArrowLeft"] = ("ArrowLeft", 37, null),
            ["ArrowRight"] = ("ArrowRight", 39, null),
            ["Home"] = ("Home", 36, null),
            ["End"] = ("End", 35, null),
            ["PageUp"] = ("PageUp", 33, null),
            ["PageDown"] = ("PageDown", 34, null),
        };

    public static async Task<int> KeyAsync(DaemonClient client, ArgParser args)
    {
        var name = args.Required(0, "key");
        if (!Keys.TryGetValue(name, out var k))
        {
            await Console.Error.WriteLineAsync(
                $"Unknown key '{name}'. Known: {string.Join(", ", Keys.Keys)}");
            return 1;
        }

        var common = $"\"key\":{Json.Str(k.Key)},\"code\":{Json.Str(k.Key)}," +
                     $"\"windowsVirtualKeyCode\":{k.Code},\"nativeVirtualKeyCode\":{k.Code}";
        var text = k.Text is null ? "" : $",\"text\":{Json.Str(k.Text)}";

        await client.CdpAsync("Input.dispatchKeyEvent", $"{{\"type\":\"keyDown\",{common}{text}}}");
        await client.CdpAsync("Input.dispatchKeyEvent", $"{{\"type\":\"keyUp\",{common}}}");

        Console.WriteLine($"pressed {k.Key}");
        return 0;
    }

    public static async Task<int> ScreenshotAsync(DaemonClient client, ArgParser args)
    {
        var output = args.Get("o") ?? args.Get("output") ?? "/tmp/tabctl-screenshot.png";
        var beyond = args.Has("full") ? "true" : "false";

        var result = await client.CdpAsync("Page.captureScreenshot",
            $"{{\"format\":\"png\",\"captureBeyondViewport\":{beyond}}}", timeoutMs: 60000);

        using var doc = JsonDocument.Parse(result);
        if (!doc.RootElement.TryGetProperty("data", out var data))
        {
            await Console.Error.WriteLineAsync("Screenshot returned no data");
            return 1;
        }

        var bytes = Convert.FromBase64String(data.GetString() ?? "");
        var full = Path.GetFullPath(output);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllBytesAsync(full, bytes);

        Console.WriteLine(full);
        return 0;
    }

    private static Task<string> EvaluateAsync(DaemonClient client, string expression)
    {
        var sb = new StringBuilder("{\"expression\":").Append(Json.Str(expression));
        sb.Append(",\"returnByValue\":true,\"awaitPromise\":true,\"userGesture\":true");
        sb.Append('}');
        return client.CdpAsync("Runtime.evaluate", sb.ToString());
    }
}
