namespace OpenCode.Core.Event;

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using OpenCode.Schema;

/// <summary>Core owns registration/version; Schema owns canonical data validation and encoding.</summary>
internal static class AssistantEventData
{
    internal static int Version(string type) => type switch
    {
        "session.step.started" or "session.step.streamed" or "session.step.ended" or "session.step.failed" or
        "session.text.started" or "session.text.ended" or "session.reasoning.started" or "session.reasoning.ended" or
        "session.tool.input.started" or "session.tool.input.ended" or "session.tool.called" or "session.retry.scheduled" => 1,
        "session.tool.success" or "session.tool.failed" => 2,
        _ => throw new NotSupportedException("This event is not in the implemented durable assistant family. Deltas and progress are live-only.")
    };

    internal static JsonElement Validate(string type, JsonElement data) => type switch
    {
        "session.step.started" => Encode(data, OpenCodeJsonContext.Default.SessionStepStartedEventData),
        "session.step.streamed" => Encode(data, OpenCodeJsonContext.Default.SessionStepStreamedEventData),
        "session.step.ended" => Encode(data, OpenCodeJsonContext.Default.SessionStepEndedEventData),
        "session.step.failed" => Encode(data, OpenCodeJsonContext.Default.SessionStepFailedEventData),
        "session.text.started" => Encode(data, OpenCodeJsonContext.Default.SessionTextStartedEventData),
        "session.text.ended" or "session.reasoning.ended" => Encode(data, OpenCodeJsonContext.Default.SessionContentEndedEventData),
        "session.reasoning.started" => Encode(data, OpenCodeJsonContext.Default.SessionReasoningStartedEventData),
        "session.tool.input.started" => Encode(data, OpenCodeJsonContext.Default.SessionToolInputStartedEventData),
        "session.tool.input.ended" => Encode(data, OpenCodeJsonContext.Default.SessionToolInputEndedEventData),
        "session.tool.called" => Encode(data, OpenCodeJsonContext.Default.SessionToolCalledEventData),
        "session.tool.success" => Encode(data, OpenCodeJsonContext.Default.SessionToolSuccessEventData),
        "session.tool.failed" => Encode(data, OpenCodeJsonContext.Default.SessionToolFailedEventData),
        "session.retry.scheduled" => Encode(data, OpenCodeJsonContext.Default.SessionRetryScheduledEventData),
        _ => throw new NotSupportedException("Unsupported assistant event.")
    };

    private static JsonElement Encode<T>(JsonElement data, JsonTypeInfo<T> type) where T : class =>
        JsonSerializer.SerializeToElement(data.Deserialize(type) ?? throw new JsonException("Event data is required."), type);

    internal static string String(JsonObject value, string field)
    {
        if (value[field] is not JsonValue node || !node.TryGetValue<string>(out var text))
            throw new JsonException($"Invalid or missing {field} string.");
        return text;
    }

    internal static JsonObject Object(JsonObject value, string field) =>
        value[field] as JsonObject ?? throw new JsonException($"Invalid or missing {field} object.");

    internal static double Finite(JsonObject value, string field)
    {
        if (value[field] is not JsonValue node || !node.TryGetValue<double>(out var number) || !double.IsFinite(number))
            throw new JsonException($"Invalid or missing {field} finite number.");
        return number;
    }

    internal static void Model(JsonObject model)
    {
        String(model, "id");
        String(model, "providerID");
        if (model.ContainsKey("variant")) String(model, "variant");
    }
}
