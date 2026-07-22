using System.Text;
using System.Text.Json;

namespace TabCtl;

/// <summary>
/// CDP payloads are opaque to this relay, so they are moved around as raw JSON strings and
/// spliced rather than deserialized into DTOs. That keeps the whole path reflection-free,
/// which is what Native AOT needs, and avoids modelling the (very large) CDP surface.
/// </summary>
public static class Json
{
    /// <summary>Encodes a string as a JSON string literal, quotes included.</summary>
    public static string Str(string? value)
    {
        if (value is null) return "null";

        var sb = new StringBuilder(value.Length + 2);
        sb.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    public static string Bool(bool value) => value ? "true" : "false";

    /// <summary>Re-formats JSON with indentation. Returns the input unchanged if it won't parse.</summary>
    public static string Pretty(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                doc.RootElement.WriteTo(writer);
            }
            return Encoding.UTF8.GetString(stream.ToArray());
        }
        catch (JsonException)
        {
            return json;
        }
    }

    public static bool IsValid(string json)
    {
        try
        {
            using var _ = JsonDocument.Parse(json);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Walks a dotted path (e.g. "result.value") returning null if any hop is missing.</summary>
    public static JsonElement? Path(JsonElement root, string path)
    {
        var current = root;
        foreach (var segment in path.Split('.'))
        {
            if (current.ValueKind != JsonValueKind.Object) return null;
            if (!current.TryGetProperty(segment, out var next)) return null;
            current = next;
        }
        return current;
    }

    public static string? PathString(JsonElement root, string path)
    {
        var el = Path(root, path);
        if (el is null) return null;
        return el.Value.ValueKind == JsonValueKind.String ? el.Value.GetString() : el.Value.GetRawText();
    }

    /// <summary>Renders a value the way a human wants to read it: bare strings, raw JSON otherwise.</summary>
    public static string Display(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString() ?? "",
        JsonValueKind.Null or JsonValueKind.Undefined => "null",
        _ => el.GetRawText(),
    };
}
