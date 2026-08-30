namespace OpenCode.Schema;

using System.Text.Json;
using System.Text.Json.Serialization;

[JsonConverter(typeof(WorkspaceIdJsonConverter))]
public readonly record struct WorkspaceId : IEquatable<WorkspaceId>
{
    public const string Prefix = "wrk_";
    public string Value { get; }

    public WorkspaceId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
    }

    public static WorkspaceId Create() => new($"{Prefix}{Identifier.Ascending()}");
    public override string ToString() => Value;
    public static implicit operator string(WorkspaceId id) => id.Value;
    public static explicit operator WorkspaceId(string value) => new(value);
}

public sealed class WorkspaceIdJsonConverter : JsonConverter<WorkspaceId>
{
    public override WorkspaceId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(reader.GetString()!);

    public override void Write(Utf8JsonWriter writer, WorkspaceId value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value);
}

/// <summary>
/// 1:1 port of Workspace from packages/schema/src/workspace.ts
/// </summary>
public sealed record WorkspaceDestroyResult(
    [property: JsonPropertyName("destroyed")] bool Destroyed
);
