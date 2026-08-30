namespace OpenCode.Schema;

using System.Text.Json;
using System.Text.Json.Serialization;

[JsonConverter(typeof(ShellIdJsonConverter))]
public readonly record struct ShellId : IEquatable<ShellId>
{
    public const string Prefix = "sh_";
    public string Value { get; }

    public ShellId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
    }

    public static ShellId Create() => new($"{Prefix}{Identifier.Ascending()}");
    public override string ToString() => Value;
    public static implicit operator string(ShellId id) => id.Value;
    public static explicit operator ShellId(string value) => new(value);
}

public sealed class ShellIdJsonConverter : JsonConverter<ShellId>
{
    public override ShellId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(reader.GetString()!);

    public override void Write(Utf8JsonWriter writer, ShellId value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value);
}

[JsonConverter(typeof(JsonStringEnumConverter<ShellStatus>))]
public enum ShellStatus
{
    [JsonStringEnumMemberName("running")]
    Running,
    [JsonStringEnumMemberName("exited")]
    Exited,
    [JsonStringEnumMemberName("timeout")]
    Timeout,
    [JsonStringEnumMemberName("killed")]
    Killed
}

public sealed record ShellTime(
    [property: JsonPropertyName("started")] long Started,
    [property: JsonPropertyName("completed")] long? Completed = null
);

/// <summary>
/// 1:1 port of Shell.Info from packages/schema/src/shell.ts
/// </summary>
public sealed record ShellInfo(
    [property: JsonPropertyName("id")] ShellId Id,
    [property: JsonPropertyName("status")] ShellStatus Status,
    [property: JsonPropertyName("command")] string Command,
    [property: JsonPropertyName("cwd")] string Cwd,
    [property: JsonPropertyName("shell")] string Shell,
    [property: JsonPropertyName("file")] string File,
    [property: JsonPropertyName("time")] ShellTime Time,
    [property: JsonPropertyName("metadata")] IReadOnlyDictionary<string, JsonElement> Metadata,
    [property: JsonPropertyName("pid")] int? Pid = null,
    [property: JsonPropertyName("exit")] int? Exit = null
);
