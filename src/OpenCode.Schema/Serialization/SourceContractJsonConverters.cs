namespace OpenCode.Schema;

using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>Source literal enums are case-sensitive strings, never integer enum values.</summary>
public sealed class SourceStringEnumJsonConverter<T> : JsonConverter<T> where T : struct, Enum
{
    private static readonly IReadOnlyDictionary<T, string> Names = Enum.GetValues<T>().ToDictionary(value => value,
        value => typeof(T).GetField(value.ToString())!.GetCustomAttribute<JsonStringEnumMemberNameAttribute>()?.Name
            ?? throw new InvalidOperationException($"Missing wire name for {typeof(T).Name}."));

    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String) throw new JsonException($"Expected {typeof(T).Name} string.");
        foreach (var pair in Names)
            if (string.Equals(pair.Value, reader.GetString(), StringComparison.Ordinal)) return pair.Key;
        throw new JsonException($"Unknown {typeof(T).Name} literal.");
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
        writer.WriteStringValue(Names.TryGetValue(value, out var name) ? name : throw new JsonException($"Unknown {typeof(T).Name} value."));
}

// Retain the established converter family names so the existing native schema
// exporter recognizes the integer/minimum contract without new caller mappings.
public sealed class NonNegativeIntegerJsonConverter<T> : JsonConverter<T> where T : struct, IBinaryInteger<T>
{
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        IntegerWire<T>.Read(ref reader, 0);
    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) => IntegerWire<T>.Write(writer, value, 0);
}
public sealed class PositiveIntegerJsonConverter<T> : JsonConverter<T> where T : struct, IBinaryInteger<T>
{
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        IntegerWire<T>.Read(ref reader, 1);
    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) => IntegerWire<T>.Write(writer, value, 1);
}
public sealed class OptionalPositiveIntegerJsonConverter<T> : JsonConverter<T?> where T : struct, IBinaryInteger<T>
{
    public override bool HandleNull => true;
    public override T? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => IntegerWire<T>.Read(ref reader, 1);
    public override void Write(Utf8JsonWriter writer, T? value, JsonSerializerOptions options) =>
        IntegerWire<T>.Write(writer, value ?? throw new JsonException("Optional integer must be omitted, not null."), 1);
}

internal static class IntegerWire<T> where T : struct, IBinaryInteger<T>
{
    internal static T Read(ref Utf8JsonReader reader, int minimum)
    {
        var number = FiniteNumberJsonConverter.ReadValue(ref reader);
        if (number < minimum || Math.Truncate(number) != number) throw new JsonException($"Expected an integer >= {minimum}.");
        try { return T.CreateChecked(number); }
        catch (OverflowException error) { throw new JsonException($"Integer exceeds native {typeof(T).Name} representation.", error); }
    }
    internal static void Write(Utf8JsonWriter writer, T value, int minimum)
    {
        if (value < T.CreateChecked(minimum)) throw new JsonException($"Expected an integer >= {minimum}.");
        writer.WriteRawValue(value.ToString(null, CultureInfo.InvariantCulture));
    }
}

internal static class SourceObjectContract
{
    internal static void Required(params object?[] values)
    {
        if (values.Any(value => value is null)) throw new JsonException("Required source property cannot be null.");
    }
}
