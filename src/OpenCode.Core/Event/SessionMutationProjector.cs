namespace OpenCode.Core.Event;

using System.Text.Json;
using System.Text.Json.Serialization;
using OpenCode.Core.Database;
using Microsoft.EntityFrameworkCore;
using OpenCode.Core.Persistence;
using OpenCode.Core.Session;
using OpenCode.Schema;

// Persisted write data from schema/session-event.ts; these are not another public event registry.
internal sealed record SessionDeletionData([property: JsonPropertyName("sessionID"), JsonRequired] SessionId SessionId);
internal sealed record SessionRenameData(
    [property: JsonPropertyName("sessionID"), JsonRequired] SessionId SessionId,
    [property: JsonPropertyName("title"), JsonRequired] string Title);
internal sealed record SessionViewData(
    [property: JsonPropertyName("sessionID"), JsonRequired] SessionId SessionId,
    [property: JsonPropertyName("idle"), JsonRequired] long Idle);
internal sealed record SessionAgentSelectionData(
    [property: JsonPropertyName("sessionID"), JsonRequired] SessionId SessionId,
    [property: JsonPropertyName("agent"), JsonRequired] string Agent,
    [property: JsonPropertyName("previous")] string? Previous);
internal sealed record SessionModelSelectionData(
    [property: JsonPropertyName("sessionID"), JsonRequired] SessionId SessionId,
    [property: JsonPropertyName("model"), JsonRequired] ModelRef Model,
    [property: JsonPropertyName("previous")] ModelRef? Previous);

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SessionDeletionData))]
[JsonSerializable(typeof(SessionRenameData))]
[JsonSerializable(typeof(SessionViewData))]
[JsonSerializable(typeof(SessionAgentSelectionData))]
[JsonSerializable(typeof(SessionModelSelectionData))]
internal partial class SessionMutationEventJsonContext : JsonSerializerContext;

/// <summary>Selected session/projector.ts and message-updater.ts projections, using the existing commit boundary.</summary>
internal sealed class SessionMutationProjector(IDatabase database)
{
    internal static readonly DurableEventDefinition<SessionDeletionData> Deleted = new(
        "session.deleted", 2, "sessionID", SessionMutationEventJsonContext.Default.SessionDeletionData, ProjectAsync);
    internal static readonly DurableEventDefinition<SessionRenameData> Renamed = new(
        "session.renamed", 1, "sessionID", SessionMutationEventJsonContext.Default.SessionRenameData, ProjectAsync);
    internal static readonly DurableEventDefinition<SessionViewData> Viewed = new(
        "session.viewed", 1, "sessionID", SessionMutationEventJsonContext.Default.SessionViewData, ProjectAsync);
    internal static readonly DurableEventDefinition<SessionAgentSelectionData> AgentSelected = new(
        "session.agent.selected", 1, "sessionID", SessionMutationEventJsonContext.Default.SessionAgentSelectionData, ProjectAsync);
    internal static readonly DurableEventDefinition<SessionModelSelectionData> ModelSelected = new(
        "session.model.selected", 1, "sessionID", SessionMutationEventJsonContext.Default.SessionModelSelectionData, ProjectAsync);

    internal Task RequireRemovalReadyAsync(SessionId id, CancellationToken ct) =>
        new EventStore(database).TransactAsync(id.Value, async (transaction, token) =>
        {
            await RequireRemovableAsync(transaction, id, token);
            return true;
        }, ct);

    internal Task RemoveAsync(SessionId id, CancellationToken ct) =>
        new EventStore(database).TransactAsync(id.Value, async (transaction, token) =>
        {
            await RequireRemovableAsync(transaction, id, token);
            await transaction.AppendAsync(Deleted, new SessionDeletionData(id), token);
            // Bus.remove purges the entire aggregate, including its deletion event.
            // Purge AFTER AppendAsync reserves its sequence, in the same transaction
            // as projection; the committed envelope still reaches post-commit observers.
            await transaction.Db.Sequences.Where(row => row.aggregate_id == id.Value).ExecuteDeleteAsync(token);
            await transaction.Db.Events.Where(row => row.aggregate_id == id.Value).ExecuteDeleteAsync(token);
            return true;
        }, ct);

    private static async Task RequireRemovableAsync(EventTransaction transaction, SessionId id, CancellationToken ct)
    {
        var session = await transaction.Db.Sessions.Where(row => row.id == id.Value).Select(row => new { row.workspace_id }).FirstOrDefaultAsync(ct)
            ?? throw new SessionMutationNotFoundException(id);
        if (session.workspace_id is not null) throw new NotSupportedException("Workspace session deletion requires its Location transport lifecycle.");
        if (await transaction.Db.Sessions.AnyAsync(row => row.parent_id == id.Value, ct))
            throw new NotSupportedException("Recursive session deletion requires child and background-job lifecycle integration. No session was deleted.");
        // Job.background records are not session foreign keys. Do not leave a
        // recoverable shell/subagent notification pointing at a deleted Session.
        if (await transaction.Db.Set<KvRow>().AnyAsync(row => string.Compare(row.key, "job.background/") >= 0 && string.Compare(row.key, "job.background0") < 0
            && (SqliteFunctions.JsonValid(row.value) ? SqliteFunctions.JsonText(row.value, "$.recovery.sessionID") == id.Value
                || SqliteFunctions.JsonText(row.value, "$.recovery.parentSessionID") == id.Value
                || SqliteFunctions.JsonText(row.value, "$.recovery.childSessionID") == id.Value : false), ct))
            throw new NotSupportedException("Session deletion requires the related background-job cancellation and notification lifecycle.");
        if (await transaction.Db.Messages.Where(row => row.session_id == id.Value).MaxAsync(row => (long?)row.seq, ct) is { } seq
            && seq > await transaction.LatestSequenceAsync(id.Value, ct))
            throw new NotSupportedException("Unsequenced history requires canonical migration before durable session deletion.");
    }

    internal Task PublishAsync<T>(SessionId id, DurableEventDefinition<T> definition, Func<SessionInfo, T?> select, CancellationToken ct,
        long? expectedSequence = null)
        where T : class => new EventStore(database).TransactAsync(id.Value, async (transaction, token) =>
        {
            if (expectedSequence is { } expected && await transaction.LatestSequenceAsync(id.Value, token) + 1 != expected) return false;
            var session = SessionStore.ReadSession(await transaction.Db.Sessions.FirstOrDefaultAsync(row => row.id == id.Value, token)
                ?? throw new SessionMutationNotFoundException(id));
            var data = select(session);
            if (data is null) return false;
            // Do not let a new mutation disguise pre-event, unsequenced message history.
            if (await transaction.Db.Messages.Where(row => row.session_id == id.Value).MaxAsync(row => (long?)row.seq, token) is { } seq
                && seq > await transaction.LatestSequenceAsync(id.Value, token))
                throw new NotSupportedException("Unsequenced history requires canonical migration before durable session mutation.");
            await transaction.AppendAsync(definition, data, token);
            return true;
        }, ct);

    private static async Task ProjectAsync(EventTransaction transaction, OpenCodeEvent committed, CancellationToken ct)
    {
        if (committed.Type == Deleted.Type)
        {
            var id = committed.Data.Deserialize(SessionMutationEventJsonContext.Default.SessionDeletionData)!.SessionId.Value;
            // Canonical foreign keys remove messages, pending/inbox, instruction
            // entries/state, and the row-owned execution claim. Shared instruction
            // blobs, projects, fork provenance, and filesystem artifacts are retained.
            await transaction.Db.Sessions.Where(row => row.id == id).ExecuteDeleteAsync(ct);
            return;
        }
        if (committed.Type == Viewed.Type)
        {
            var data = committed.Data.Deserialize(SessionMutationEventJsonContext.Default.SessionViewData)!;
            await transaction.Db.Sessions.Where(row => row.id == data.SessionId.Value).ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.time_viewed, row => Math.Max(data.Idle, row.time_viewed ?? data.Idle)), ct);
            return;
        }

        string column;
        string value;
        SessionMessage? message = null;
        switch (committed.Type)
        {
            case "session.renamed":
                column = "title";
                value = committed.Data.Deserialize(SessionMutationEventJsonContext.Default.SessionRenameData)!.Title;
                break;
            case "session.agent.selected":
                var agent = committed.Data.Deserialize(SessionMutationEventJsonContext.Default.SessionAgentSelectionData)!;
                column = "agent";
                value = agent.Agent;
                message = new AgentSelectedMessage
                {
                    Id = MessageId.FromExisting("msg_" + committed.Id.Value[EventId.Prefix.Length..]),
                    Time = new MessageTime(DateTimeOffset.FromUnixTimeMilliseconds(checked((long)committed.Created))),
                    Metadata = committed.Metadata,
                    Agent = agent.Agent,
                    Previous = agent.Previous
                };
                break;
            case "session.model.selected":
                var model = committed.Data.Deserialize(SessionMutationEventJsonContext.Default.SessionModelSelectionData)!;
                column = "model";
                value = JsonSerializer.Serialize(model.Model, OpenCodeJsonContext.Default.ModelRef);
                message = new ModelSelectedMessage
                {
                    Id = MessageId.FromExisting("msg_" + committed.Id.Value[EventId.Prefix.Length..]),
                    Time = new MessageTime(DateTimeOffset.FromUnixTimeMilliseconds(checked((long)committed.Created))),
                    Metadata = committed.Metadata,
                    Model = model.Model,
                    Previous = model.Previous
                };
                break;
            default: throw new NotSupportedException("Unsupported session mutation event.");
        }

        if (message is not null)
        {
            var encoded = JsonSerializer.SerializeToNode(message, OpenCodeJsonContext.Default.SessionMessage)!.AsObject();
            encoded.Remove("id");
            encoded.Remove("type");
            await SqliteIntrinsics.InsertMessageAsync(transaction.Db, message.Id.Value, committed.Durable!.AggregateId, message.Type,
                checked((long)committed.Durable.Seq), committed.Created, transaction.Clock.GetUtcNow().ToUnixTimeMilliseconds(), encoded.ToJsonString(), ct);
        }
        await transaction.Db.Sessions.Where(row => row.id == committed.Durable!.AggregateId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => EF.Property<string?>(row, column), value), ct);
        await SqliteIntrinsics.TouchSessionAsync(transaction.Db, committed.Durable!.AggregateId, committed.Created, ct);
    }
}
