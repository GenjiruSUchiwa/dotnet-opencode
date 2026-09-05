namespace OpenCode.Core.Event;

using System.Text.Json;
using System.Text.Json.Nodes;
using OpenCode.Schema;
using Microsoft.EntityFrameworkCore;
using OpenCode.Core.Persistence;

internal static class CompactionProjector
{
    internal static readonly DurableEventDefinition<SessionCompactionStartedEventData> Started = Bind(SessionEventDefinitions.Compaction.Started);
    internal static readonly DurableEventDefinition<SessionCompactionEndedEventData> Ended = Bind(SessionEventDefinitions.Compaction.Ended);
    internal static readonly DurableEventDefinition<SessionCompactionFailedEventData> Failed = Bind(SessionEventDefinitions.Compaction.Failed);
    internal static readonly DurableEventDefinition<SessionUsageRecordedEventData> Usage = new(
        "session.usage.recorded", 1, "sessionID", OpenCodeJsonContext.Default.SessionUsageRecordedEventData, ProjectUsageAsync);

    private static DurableEventDefinition<T> Bind<T>(OpenCode.Schema.DurableEventDefinition<T> definition) where T : class =>
        new(definition.Type, checked((int)definition.Durable.Version), definition.Durable.Aggregate, definition.DataType, ProjectAsync);

    private static async Task ProjectAsync(EventTransaction transaction, OpenCodeEvent committed, CancellationToken ct)
    {
        var data = committed.Data;
        var session = data.GetProperty("sessionID").GetString()!;
        var reason = data.GetProperty("reason").GetString();
        if (reason is not ("auto" or "manual")) throw new JsonException("Unknown compaction reason.");
        string? currentId = null;
        JsonObject? current = null;
        if (committed.Type != Started.Type)
        {
            var row = await transaction.Db.Messages.Where(row => row.session_id == session && row.type == "compaction"
                && SqliteFunctions.JsonText(row.data, "$.status") == "running").OrderByDescending(row => row.seq)
                .Select(row => new { row.id, row.data }).FirstOrDefaultAsync(ct);
            if (row is not null) { currentId = row.id; current = JsonNode.Parse(row.data)!.AsObject(); }
        }
        var id = currentId ?? (data.TryGetProperty("inputID", out var input) ? input.GetString()! : "msg_" + committed.Id.Value[4..]);
        JsonObject message;
        if (committed.Type == Failed.Type)
        {
            message = new JsonObject
            {
                ["status"] = "failed", ["reason"] = reason,
                ["error"] = JsonNode.Parse(data.GetProperty("error").GetRawText()),
                ["time"] = current?["time"]?.DeepClone() ?? new JsonObject { ["created"] = committed.Created }
            };
            if (current?["metadata"] is { } metadata) message["metadata"] = metadata.DeepClone();
        }
        else
        {
            message = current ?? new JsonObject { ["time"] = new JsonObject { ["created"] = committed.Created } };
            message["reason"] = reason;
            message["status"] = committed.Type == Started.Type ? "running" : "completed";
            message["summary"] = committed.Type == Started.Type ? "" : data.GetProperty("text").GetString();
            message["recent"] = data.GetProperty("recent").GetString();
        }
        if (currentId is null && committed.Metadata is not null)
            message["metadata"] = JsonSerializer.SerializeToNode(committed.Metadata);
        // Validate the canonical discriminated message before writing its projection.
        var complete = new JsonObject { ["id"] = id, ["type"] = "compaction" };
        foreach (var field in message) complete[field.Key] = field.Value?.DeepClone();
        _ = complete.Deserialize(OpenCodeJsonContext.Default.SessionMessage) ?? throw new JsonException("Missing compaction message.");
        if (currentId is null)
            await SqliteIntrinsics.InsertMessageAsync(transaction.Db, id, session, "compaction", checked((long)committed.Durable!.Seq),
                message["time"]!["created"]!.GetValue<double>(), transaction.Clock.GetUtcNow().ToUnixTimeMilliseconds(), message.ToJsonString(), ct);
        else
            await SqliteIntrinsics.SaveCompactionAsync(transaction.Db, session, id, message.ToJsonString(), message["time"]!["created"]!.GetValue<double>(),
                transaction.Clock.GetUtcNow().ToUnixTimeMilliseconds(), ct);
        if (committed.Type != Ended.Type) return;
        var sequence = checked((long)committed.Durable!.Seq);
        await transaction.Db.Set<InstructionStateRow>().Where(row => row.session_id == session).ExecuteUpdateAsync(setters => setters
            .SetProperty(row => row.epoch_start, sequence).SetProperty(row => row.through_seq, sequence)
            .SetProperty(row => row.initial_values, row => row.current_values), ct);
    }

    private static async Task ProjectUsageAsync(EventTransaction transaction, OpenCodeEvent committed, CancellationToken ct)
    {
        var usage = committed.Data.Deserialize(OpenCodeJsonContext.Default.SessionUsageRecordedEventData)!;
        await SqliteIntrinsics.AddUsageAsync(transaction.Db, usage.SessionId.Value, usage.Cost.Amount, usage.Tokens.Input, usage.Tokens.Output,
            usage.Tokens.Reasoning, usage.Tokens.Cache.Read, usage.Tokens.Cache.Write, ct);
    }
}
