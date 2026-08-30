namespace OpenCode.Schema;

using System.Text.Json;
using System.Text.Json.Serialization;

[JsonConverter(typeof(SessionIdJsonConverter))]
public readonly record struct SessionId : IEquatable<SessionId>, IComparable<SessionId>
{
    public const string Prefix = "ses_";
    public string Value { get; }

    public SessionId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (!value.StartsWith(Prefix, StringComparison.Ordinal))
        {
            throw new ArgumentException($"SessionId must start with '{Prefix}', got '{value}'", nameof(value));
        }
        Value = value;
    }

    public static SessionId Create() => new($"{Prefix}{Identifier.Ascending()}");
    public static SessionId FromExisting(string value) => new(value);

    public int CompareTo(SessionId other) => string.CompareOrdinal(Value, other.Value);
    public override string ToString() => Value;

    public static implicit operator string(SessionId id) => id.Value;
    public static explicit operator SessionId(string value) => new(value);
}

public sealed class SessionIdJsonConverter : JsonConverter<SessionId>
{
    public override SessionId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(reader.GetString()!);

    public override void Write(Utf8JsonWriter writer, SessionId value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value);
}

[JsonConverter(typeof(ProjectIdJsonConverter))]
public readonly record struct ProjectId : IEquatable<ProjectId>
{
    public const string Prefix = "prj_";
    public string Value { get; }

    public ProjectId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
    }

    public static ProjectId Create() => new($"{Prefix}{Identifier.Ascending()}");
    public static ProjectId FromExisting(string value) => new(value);

    public override string ToString() => Value;
    public static implicit operator string(ProjectId id) => id.Value;
    public static explicit operator ProjectId(string value) => new(value);
}

public sealed class ProjectIdJsonConverter : JsonConverter<ProjectId>
{
    public override ProjectId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(reader.GetString()!);

    public override void Write(Utf8JsonWriter writer, ProjectId value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value);
}

[JsonConverter(typeof(MessageIdJsonConverter))]
public readonly record struct MessageId : IEquatable<MessageId>
{
    public const string Prefix = "msg_";
    public string Value { get; }

    public MessageId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
    }

    public static MessageId Create() => new($"{Prefix}{Identifier.Ascending()}");
    public static MessageId FromExisting(string value) => new(value);

    public override string ToString() => Value;
    public static implicit operator string(MessageId id) => id.Value;
    public static explicit operator MessageId(string value) => new(value);
}

public sealed class MessageIdJsonConverter : JsonConverter<MessageId>
{
    public override MessageId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(reader.GetString()!);

    public override void Write(Utf8JsonWriter writer, MessageId value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value);
}
