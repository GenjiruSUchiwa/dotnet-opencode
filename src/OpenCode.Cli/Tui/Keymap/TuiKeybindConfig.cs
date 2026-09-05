using System.Text.Json;
using System.Text.RegularExpressions;
using OpenTui.Blazor.Keymap;

namespace OpenCode.Cli.Tui.Keymap;

/// <summary>OpenCode cli.json keybind shapes and defaults, separate from reusable OpenTUI dispatch.</summary>
public sealed class TuiKeybindConfig
{
    public const string LeaderDefault = "ctrl+x";
    private readonly IReadOnlyDictionary<string, BindingValue> _values;
    public KeyStroke? Leader { get; }
    public TimeSpan LeaderTimeout { get; }
    public KeySequenceParser Parser { get; }

    private TuiKeybindConfig(IReadOnlyDictionary<string, BindingValue> values, TimeSpan timeout)
    {
        _values = values;
        LeaderTimeout = timeout;
        var leader = values["leader"] is BindingValue.Items items ? items.Bindings.FirstOrDefault() : null;
        var parser = new KeySequenceParser(commaBindings: true, expand: ExpandAliases);
        if (leader is not null)
        {
            var keys = parser.Parse(leader.Key);
            if (keys.Count != 1 || keys[0].Count != 1) throw new FormatException("The leader trigger must contain exactly one stroke.");
            Leader = keys[0][0].Stroke;
        }
        Parser = new(Leader is null ? null : new Dictionary<string, KeyStroke> { ["leader"] = Leader }, true, ExpandAliases);
        // Decode validates shapes; compile now validates every configured key, even in an unmounted layer.
        foreach (var id in values.Keys.Where(id => id != "leader")) _ = Get(id);
    }

    public static TuiKeybindConfig Defaults() => Parse();

    /// <summary>Accepts only the keybinds object. Unknown command IDs fail, as in TuiKeybind.parse.</summary>
    public static TuiKeybindConfig Parse(JsonElement? keybinds = null, TimeSpan? leaderTimeout = null)
    {
        if (keybinds is { ValueKind: not JsonValueKind.Object }) throw new FormatException("keybinds must be an object.");
        var definitions = DefaultBindings.All.ToDictionary(item => item.Id);
        var overrides = keybinds?.EnumerateObject().ToDictionary(property => property.Name, property => property.Value)
            ?? new Dictionary<string, JsonElement>();
        var unknown = overrides.Keys.Where(key => !definitions.ContainsKey(key)).ToArray();
        if (unknown.Length > 0) throw new FormatException($"Unrecognized keybinds: {string.Join(", ", unknown)}");
        if (leaderTimeout is { } timeout && timeout < TimeSpan.Zero) throw new FormatException("Leader timeout cannot be negative.");
        var values = definitions.ToDictionary(pair => pair.Key, pair =>
        {
            if (overrides.TryGetValue(pair.Key, out var value)) return BindingValue.Decode(value);
            // The pinned source lists Alt+Shift word selection, but omits the conventional
            // Ctrl+Shift arrows despite supplying Ctrl+Arrow movement. Add aliases at the
            // default-config boundary so explicit remapping/disable still takes precedence.
            var key = pair.Key switch
            {
                "input.select.word.forward" => pair.Value.Key + ",ctrl+shift+right",
                "input.select.word.backward" => pair.Value.Key + ",ctrl+shift+left",
                _ => pair.Value.Key
            };
            return key == "none" ? (BindingValue)new BindingValue.Disabled()
                : new BindingValue.Items([new(new BindingKey.Text(key), PreventDefault: pair.Value.PreventDefault)]);
        });
        return new(values, leaderTimeout ?? TimeSpan.FromMilliseconds(2000));
    }

    /// <summary>Reads keybinds and leader.timeout; the shipped leader_timeout fallback remains supported.</summary>
    public static TuiKeybindConfig FromCliConfig(JsonElement config)
    {
        if (config.ValueKind != JsonValueKind.Object) throw new FormatException("CLI config must be an object.");
        JsonElement? timeout = null;
        if (config.TryGetProperty("leader", out var leader))
        {
            if (leader.ValueKind != JsonValueKind.Object) throw new FormatException("leader must be an object.");
            if (leader.TryGetProperty("timeout", out var nested)) timeout = nested;
        }
        if (timeout is null && config.TryGetProperty("leader_timeout", out var legacy)) timeout = legacy;
        if (timeout is { } duration && (duration.ValueKind != JsonValueKind.Number || !duration.TryGetDouble(out var number)
            || !double.IsFinite(number) || number < 0 || number > TimeSpan.MaxValue.TotalMilliseconds))
            throw new FormatException("Leader timeout must be a nonnegative finite number of milliseconds.");
        return Parse(config.TryGetProperty("keybinds", out var keybinds) ? keybinds : null,
            timeout is { } milliseconds ? TimeSpan.FromMilliseconds(milliseconds.GetDouble()) : null);
    }

    public bool IsDisabled(string id) => _values.TryGetValue(id, out var value)
        && (value is BindingValue.Disabled || value is BindingValue.Items { Bindings.Count: 0 });

    public IReadOnlyList<KeymapBinding> Get(string id)
    {
        if (!_values.TryGetValue(id, out var value) || value is not BindingValue.Items items) return [];
        return items.Bindings.SelectMany(binding => binding.Key is BindingKey.Text text && text.Value.Contains(',')
                ? text.Value.Split(',').Select(part => binding with { Key = new BindingKey.Text(part.Trim()) }) : [binding])
            .Where(binding => Leader is not null || binding.Key is not BindingKey.Text text
                || !Regex.IsMatch(text.Value, @"<\s*leader\s*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking))
            .SelectMany(binding => KeymapBinding.Compile(id, binding, Parser))
            .Select(binding => binding with { Description = binding.Description ?? DefaultBindings.All.First(item => item.Id == id).Description })
            .ToArray();
    }

    public KeymapDispatcher CreateDispatcher() => new(new()
    {
        TimedLeader = Leader, LeaderTimeout = LeaderTimeout, BaseLayoutFallback = true,
        EscapeClearsPending = true, BackspacePopsPending = true
    });

#pragma warning disable MA0009 // Fixed-width alias alternatives plus one boundary lookahead: linear scan, no unbounded backtracking. Preserve source lookahead semantics.
    private static string ExpandAliases(string input) => Regex.Replace(input,
        @"(?<separator>^|[+,\s>])(?<alias>enter|esc|pgdown|pgup)(?=$|[+,\s<])",
        match => match.Groups["separator"].Value + (match.Groups["alias"].Value.ToLowerInvariant() switch
        {
            "enter" => "return", "esc" => "escape", "pgdown" => "pagedown", _ => "pageup"
        }), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
#pragma warning restore MA0009
}
