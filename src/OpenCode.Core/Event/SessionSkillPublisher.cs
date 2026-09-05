namespace OpenCode.Core.Event;

using System.Text.Json;
using System.Text.Json.Nodes;
using OpenCode.Core.Database;
using Microsoft.EntityFrameworkCore;
using OpenCode.Core.Persistence;
using OpenCode.Core.Session;
using OpenCode.Core.Session.Skills;
using OpenCode.Schema;

/// <summary>Standalone skill activation's canonical durable publisher; no inbox reconciliation or model/tool execution.</summary>
public sealed class SessionSkillPublisher(IDatabase database) : ISessionSkillPublisher
{
    internal static readonly DurableEventDefinition<SessionSkillActivatedData> Activated = new(
        "session.skill.activated", 1, "sessionID", SessionSkillJsonContext.Default.SessionSkillActivatedData, ProjectAsync);

    public Task PublishAsync(SessionSkillActivatedData data, EventId? eventId, CancellationToken ct) =>
        SessionRunCoordinator.AdmitAsync(data.SessionId, () => new EventStore(database).TransactAsync(data.SessionId.Value, async (transaction, token) =>
        {
            // Resolve existence in the current aggregate, not the skill file's original Location.
            // Like source Session.skill, the event is Session-ID based and is not pinned to a stale directory.
            if (!await transaction.Db.Sessions.AnyAsync(row => row.id == data.SessionId.Value, token)) throw new SessionMutationNotFoundException(data.SessionId);
            if (await transaction.Db.HighestProjectionAsync(data.SessionId.Value, token) > await transaction.LatestSequenceAsync(data.SessionId.Value, token))
                throw new NotSupportedException("Unsequenced projections require canonical migration before skill activation.");
            return await transaction.AppendAsync(Activated, data, token, eventId);
        }, ct), ct);

    private static async Task ProjectAsync(EventTransaction transaction, OpenCodeEvent committed, CancellationToken ct)
    {
        var data = committed.Data.Deserialize(SessionSkillJsonContext.Default.SessionSkillActivatedData)!;
        var message = new JsonObject
        {
            ["skill"] = data.Id.Value, ["name"] = data.Name, ["text"] = data.Text,
            ["time"] = new JsonObject { ["created"] = committed.Created }
        };
        if (committed.Metadata is not null) message["metadata"] = JsonSerializer.SerializeToNode(committed.Metadata);
        await SqliteIntrinsics.InsertMessageAsync(transaction.Db, "msg_" + committed.Id.Value[4..], data.SessionId.Value, "skill",
            checked((long)committed.Durable!.Seq), committed.Created, transaction.Clock.GetUtcNow().ToUnixTimeMilliseconds(), message.ToJsonString(), ct);
    }
}
