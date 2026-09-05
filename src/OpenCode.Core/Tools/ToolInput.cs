namespace OpenCode.Core.Tools;

using System.Text.Json;

internal readonly struct ToolInput
{
    private readonly JsonElement _value;

    public ToolInput(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new ToolExecutionException("Tool input must be an object.");
        _value = value;
    }

    public string String(string name) => OptionalString(name) ?? throw new ToolExecutionException($"{name} is required.");

    public string? OptionalString(string name)
    {
        if (!_value.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind != JsonValueKind.String) throw new ToolExecutionException($"{name} must be a string.");
        return value.GetString()!;
    }

    public int Integer(string name, int fallback, int minimum, int maximum = int.MaxValue)
    {
        if (!_value.TryGetProperty(name, out var value)) return fallback;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number) || number < minimum || number > maximum)
            throw new ToolExecutionException($"{name} must be an integer between {minimum} and {maximum}.");
        return number;
    }

    public bool Boolean(string name, bool fallback = false)
    {
        if (!_value.TryGetProperty(name, out var value)) return fallback;
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new ToolExecutionException($"{name} must be a boolean.");
        return value.GetBoolean();
    }
}
