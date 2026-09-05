namespace OpenCode.Core.Event;

using System.Text.Json;
using OpenCode.Core.Database;
using Microsoft.EntityFrameworkCore;
using OpenCode.Core.Persistence;
using OpenCode.Core.Session;
using OpenCode.Schema;

internal enum RestartPreparation { Missing, Ready, Exhausted }
internal sealed record RestartScope(IReadOnlySet<SessionId> Children, IReadOnlySet<MessageId> Notifications);

internal sealed class RestartPersistence(IDatabase database)
{
    internal async Task<IReadOnlyList<SessionId>> ListAsync(CancellationToken ct)
    {
        var connection = database.CreateConnection();
        await using var connectionLifetime = connection.ConfigureAwait(true);
        var db = new PersistenceContext(connection);
        await using var dbLifetime = db.ConfigureAwait(true);
        return (await db.Sessions.Where(row => row.time_suspended != null && row.parent_id == null).Select(row => row.id).ToListAsync(ct).ConfigureAwait(true))
            .Select(SessionId.FromExisting).ToArray();
    }

    internal Task<RestartPreparation> PrepareAsync(SessionId id, int maxAttempts, CancellationToken ct, RestartScope? scope = null,
        bool child = false, bool localMoves = false) =>
        new EventStore(database).TransactAsync(id.Value, async (transaction, token) =>
        {
            var placement = await transaction.Db.Sessions.Where(row => row.id == id.Value
                && (child ? row.parent_id != null : row.time_suspended != null && row.parent_id == null))
                .Select(row => new { row.workspace_id }).FirstOrDefaultAsync(token).ConfigureAwait(true);
            if (placement is null) return RestartPreparation.Missing;
            if (placement.workspace_id is not null) throw new NotSupportedException("Workspace execution recovery requires its placement service.");
            var notifications = scope?.Notifications.Select(item => item.Value).ToArray() ?? [];
            if (await transaction.Db.Set<KvRow>().AnyAsync(row => string.Compare(row.key, "job.background/") >= 0 && string.Compare(row.key, "job.background0") < 0
                && (SqliteFunctions.JsonValid(row.value) ? SqliteFunctions.JsonText(row.value, "$.recovery.sessionID") == id.Value
                    || SqliteFunctions.JsonText(row.value, "$.recovery.parentSessionID") == id.Value
                    || SqliteFunctions.JsonText(row.value, "$.recovery.childSessionID") == id.Value : false)
                && (SqliteFunctions.JsonValid(row.value) ? (SqliteFunctions.JsonText(row.value, "$.recovery.kind") == "subagent"
                    && notifications.Contains(SqliteFunctions.JsonText(row.value, "$.notificationID")!) ? false : true) : true), token).ConfigureAwait(true))
                throw new NotSupportedException("This claim requires background shell/subagent recovery before top-level execution can resume.");
            // Source history ignores running/failed compaction attempts and uses only completed checkpoints.
            // A delivered control is never reconstructed from an unfinished message or partial summary.
            if (await transaction.Db.Messages.AnyAsync(row => row.session_id == id.Value
                && ((row.type == "shell" && (SqliteFunctions.JsonText(row.data, "$.status") ?? "running") == "running")
                    || (row.type == "compaction" && (SqliteFunctions.JsonText(row.data, "$.status") ?? "") != "running"
                        && (SqliteFunctions.JsonText(row.data, "$.status") ?? "") != "completed"
                        && (SqliteFunctions.JsonText(row.data, "$.status") ?? "") != "failed")), token).ConfigureAwait(true))
                throw new NotSupportedException("This history contains unsupported compaction state or requires running shell recovery integration.");
            var controls = transaction.Db.Inbox.Where(row => row.session_id == id.Value && row.type == "move")
                .OrderBy(row => row.enqueued_seq).Select(row => row.payload);
            await foreach (var payload in controls.ReadAsync(-1, token).ConfigureAwait(true))
            {
                if (!localMoves) throw new NotSupportedException("Pending local movement requires the host's SessionMovement composition before recovery.");
                var move = JsonSerializer.Deserialize(payload, OpenCodeJsonContext.Default.MoveInboxPayload)
                    ?? throw new JsonException("Missing pending move payload.");
                if (move.Location.WorkspaceId is not null)
                    throw new NotSupportedException("Workspace movement recovery requires destination Location routing.");
            }
            var children = scope?.Children.Select(item => item.Value).ToArray() ?? [];
            if (await transaction.Db.Sessions.AnyAsync(row => row.parent_id == id.Value && row.time_suspended != null && !children.Contains(row.id), token).ConfigureAwait(true))
                throw new NotSupportedException("Claimed children require child/background recovery before this parent resumes.");
            if (await transaction.Db.HighestProjectionAsync(id.Value, token).ConfigureAwait(true) > await transaction.LatestSequenceAsync(id.Value, token).ConfigureAwait(true))
                throw new NotSupportedException("Unsequenced projections require canonical migration before recovery.");
            if (await SqliteIntrinsics.IncrementResumeAsync(transaction.Db, id.Value, token).ConfigureAwait(true) is not { } attempts) return RestartPreparation.Missing;
            if (attempts > maxAttempts)
            {
                await transaction.AppendAsync(ExecutionProjector.Definition("session.execution.failed"), JsonSerializer.SerializeToElement(new
                {
                    sessionID = id.Value,
                    error = new { type = "aborted", message = "Execution was interrupted repeatedly and will not be resumed automatically." }
                }), token).ConfigureAwait(true);
                return RestartPreparation.Exhausted;
            }
            await transaction.AppendAsync(SessionSyntheticProjector.Definition, new SessionSyntheticData(id,
                "The server restarted while you were working. Continue from where you left off without repeating completed work.",
                "Continuing after restart"), token).ConfigureAwait(true);
            return RestartPreparation.Ready;
        }, ct);

    /// <summary>Source releaseChildClaims: abandoned foreground children do not get restarted as detached jobs.</summary>
    internal async Task ReleaseChildClaimsAsync(IReadOnlySet<SessionId> preserve, CancellationToken ct)
    {
        var children = new List<SessionId>();
        {
            var connection = database.CreateConnection();
            await using var connectionLifetime = connection.ConfigureAwait(true);
            var db = new PersistenceContext(connection);
            await using var dbLifetime = db.ConfigureAwait(true);
            children.AddRange((await db.Sessions.Where(row => row.time_suspended != null && row.parent_id != null).Select(row => row.id).ToListAsync(ct).ConfigureAwait(true))
                .Select(SessionId.FromExisting));
        }
        foreach (var id in children.Where(id => !preserve.Contains(id)))
        {
            if (SessionRunCoordinator.IsActive(id)) continue;
            try
            {
                await SessionRunCoordinator.WithIdleOperationAsync(id, token => new EventStore(database).TransactAsync(id.Value, async (transaction, cancellation) =>
                {
                    // Unsupported shell/unknown background domains still own their recovery decision.
                    if (await transaction.Db.Set<KvRow>().AnyAsync(row => string.Compare(row.key, "job.background/") >= 0 && string.Compare(row.key, "job.background0") < 0
                        && (SqliteFunctions.JsonValid(row.value) ? (SqliteFunctions.JsonText(row.value, "$.recovery.sessionID") == id.Value
                            || SqliteFunctions.JsonText(row.value, "$.recovery.childSessionID") == id.Value)
                            && (SqliteFunctions.JsonText(row.value, "$.recovery.kind") ?? "") != "subagent" : false), cancellation).ConfigureAwait(true)) return 0;
                    return await transaction.Db.Sessions.Where(row => row.id == id.Value && row.parent_id != null && row.time_suspended != null)
                        .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.time_suspended, (long?)null).SetProperty(row => row.resume_attempts, 0), cancellation).ConfigureAwait(true);
                }, token), ct).ConfigureAwait(true);
            }
            catch (SessionBusyException) { } // A current process owner is never an abandoned child.
        }
    }
}
