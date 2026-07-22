namespace TabCtl.Commands;

/// <summary>Minimal <c>--key value</c> / <c>--flag</c> / positional splitter.</summary>
public sealed class ArgParser
{
    private readonly Dictionary<string, string> _options = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Positional { get; } = [];

    public ArgParser(IEnumerable<string> args)
    {
        var list = args.ToList();
        for (var i = 0; i < list.Count; i++)
        {
            var arg = list[i];
            if (arg.Length > 1 && arg[0] == '-' && !char.IsDigit(arg[1]))
            {
                var key = arg.TrimStart('-');
                var hasValue = i + 1 < list.Count && !(list[i + 1].Length > 1 && list[i + 1][0] == '-' && !char.IsDigit(list[i + 1][1]));
                _options[key] = hasValue ? list[++i] : "true";
            }
            else
            {
                Positional.Add(arg);
            }
        }
    }

    public bool Has(string key) => _options.ContainsKey(key);

    public string? Get(string key, string? fallback = null) =>
        _options.TryGetValue(key, out var v) ? v : fallback;

    public int GetInt(string key, int fallback) =>
        _options.TryGetValue(key, out var v) && int.TryParse(v, out var n) ? n : fallback;

    public long GetLong(string key, long fallback) =>
        _options.TryGetValue(key, out var v) && long.TryParse(v, out var n) ? n : fallback;

    /// <summary>Positional at <paramref name="index"/>, or a usage error naming what was expected.</summary>
    public string Required(int index, string name) =>
        index < Positional.Count
            ? Positional[index]
            : throw new InvalidOperationException($"missing required argument <{name}>");
}
