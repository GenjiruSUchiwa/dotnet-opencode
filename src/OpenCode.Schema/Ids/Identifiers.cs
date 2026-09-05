namespace OpenCode.Schema;

using System.Text.Json;
using System.Text.Json.Serialization;

[JsonConverter(typeof(SessionIdJsonConverter))]
[Vogen.ValueObject<string>(comparison: Vogen.ComparisonGeneration.Omit)]
public readonly partial struct SessionId : IComparable<SessionId>
{
    public const string Prefix = "ses_";
    public static SessionId FromExisting(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        // Legacy Session IDs accept "ses"; newly generated IDs still use Prefix ("ses_").
        if (!value.StartsWith("ses", StringComparison.Ordinal))
        {
            throw new ArgumentException("SessionId must start with 'ses'.", nameof(value));
        }
        return From(value);
    }

    public static SessionId Create() => FromExisting($"{Prefix}{Identifier.Ascending()}");
    private static Vogen.Validation Validate(string value) => !string.IsNullOrWhiteSpace(value) && value.StartsWith("ses", StringComparison.Ordinal)
        ? Vogen.Validation.Ok : Vogen.Validation.Invalid("SessionId must start with 'ses'.");

    public int CompareTo(SessionId other) => string.CompareOrdinal(Value, other.Value);
    public override string ToString() => Value;

    public static implicit operator string(SessionId id) => id.Value;
    public static explicit operator SessionId(string value) => FromExisting(value);
}

public sealed class SessionIdJsonConverter() : ScalarJsonConverter<SessionId, string>(SessionId.FromExisting, static value => value.Value)
{
    public override SessionId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        SessionId.FromExisting(reader.GetString()!);

    public override void Write(Utf8JsonWriter writer, SessionId value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value);
}

[JsonConverter(typeof(ProjectIdJsonConverter))]
[Vogen.ValueObject<string>(comparison: Vogen.ComparisonGeneration.Omit)]
public readonly partial struct ProjectId
{
    public const string Prefix = "prj_";
    public static ProjectId FromExisting(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return From(value);
    }

    public static ProjectId Create() => FromExisting($"{Prefix}{Identifier.Ascending()}");

    public override string ToString() => Value;
    public static implicit operator string(ProjectId id) => id.Value;
    public static explicit operator ProjectId(string value) => FromExisting(value);
}

public sealed class ProjectIdJsonConverter() : ScalarJsonConverter<ProjectId, string>(ProjectId.FromExisting, static value => value.Value)
{
    public override ProjectId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String) throw new JsonException("Expected project ID string.");
        return ProjectId.FromExisting(reader.GetString()!);
    }

    public override void Write(Utf8JsonWriter writer, ProjectId value, JsonSerializerOptions options) =>
        writer.WriteStringValue(PromptValidation.Required(value.Value));
}

[JsonConverter(typeof(MessageIdJsonConverter))]
[Vogen.ValueObject<string>(comparison: Vogen.ComparisonGeneration.Omit)]
public readonly partial struct MessageId
{
    public const string Prefix = "msg_";
    public static MessageId FromExisting(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return From(value);
    }

    public static MessageId Create() => FromExisting($"{Prefix}{Identifier.Ascending()}");
    private static Vogen.Validation Validate(string value) => !string.IsNullOrWhiteSpace(value)
        ? Vogen.Validation.Ok : Vogen.Validation.Invalid("MessageId cannot be empty or whitespace.");

    public override string ToString() => Value;
    public static implicit operator string(MessageId id) => id.Value;
    public static explicit operator MessageId(string value) => FromExisting(value);
}

public sealed class MessageIdJsonConverter() : ScalarJsonConverter<MessageId, string>(MessageId.FromExisting, static value => value.Value)
{
    public override MessageId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        MessageId.FromExisting(reader.GetString()!);

    public override void Write(Utf8JsonWriter writer, MessageId value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value);
}
