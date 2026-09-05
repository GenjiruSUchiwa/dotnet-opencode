namespace OpenCode.Schema;

using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

internal static class MessageContract
{
    internal static IReadOnlyList<T> NonEmpty<T>(IReadOnlyList<T> value) where T : class
    {
        if (value is null || value.Count == 0) throw new JsonException("Content must be a nonempty array.");
        PromptValidation.Attachments(value);
        return value;
    }

    internal static double Integer(double value, int minimum)
    {
        if (!double.IsFinite(value) || value != Math.Truncate(value) || value < minimum)
            throw new JsonException($"Expected an integer at least {minimum}.");
        return value;
    }

    internal static ModelRef Model(ModelRef value) => value?.Id is not null && value.ProviderId is not null
        ? value : throw new JsonException("Model requires id and providerID.");
}

public sealed class NonEmptyToolContentJsonConverter : JsonConverter<IReadOnlyList<ToolContent>>
{
    private static readonly PromptAttachmentListJsonConverter<ToolContent> Items = new();
    public override bool HandleNull => true;
    public override IReadOnlyList<ToolContent> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        MessageContract.NonEmpty(Items.Read(ref reader, typeToConvert, options));
    public override void Write(Utf8JsonWriter writer, IReadOnlyList<ToolContent> value, JsonSerializerOptions options) =>
        Items.Write(writer, MessageContract.NonEmpty(value), options);
}

public sealed class OptionalValueJsonConverter<T> : JsonConverter<T?> where T : struct
{
    public override bool HandleNull => true;
    public override T? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) throw new JsonException("Optional value must be omitted, not null.");
        return JsonSerializer.Deserialize(ref reader, (JsonTypeInfo<T>)OpenCodeJsonContext.Default.GetTypeInfo(typeof(T))!);
    }
    public override void Write(Utf8JsonWriter writer, T? value, JsonSerializerOptions options)
    {
        if (value is null) throw new JsonException("Optional value must be omitted, not null.");
        JsonSerializer.Serialize(writer, value.Value, (JsonTypeInfo<T>)OpenCodeJsonContext.Default.GetTypeInfo(typeof(T))!);
    }
}

public sealed class PositiveIntegerJsonConverter : JsonConverter<double>
{
    public override double Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        MessageContract.Integer(FiniteNumberJsonConverter.ReadValue(ref reader), 1);
    public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options) =>
        writer.WriteNumberValue(MessageContract.Integer(value, 1));
}

public sealed class NonNegativeIntegerJsonConverter : JsonConverter<double>
{
    public override double Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        MessageContract.Integer(FiniteNumberJsonConverter.ReadValue(ref reader), 0);
    public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options) =>
        writer.WriteNumberValue(MessageContract.Integer(value, 0));
}

public sealed class LlmFinishReasonJsonConverter : JsonConverter<LlmFinishReason>
{
    public override LlmFinishReason Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String) throw new JsonException("Expected finish reason string.");
        return reader.GetString() switch
        {
            "stop" => LlmFinishReason.Stop, "length" => LlmFinishReason.Length, "tool-calls" => LlmFinishReason.ToolCalls,
            "content-filter" => LlmFinishReason.ContentFilter, "error" => LlmFinishReason.Error, "unknown" => LlmFinishReason.Unknown,
            _ => throw new JsonException("Unknown finish reason.")
        };
    }
    public override void Write(Utf8JsonWriter writer, LlmFinishReason value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value switch
        {
            LlmFinishReason.Stop => "stop", LlmFinishReason.Length => "length", LlmFinishReason.ToolCalls => "tool-calls",
            LlmFinishReason.ContentFilter => "content-filter", LlmFinishReason.Error => "error", LlmFinishReason.Unknown => "unknown",
            _ => throw new JsonException("Unknown finish reason.")
        });
}

public sealed class OptionalLlmFinishReasonJsonConverter : JsonConverter<LlmFinishReason?>
{
    private static readonly LlmFinishReasonJsonConverter Reason = new();
    public override bool HandleNull => true;
    public override LlmFinishReason? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        Reason.Read(ref reader, typeof(LlmFinishReason), options);
    public override void Write(Utf8JsonWriter writer, LlmFinishReason? value, JsonSerializerOptions options)
    {
        if (value is null) throw new JsonException("Finish must be omitted, not null.");
        Reason.Write(writer, value.Value, options);
    }
}

public abstract class ScopedMessageTimeJsonConverter(bool completed) : JsonConverter<MessageTime>
{
    public override bool HandleNull => true;
    public override MessageTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("Expected message time object.");
        DateTimeOffset? created = null, ended = null;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                return new MessageTime(created ?? throw new JsonException("Message time requires created."), ended);
            if (reader.TokenType != JsonTokenType.PropertyName) throw new JsonException();
            var field = reader.ValueTextEquals("created"u8) ? 1 : completed && reader.ValueTextEquals("completed"u8) ? 2 : 0;
            if (!reader.Read()) throw new JsonException();
            if (field == 0) { reader.Skip(); continue; }
            var value = EpochMillisecondsJsonConverter.ReadValue(ref reader);
            if (field == 1) created = value;
            else ended = value;
        }
        throw new JsonException("Unterminated message time object.");
    }
    public override void Write(Utf8JsonWriter writer, MessageTime value, JsonSerializerOptions options)
    {
        if (value is null) throw new JsonException("Message time cannot be null.");
        writer.WriteStartObject();
        writer.WriteNumber("created"u8, value.Created.ToUnixTimeMilliseconds());
        if (completed && value.Completed is { } ended) writer.WriteNumber("completed"u8, ended.ToUnixTimeMilliseconds());
        writer.WriteEndObject();
    }
}

public sealed class CreatedMessageTimeJsonConverter() : ScopedMessageTimeJsonConverter(false);
public sealed class CompletedMessageTimeJsonConverter() : ScopedMessageTimeJsonConverter(true);

public sealed class SessionMessageJsonConverter : JsonConverter<SessionMessage>
{
    public override bool HandleNull => true;
    public override SessionMessage Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("Expected session message object.");
        // Probe a copy to dispatch both type and compaction status without a DOM or key-order requirement.
        var probe = reader;
        string? type = null, status = null;
        while (probe.Read() && probe.TokenType != JsonTokenType.EndObject)
        {
            if (probe.TokenType != JsonTokenType.PropertyName) throw new JsonException();
            var field = probe.ValueTextEquals("type"u8) ? 1 : probe.ValueTextEquals("status"u8) ? 2 : 0;
            if (!probe.Read()) throw new JsonException();
            if (field == 0) { probe.Skip(); continue; }
            if (probe.TokenType != JsonTokenType.String)
            {
                if (field == 1) throw new JsonException("Message discriminator must be a string.");
                status = null;
                probe.Skip();
                continue;
            }
            if (field == 1) type = probe.GetString();
            else status = probe.GetString();
        }
        return type switch
        {
            "agent-switched" => JsonSerializer.Deserialize(ref reader, OpenCodeJsonContext.Default.AgentSelectedMessage)!,
            "model-switched" => JsonSerializer.Deserialize(ref reader, OpenCodeJsonContext.Default.ModelSelectedMessage)!,
            "location-switched" => JsonSerializer.Deserialize(ref reader, OpenCodeJsonContext.Default.LocationSwitchedMessage)!,
            "user" => JsonSerializer.Deserialize(ref reader, OpenCodeJsonContext.Default.UserMessage)!,
            "synthetic" => JsonSerializer.Deserialize(ref reader, OpenCodeJsonContext.Default.SyntheticMessage)!,
            "system" => JsonSerializer.Deserialize(ref reader, OpenCodeJsonContext.Default.SystemMessage)!,
            "skill" => JsonSerializer.Deserialize(ref reader, OpenCodeJsonContext.Default.SkillMessage)!,
            "shell" => JsonSerializer.Deserialize(ref reader, OpenCodeJsonContext.Default.ShellMessage)!,
            "assistant" => JsonSerializer.Deserialize(ref reader, OpenCodeJsonContext.Default.AssistantMessage)!,
            "compaction" when status == "running" => JsonSerializer.Deserialize(ref reader, OpenCodeJsonContext.Default.CompactionRunningMessage)!,
            "compaction" when status == "completed" => JsonSerializer.Deserialize(ref reader, OpenCodeJsonContext.Default.CompactionCompletedMessage)!,
            "compaction" when status == "failed" => JsonSerializer.Deserialize(ref reader, OpenCodeJsonContext.Default.CompactionFailedMessage)!,
            _ => throw new JsonException("Unknown or missing session message type/compaction status.")
        };
    }

    public override void Write(Utf8JsonWriter writer, SessionMessage value, JsonSerializerOptions options)
    {
        switch (value)
        {
            case AgentSelectedMessage item: JsonSerializer.Serialize(writer, item, OpenCodeJsonContext.Default.AgentSelectedMessage); break;
            case ModelSelectedMessage item: JsonSerializer.Serialize(writer, item, OpenCodeJsonContext.Default.ModelSelectedMessage); break;
            case LocationSwitchedMessage item: JsonSerializer.Serialize(writer, item, OpenCodeJsonContext.Default.LocationSwitchedMessage); break;
            case UserMessage item: JsonSerializer.Serialize(writer, item, OpenCodeJsonContext.Default.UserMessage); break;
            case SyntheticMessage item: JsonSerializer.Serialize(writer, item, OpenCodeJsonContext.Default.SyntheticMessage); break;
            case SystemMessage item: JsonSerializer.Serialize(writer, item, OpenCodeJsonContext.Default.SystemMessage); break;
            case SkillMessage item: JsonSerializer.Serialize(writer, item, OpenCodeJsonContext.Default.SkillMessage); break;
            case ShellMessage item: JsonSerializer.Serialize(writer, item, OpenCodeJsonContext.Default.ShellMessage); break;
            case AssistantMessage item: JsonSerializer.Serialize(writer, item, OpenCodeJsonContext.Default.AssistantMessage); break;
            case CompactionRunningMessage item: JsonSerializer.Serialize(writer, item, OpenCodeJsonContext.Default.CompactionRunningMessage); break;
            case CompactionCompletedMessage item: JsonSerializer.Serialize(writer, item, OpenCodeJsonContext.Default.CompactionCompletedMessage); break;
            case CompactionFailedMessage item: JsonSerializer.Serialize(writer, item, OpenCodeJsonContext.Default.CompactionFailedMessage); break;
            default: throw new JsonException("Unknown or null session message.");
        }
    }
}

public sealed class CompactionMessageJsonConverter : JsonConverter<CompactionMessage>
{
    private static readonly SessionMessageJsonConverter Message = new();
    public override bool HandleNull => true;
    public override CompactionMessage Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        Message.Read(ref reader, typeof(SessionMessage), options) as CompactionMessage
        ?? throw new JsonException("Expected compaction message.");
    public override void Write(Utf8JsonWriter writer, CompactionMessage value, JsonSerializerOptions options) =>
        Message.Write(writer, value, options);
}
