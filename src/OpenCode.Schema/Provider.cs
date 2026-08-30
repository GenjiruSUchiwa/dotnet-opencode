namespace OpenCode.Schema;

using System.Text.Json;
using System.Text.Json.Serialization;

[JsonConverter(typeof(ProviderIdJsonConverter))]
public readonly record struct ProviderId(string Value) : IEquatable<ProviderId>
{
    public override string ToString() => Value;
    public static implicit operator string(ProviderId id) => id.Value;
    public static explicit operator ProviderId(string value) => new(value);
}

public sealed class ProviderIdJsonConverter : JsonConverter<ProviderId>
{
    public override ProviderId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(reader.GetString()!);

    public override void Write(Utf8JsonWriter writer, ProviderId value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value);
}

[JsonConverter(typeof(JsonStringEnumConverter<ProviderActivation>))]
public enum ProviderActivation
{
    [JsonStringEnumMemberName("auto")]
    Auto,
    [JsonStringEnumMemberName("enabled")]
    Enabled,
    [JsonStringEnumMemberName("disabled")]
    Disabled
}

public sealed record ProviderInfo(
    string Id,
    string Name,
    ProviderActivation Activation,
    string Package,
    string? IntegrationId = null,
    IReadOnlyDictionary<string, object>? Settings = null,
    IReadOnlyDictionary<string, string>? Headers = null,
    IReadOnlyDictionary<string, object>? Body = null
);
