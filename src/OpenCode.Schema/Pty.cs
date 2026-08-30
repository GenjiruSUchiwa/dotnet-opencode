namespace OpenCode.Schema;

using System.Text.Json;
using System.Text.Json.Serialization;

[JsonConverter(typeof(PtyIdJsonConverter))]
public readonly record struct PtyId : IEquatable<PtyId>
{
    public const string Prefix = "pty_";
    public string Value { get; }

    public PtyId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
    }

    public static PtyId Create() => new($"{Prefix}{Identifier.Ascending()}");
    public override string ToString() => Value;
    public static implicit operator string(PtyId id) => id.Value;
    public static explicit operator PtyId(string value) => new(value);
}

public sealed class PtyIdJsonConverter : JsonConverter<PtyId>
{
    public override PtyId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(reader.GetString()!);

    public override void Write(Utf8JsonWriter writer, PtyId value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value);
}

[JsonConverter(typeof(JsonStringEnumConverter<PtyStatus>))]
public enum PtyStatus
{
    [JsonStringEnumMemberName("running")]
    Running,
    [JsonStringEnumMemberName("exited")]
    Exited
}

/// <summary>
/// 1:1 port of Pty.Info from packages/schema/src/pty.ts
/// </summary>
public sealed record PtyInfo(
    [property: JsonPropertyName("id")] PtyId Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("command")] string Command,
    [property: JsonPropertyName("args")] IReadOnlyList<string> Args,
    [property: JsonPropertyName("cwd")] string Cwd,
    [property: JsonPropertyName("status")] PtyStatus Status,
    [property: JsonPropertyName("pid")] int Pid,
    [property: JsonPropertyName("exitCode")] int? ExitCode = null
);
