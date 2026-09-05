using System.Text.Json;

namespace OpenTui.Blazor.Keymap;

public sealed record BindingSpec(BindingKey Key, KeyEventType Event = KeyEventType.Press,
    bool PreventDefault = true, bool Fallthrough = false,
    IReadOnlyDictionary<string, JsonElement>? Attributes = null);

public abstract record BindingValue
{
    private BindingValue() { }
    public sealed record Disabled : BindingValue;
    public sealed record Items(IReadOnlyList<BindingSpec> Bindings) : BindingValue;

    /// <summary>Decodes the source union explicitly; does not coerce true, null, numbers or invalid modifiers.</summary>
    public static BindingValue Decode(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.False || value.ValueKind == JsonValueKind.String && value.GetString() == "none")
            return new Disabled();
        if (value.ValueKind == JsonValueKind.Array) return new Items(value.EnumerateArray().Select(DecodeItem).ToArray());
        return new Items([DecodeItem(value)]);
    }

    private static BindingSpec DecodeItem(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String) return new(new BindingKey.Text(value.GetString()!));
        if (value.ValueKind != JsonValueKind.Object) throw new FormatException("Expected a key string, stroke object or binding object.");
        if (!value.TryGetProperty("key", out var key)) return new(new BindingKey.Stroke(DecodeStroke(value)));
        var eventType = KeyEventType.Press;
        if (value.TryGetProperty("event", out var eventValue))
            eventType = eventValue.ValueKind == JsonValueKind.String ? eventValue.GetString() switch
            {
                "press" => KeyEventType.Press,
                "release" => KeyEventType.Release,
                _ => throw new FormatException("Binding event must be 'press' or 'release'.")
            } : throw new FormatException("Binding event must be a string.");
        return new(key.ValueKind == JsonValueKind.String ? new BindingKey.Text(key.GetString()!) : new BindingKey.Stroke(DecodeStroke(key)),
            eventType, Boolean(value, "preventDefault", true), Boolean(value, "fallthrough"),
            value.EnumerateObject().Where(property => property.Name is not ("key" or "event" or "preventDefault" or "fallthrough" or "cmd"))
                .ToDictionary(property => property.Name, property => property.Value.Clone()));
    }

    private static KeyStroke DecodeStroke(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String)
            throw new FormatException("A stroke requires a string 'name'.");
        return new(name.GetString()!, Boolean(value, "ctrl"), Boolean(value, "shift"), Boolean(value, "meta"),
            Boolean(value, "super"), Boolean(value, "hyper"));
    }

    private static bool Boolean(JsonElement value, string name, bool fallback = false) => !value.TryGetProperty(name, out var property)
        ? fallback : property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new FormatException($"'{name}' must be a boolean.")
        };
}
