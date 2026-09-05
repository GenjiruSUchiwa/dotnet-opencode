namespace OpenCode.Core.Event;

using System.Text.Json;
using OpenCode.Core.Database;
using Microsoft.EntityFrameworkCore;
using OpenCode.Core.Persistence;
using OpenCode.Schema;

internal sealed class ExecutionProjector(IDatabase database)
{
    internal Task<OpenCodeEvent> AppendAsync(SessionId sessionId, string type, SessionStructuredError? error,
        string? reason, CancellationToken ct)
    {
        var data = new System.Text.Json.Nodes.JsonObject { ["sessionID"] = sessionId.Value };
        switch (type)
        {
            case "session.execution.started":
            case "session.execution.succeeded": break;
            case "session.execution.failed":
                if (error is null) throw new ArgumentNullException(nameof(error));
                data["error"] = JsonSerializer.SerializeToNode(error, OpenCodeJsonContext.Default.SessionStructuredError);
                break;
            case "session.execution.interrupted":
                if (reason is not ("user" or "shutdown" or "superseded")) throw new ArgumentOutOfRangeException(nameof(reason));
                data["reason"] = reason;
                break;
            default: throw new NotSupportedException("Unsupported execution lifecycle event.");
        }
        return new EventStore(database).TransactAsync(sessionId.Value, async (transaction, token) =>
        {
            if (!await transaction.Db.Sessions.AnyAsync(row => row.id == sessionId.Value, token).ConfigureAwait(true)) throw new InvalidOperationException("Session not found.");
            return await transaction.AppendAsync(Definition(type), JsonSerializer.SerializeToElement(data), token).ConfigureAwait(true);
        }, ct);
    }

    internal static DurableEventDefinition<JsonElement> Definition(string type) => new(type, 1, "sessionID",
        AssistantEventJsonContext.Default.JsonElement, ProjectAsync);

    private static async Task ProjectAsync(EventTransaction transaction, OpenCodeEvent committed, CancellationToken ct)
    {
        var id = committed.Data.GetProperty("sessionID").GetString()!;
        if (committed.Type == "session.execution.started")
        {
            if (transaction.Replaying) return; // Source started projection is void; local claim is an operational commit hook.
            if (await transaction.Db.HighestProjectionAsync(id, ct).ConfigureAwait(true) >= committed.Durable!.Seq)
                throw new NotSupportedException("Unsequenced projections require canonical migration before execution.");
            await SqliteIntrinsics.ClaimExecutionAsync(transaction.Db, id, committed.Created, ct).ConfigureAwait(true);
            return;
        }
        // SessionMessageUpdater.clearCurrentRetry only considers the newest assistant.
        await SqliteIntrinsics.ClearCurrentRetryAsync(transaction.Db, id, transaction.Clock.GetUtcNow().ToUnixTimeMilliseconds(), ct).ConfigureAwait(true);
        if (committed.Type == "session.execution.interrupted" && committed.Data.GetProperty("reason").GetString() == "shutdown") return;
        await SqliteIntrinsics.CompleteExecutionAsync(transaction.Db, id, committed.Created, committed.Type switch
        {
            "session.execution.succeeded" => "succeeded",
            "session.execution.failed" => "failed",
            _ => "interrupted"
        }, transaction.Replaying, ct).ConfigureAwait(true);
    }
}
