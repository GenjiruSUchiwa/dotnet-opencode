namespace OpenCode.Schema;

using System.Text.Json;
using System.Text.Json.Serialization;

[JsonConverter(typeof(WorkspaceIdJsonConverter))]
[Vogen.ValueObject<string>(comparison: Vogen.ComparisonGeneration.Omit)]
public readonly partial struct WorkspaceId
{
    public const string Prefix = "wrk_";
    public static WorkspaceId FromExisting(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return From(value);
    }

    public static WorkspaceId Create() => FromExisting($"{Prefix}{Identifier.Ascending()}");
    private static Vogen.Validation Validate(string value) => !string.IsNullOrWhiteSpace(value)
        ? Vogen.Validation.Ok : Vogen.Validation.Invalid("WorkspaceId cannot be empty or whitespace.");
    public override string ToString() => Value;
    public static implicit operator string(WorkspaceId id) => id.Value;
    public static explicit operator WorkspaceId(string value) => FromExisting(value);
}

public sealed class WorkspaceIdJsonConverter() : ScalarJsonConverter<WorkspaceId, string>(WorkspaceId.FromExisting, static value => value.Value)
{
    public override WorkspaceId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        WorkspaceId.FromExisting(reader.GetString()!);

    public override void Write(Utf8JsonWriter writer, WorkspaceId value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value);
}

/// <summary>
/// 1:1 port of Workspace from packages/schema/src/workspace.ts
/// </summary>
public sealed record WorkspaceDestroyResult(
    [property: JsonPropertyName("destroyed"), JsonRequired] bool Destroyed
);
