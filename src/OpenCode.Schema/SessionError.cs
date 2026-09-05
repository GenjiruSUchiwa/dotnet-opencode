namespace OpenCode.Schema;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// Structured error with an optional HTTP status in the upstream 100-599 range.
/// </summary>
public sealed record SessionStructuredError(
    [property: JsonPropertyName("type"), JsonRequired] string Type,
    [property: JsonPropertyName("message"), JsonRequired] string Message,
    [property: JsonPropertyName("status"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(OptionalHttpStatusJsonConverter))] int? Status = null
) : IJsonOnSerializing, IJsonOnDeserialized
{
    void IJsonOnSerializing.OnSerializing() => Validate();
    void IJsonOnDeserialized.OnDeserialized() => Validate();

    private void Validate()
    {
        if (Type is null || Message is null) throw new JsonException("Structured error requires type and message.");
        if (Status is < 100 or > 599) throw new JsonException("HTTP status must be between 100 and 599.");
    }
}
