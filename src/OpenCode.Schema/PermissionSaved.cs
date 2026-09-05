namespace OpenCode.Schema;

using System.Text.Json;
using System.Text.Json.Serialization;

[JsonConverter(typeof(PermissionSavedIdJsonConverter))]
[Vogen.ValueObject<string>(comparison: Vogen.ComparisonGeneration.Omit)]
public readonly partial struct PermissionSavedId
{
    public const string Prefix = "psv_";
    public static PermissionSavedId FromExisting(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return From(value);
    }

    public static PermissionSavedId Create() => FromExisting($"{Prefix}{Identifier.Ascending()}");
    private static Vogen.Validation Validate(string value) => !string.IsNullOrWhiteSpace(value)
        ? Vogen.Validation.Ok : Vogen.Validation.Invalid("PermissionSavedId cannot be empty or whitespace.");
    public override string ToString() => Value;
    public static implicit operator string(PermissionSavedId id) => id.Value;
    public static explicit operator PermissionSavedId(string value) => FromExisting(value);
}

public sealed class PermissionSavedIdJsonConverter() : ScalarJsonConverter<PermissionSavedId, string>(PermissionSavedId.FromExisting, static value => value.Value)
{
    public override PermissionSavedId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        PermissionSavedId.FromExisting(reader.GetString()!);

    public override void Write(Utf8JsonWriter writer, PermissionSavedId value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value);
}

/// <summary>
/// 1:1 port of PermissionSaved.Info from packages/schema/src/permission-saved.ts
/// </summary>
public sealed record PermissionSavedInfo(
    [property: JsonPropertyName("id"), JsonRequired] PermissionSavedId Id,
    [property: JsonPropertyName("projectID"), JsonRequired] ProjectId ProjectId,
    [property: JsonPropertyName("action"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string Action,
    [property: JsonPropertyName("resource"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string Resource
) : IJsonOnSerializing
{
    void IJsonOnSerializing.OnSerializing() => SourceObjectContract.Required(Action, Resource);
}
