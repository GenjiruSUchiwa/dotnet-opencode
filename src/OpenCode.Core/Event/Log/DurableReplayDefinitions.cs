namespace OpenCode.Core.Event.Log;

using System.Text.Json;
using System.Text.Json.Nodes;
using OpenCode.Core.Session.Transfer;
using OpenCode.Schema;

internal sealed record DurableReplayDefinition(string Type, int Version, string Aggregate,
    Func<JsonElement, JsonElement> Decode, Func<EventTransaction, OpenCodeEvent, CancellationToken, Task> Project);

/// <summary>Exact implemented Session definitions/projectors, initialized independently of prior local publications.</summary>
internal static class DurableReplayDefinitions
{
    private static readonly IReadOnlyDictionary<string, DurableReplayDefinition> Definitions = Build();

    internal static (OpenCodeEvent Event, DurableReplayDefinition? Definition) Decode(SerializedDurableEvent input, bool requireProjector)
    {
        ArgumentNullException.ThrowIfNull(input.AggregateId);
        if (input.Data.ValueKind != JsonValueKind.Object) throw new InvalidDurableEventException(input.Type, "Durable event data must be an object.");
        if (Definitions.TryGetValue(input.Type, out var definition))
        {
            var data = definition.Decode(input.Data);
            if (!data.TryGetProperty(definition.Aggregate, out var aggregate) || aggregate.ValueKind != JsonValueKind.String || aggregate.GetString() != input.AggregateId)
                throw new InvalidDurableEventException(input.Type, $"Expected matching string aggregate field {definition.Aggregate}.");
            var result = new OpenCodeEvent(input.Id, definition.Type, input.Created ?? 0, data,
                Durable: new DurableEnvelope(input.AggregateId, input.Seq, definition.Version));
            result.Validate();
            return (result, definition);
        }
        if (!requireProjector && EventManifest.ImplementedDurable.TryGetValue(input.Type, out var schema) && schema.Durable is { } durable)
        {
            var result = schema.Normalize(new OpenCodeEvent(input.Id, schema.Type, input.Created ?? 0, input.Data,
                Durable: new DurableEnvelope(input.AggregateId, input.Seq, durable.Version)));
            if (!result.Data.TryGetProperty(durable.Aggregate, out var aggregate) || aggregate.ValueKind != JsonValueKind.String || aggregate.GetString() != input.AggregateId)
                throw new InvalidDurableEventException(input.Type, "Stored event aggregate does not match its payload.");
            return (result, null);
        }
        throw new InvalidDurableEventException(input.Type, requireProjector
            ? $"No implemented canonical replay projector for {input.Type}" : $"Unknown durable event type {input.Type}");
    }

    private static IReadOnlyDictionary<string, DurableReplayDefinition> Build()
    {
        var result = new Dictionary<string, DurableReplayDefinition>(StringComparer.Ordinal);
        void Add<T>(OpenCode.Core.Event.DurableEventDefinition<T> item) => result.Add($"{item.Type}.{item.Version}", new(item.Type, item.Version, item.AggregateField,
            data =>
            {
                foreach (var property in item.DataType.Properties.Where(property => property.IsRequired))
                    if (!data.TryGetProperty(property.Name, out var value) || value.ValueKind == JsonValueKind.Null)
                        throw new JsonException($"Missing required event field {property.Name}.");
                return JsonSerializer.SerializeToElement(data.Deserialize(item.DataType) ?? throw new JsonException("Missing event payload."), item.DataType);
            }, item.Project));
        Add(SessionCreation.Created);
        Add(SessionAdmission.Enqueued); Add(SessionAdmission.Delivered); Add(SessionAdmission.Cancelled); Add(SessionAdmission.DeliveryChanged);
        Add(SessionMutationProjector.Deleted); Add(SessionMutationProjector.Renamed); Add(SessionMutationProjector.Viewed);
        Add(SessionMutationProjector.AgentSelected); Add(SessionMutationProjector.ModelSelected);
        Add(SessionSyntheticProjector.Definition); Add(SessionSkillPublisher.Activated);
        Add(SessionShellPersistence.Started); Add(SessionShellPersistence.Ended);
        Add(CompactionProjector.Started); Add(CompactionProjector.Ended); Add(CompactionProjector.Failed); Add(CompactionProjector.Usage);
        Add(InstructionPersistence.Updated);
        Add(SessionRevertPersistence.Staged); Add(SessionRevertPersistence.Cleared); Add(SessionRevertPersistence.Committed);
        Add(MoveProjector.Moved); Add(ForkProjector.Forked);
        foreach (var type in new[] { "session.step.started", "session.step.streamed", "session.step.ended", "session.step.failed",
            "session.text.started", "session.text.ended", "session.reasoning.started", "session.reasoning.ended", "session.tool.input.started",
            "session.tool.input.ended", "session.tool.called", "session.tool.success", "session.tool.failed", "session.retry.scheduled" })
        {
            var version = AssistantEventData.Version(type);
            result.Add($"{type}.{version}", new(type, version, "sessionID", data => AssistantEventData.Validate(type, data), AssistantProjector.ProjectAsync));
        }
        foreach (var type in new[] { "session.execution.started", "session.execution.succeeded", "session.execution.failed", "session.execution.interrupted" })
        {
            var item = ExecutionProjector.Definition(type);
            result.Add($"{type}.1", new(type, 1, "sessionID", data =>
            {
                var encoded = new JsonObject { ["sessionID"] = data.GetProperty("sessionID").GetString() ?? throw new JsonException("Missing Session ID.") };
                if (type == "session.execution.failed") encoded["error"] = JsonSerializer.SerializeToNode(
                    data.GetProperty("error").Deserialize(OpenCodeJsonContext.Default.SessionStructuredError) ?? throw new JsonException("Missing error."), OpenCodeJsonContext.Default.SessionStructuredError);
                if (type == "session.execution.interrupted")
                {
                    var reason = data.GetProperty("reason").GetString();
                    if (reason is not ("user" or "shutdown" or "superseded")) throw new JsonException("Invalid interruption reason.");
                    encoded["reason"] = reason;
                }
                return JsonSerializer.SerializeToElement(encoded);
            }, item.Project));
        }
        return result;
    }
}
