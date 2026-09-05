namespace OpenCode.Cli.Tui.Settings;

using System.Globalization;
using System.Text.Json;

/// <summary>Typed metadata for settings implemented by a registered runtime consumer.</summary>
public sealed class CliSetting<T>
{
    public string Id { get; }
    public string Title { get; }
    public string Category { get; }
    public string Keywords { get; }
    public T Default { get; }
    internal Func<JsonElement, T> Decode { get; }
    internal Func<T, JsonElement> Encode { get; }
    internal Func<T, string> Format { get; }
    internal IReadOnlyList<T>? Choices { get; }
    internal Func<T, int, T>? Increment { get; }

    internal CliSetting(string id, string title, string category, string keywords, T fallback,
        Func<JsonElement, T> decode, Func<T, JsonElement> encode, Func<T, string> format,
        IReadOnlyList<T>? choices = null, Func<T, int, T>? increment = null)
    {
        Id = id; Title = title; Category = category; Keywords = keywords; Default = fallback;
        Decode = decode; Encode = encode; Format = format; Choices = choices; Increment = increment;
    }
}

/// <summary>Source dialog-config.tsx entries supported by the current native consumer contracts.</summary>
public static class CliSettings
{
    public static CliSetting<string> ThemeName { get; } = Choice("theme.name", "Theme", "Appearance", "opencode", [], "color scheme colors");
    public static CliSetting<string> ColorMode { get; } = Choice("theme.mode", "Color mode", "Appearance", "system", ["system", "dark", "light"], "dark mode light mode system theme");
    public static CliSetting<bool> Mouse { get; } = Toggle("mouse", "Mouse", "Input", true, "mouse capture");
    public static CliSetting<double> ScrollSpeed { get; } = Number("scroll.speed", "Scroll speed", "Input", 3, .25, .25, 10,
        value => value.ToString("F2", CultureInfo.InvariantCulture), "scrolling", value => value >= .001);
    public static CliSetting<double> LeaderTimeout { get; } = Number("leader.timeout", "Leader timeout", "Input", 2000, 250, 250, 10000,
        value => value.ToString(CultureInfo.InvariantCulture) + " ms", "leader key shortcut timeout", value => value > 0 && value == Math.Truncate(value));
    public static CliSetting<string> Thinking { get; } = Choice("session.thinking", "Thinking", "Session", "hide", ["hide", "show"], "reasoning chain of thought");
    public static CliSetting<string> Markdown { get; } = Choice("session.markdown", "Markdown", "Session", "rendered", ["source", "rendered"], "syntax concealment rendering");
    public static CliSetting<string> Grouping { get; } = Choice("session.grouping", "Tool grouping", "Session", "auto", ["none", "auto"], "transcript messages reads searches");
    public static CliSetting<string> Sidebar { get; } = Choice("session.sidebar", "Sidebar", "Session", "auto", ["hide", "auto"], "side panel");
    public static CliSetting<string> DiffView { get; } = Choice("diffs.view", "Layout", "Diffs", "auto", ["auto", "split", "unified"], "diff layout split diff unified diff");
    public static CliSetting<string> DiffWrap { get; } = Choice("diffs.wrap", "Wrapping", "Diffs", "word", ["none", "word"], "diff wrap word wrap line wrap");

    private static CliSetting<bool> Toggle(string id, string title, string category, bool fallback, string keywords) =>
        new(id, title, category, keywords, fallback,
            value => value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : throw Invalid(id, "a boolean"),
            value => JsonSerializer.SerializeToElement(value), value => value ? "on" : "off", [false, true]);

    private static CliSetting<string> Choice(string id, string title, string category, string fallback, IReadOnlyList<string> choices, string keywords) =>
        new(id, title, category, keywords, fallback,
            value => value.ValueKind == JsonValueKind.String && value.GetString() is { } text &&
                (choices.Count == 0 || choices.Contains(text)) ? text : throw Invalid(id, "a supported string value"),
            value => JsonSerializer.SerializeToElement(value), value => value, choices);

    private static CliSetting<double> Number(string id, string title, string category, double fallback, double step, double min, double max,
        Func<double, string> format, string keywords, Func<double, bool> validate) =>
        new(id, title, category, keywords, fallback,
            value => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number) && validate(number)
                ? number : throw Invalid(id, "a valid finite number"),
            value => JsonSerializer.SerializeToElement(value), format,
            increment: (value, direction) => Math.Clamp(value + direction * step, min, max));

    private static JsonException Invalid(string id, string expected) => new($"CLI setting '{id}' requires {expected}.");
}

internal abstract class SettingRegistration
{
    public abstract string Id { get; }
    public abstract string Title { get; }
    public abstract string Category { get; }
    public abstract string Keywords { get; }
    public abstract bool Available { get; }
    public abstract JsonElement Value(JsonElement config);
    public abstract string Display(JsonElement config);
    public abstract JsonElement Next(JsonElement config, int direction);
    public abstract JsonElement DefaultValue { get; }
    public abstract void Validate(JsonElement value, JsonElement config);
    public abstract void Apply(JsonElement value, JsonElement config);
}

internal sealed class SettingRegistration<T>(CliSetting<T> definition, Action<T, JsonElement> apply, Func<bool>? available,
    Func<IReadOnlyList<T>>? choices, Func<T>? fallback, Action<T, JsonElement>? validate) : SettingRegistration
{
    public override string Id => definition.Id;
    public override string Title => definition.Title;
    public override string Category => definition.Category;
    public override string Keywords => definition.Keywords;
    public override bool Available => available?.Invoke() ?? true;
    public override JsonElement DefaultValue => definition.Encode(definition.Default);
    private JsonElement FallbackValue => definition.Encode(fallback is null ? definition.Default : fallback());
    public override JsonElement Value(JsonElement config)
    {
        foreach (var part in Id.Split('.'))
        {
            if (config.ValueKind != JsonValueKind.Object || !config.TryGetProperty(part, out var child)) return FallbackValue;
            config = child;
        }
        return config.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? FallbackValue : definition.Encode(definition.Decode(config));
    }
    public override string Display(JsonElement config) => definition.Format(definition.Decode(Value(config)));
    public override JsonElement Next(JsonElement config, int direction)
    {
        var current = definition.Decode(Value(config));
        var values = choices?.Invoke() ?? definition.Choices;
        if (values is { Count: > 0 })
        {
            var index = values.ToList().IndexOf(current);
            return definition.Encode(values[(index + direction + values.Count) % values.Count]);
        }
        if (definition.Increment is null) throw new InvalidOperationException($"Setting '{Id}' has no available values.");
        return definition.Encode(definition.Increment(current, direction));
    }
    public override void Validate(JsonElement value, JsonElement config)
    {
        var decoded = definition.Decode(value);
        if (choices?.Invoke() is { } values && !values.Contains(decoded)) throw new InvalidOperationException($"The selected value for '{Id}' is no longer available.");
        validate?.Invoke(decoded, config);
    }
    public override void Apply(JsonElement value, JsonElement config) => apply(definition.Decode(value), config);
}
