namespace OpenCode.Core.Persistence;

using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using OpenCode.Core.Session.Archive;
using OpenCode.Schema;

/// <summary>Reviewed SQLite operations whose conflict/returning behavior is part of the source contract.</summary>
internal static class SqliteIntrinsics
{
    // These scalar reads deliberately have no Session-table root. Projections
    // are checked even if the Session row is absent, using one SQLite snapshot.
    internal static Task<long> HighestProjectionAsync(PersistenceContext db, string session, CancellationToken ct) =>
        db.Database.SqlQuery<long>($"""
            SELECT max((SELECT coalesce(max(seq), -1) FROM session_message WHERE session_id = {session}),
                       (SELECT coalesce(max(enqueued_seq), -1) FROM session_inbox WHERE session_id = {session})) AS Value
            """).SingleAsync(ct);

    internal static Task<bool> HasUnsequencedProjectionAsync(PersistenceContext db, string session, CancellationToken ct) =>
        db.Database.SqlQuery<bool>($"""
            SELECT max((SELECT coalesce(max(seq), -1) FROM session_message WHERE session_id = {session}),
                       (SELECT coalesce(max(enqueued_seq), -1) FROM session_inbox WHERE session_id = {session}))
                > coalesce((SELECT seq FROM event_sequence WHERE aggregate_id = {session}), -1) AS Value
            """).SingleAsync(ct);

    internal static Task<int> ForkSessionAsync(PersistenceContext db, string id, string parent, string boundary, string slug, string? title,
        string directory, string? subpath, double created, CancellationToken ct) =>
        db.Database.ExecuteSqlAsync($"""
            INSERT INTO session_v2 (id, parent_id, fork_session_id, fork_boundary, project_id, workspace_id,
                slug, directory, path, title, agent, model, metadata, version, time_created, time_updated)
            SELECT {id}, NULL, id, {boundary}, project_id, workspace_id, {slug}, {directory}, {subpath}, {title},
                agent, model, metadata, version, {created}, {created} FROM session_v2 WHERE id = {parent}
            ON CONFLICT DO NOTHING
            """, ct);

    internal static Task ForkMessagesAsync(PersistenceContext db, string id, string parent, string prefix, long last, CancellationToken ct) =>
        db.Database.ExecuteSqlAsync($"""
            INSERT INTO session_message (id, session_id, type, seq, time_created, time_updated, data)
            SELECT {prefix} || '_' || seq, {id}, type, seq, time_created, time_updated, data
            FROM session_message WHERE session_id = {parent} AND seq > -1 AND seq <= {last}
              AND (type != 'assistant' OR json_extract(data, '$.time.completed') IS NOT NULL)
              AND (type != 'shell' OR json_extract(data, '$.status') != 'running')
              AND (type != 'compaction' OR json_extract(data, '$.status') != 'running')
            ORDER BY seq ASC
            """, ct);

    internal static Task ForkInstructionEntryAsync(PersistenceContext db, string id, string key, string? value, bool removed, double created, CancellationToken ct) =>
        db.Database.ExecuteSqlAsync($"""
            INSERT INTO instruction_entry (session_id, key, value, removed, time_created, time_updated)
            VALUES ({id}, {key}, {value}, {removed}, {created}, {created})
            """, ct);

    internal static Task ForkInstructionStateAsync(PersistenceContext db, string id, long sequence, string values, CancellationToken ct) =>
        db.Database.ExecuteSqlAsync($"""
            INSERT INTO instruction_state (session_id, epoch_start, through_seq, initial_values, current_values)
            VALUES ({id}, {sequence}, {sequence}, {values}, {values}) ON CONFLICT DO NOTHING
            """, ct);

    internal static Task AddWorktreeAsync(PersistenceContext db, string project, string directory, string? strategy, long created, CancellationToken ct) =>
        db.Database.ExecuteSqlAsync($"""
            INSERT INTO worktree(project_id,directory,strategy,time_created) VALUES ({project},{directory},{strategy},{created}) ON CONFLICT DO NOTHING
            """, ct);

    internal static Task RestoreArchiveAsync(PersistenceContext db, SessionArchiveImport input, CancellationToken ct)
    {
        var viewed = input.Time.Idle is { } idle && input.Time.Viewed is { } timeViewed
            ? (long?)Math.Min(idle.ToUnixTimeMilliseconds(), timeViewed.ToUnixTimeMilliseconds()) : null;
        var outcome = input.Time.Idle is not null && input.Outcome is not null
            ? JsonSerializer.Serialize(input.Outcome, OpenCodeJsonContext.Default.SessionOutcome) : null;
        return db.Database.ExecuteSqlAsync($"""
            UPDATE session_v2 SET cost = {input.Cost.Amount}, tokens_input = {input.Tokens.Input}, tokens_output = {input.Tokens.Output},
                tokens_reasoning = {input.Tokens.Reasoning}, tokens_cache_read = {input.Tokens.Cache.Read}, tokens_cache_write = {input.Tokens.Cache.Write},
                time_created = {input.Time.Created.ToUnixTimeMilliseconds()}, time_updated = {input.Time.Updated.ToUnixTimeMilliseconds()},
                time_idle = {input.Time.Idle?.ToUnixTimeMilliseconds()}, time_viewed = {viewed},
                time_archived = {input.Time.Archived?.ToUnixTimeMilliseconds()}, idle_outcome = {outcome} WHERE id = {input.Id.Value}
            """, ct);
    }

    internal static Task ClaimExecutionAsync(PersistenceContext db, string session, double created, CancellationToken ct) =>
        db.Database.ExecuteSqlAsync($"""
            UPDATE session_v2 SET time_suspended = {created}, time_updated = time_updated
            WHERE id = {session} AND time_suspended IS NULL
            """, ct);

    internal static Task ClearCurrentRetryAsync(PersistenceContext db, string session, long updated, CancellationToken ct) =>
        db.Database.ExecuteSqlAsync($"""
            UPDATE session_message SET data = json_remove(data, '$.retry'), time_updated = {updated}
            WHERE id = (SELECT id FROM session_message WHERE session_id = {session} AND type = 'assistant' ORDER BY seq DESC LIMIT 1)
                AND json_extract(data, '$.time.completed') IS NULL AND json_type(data, '$.retry') IS NOT NULL
            """, ct);

    internal static Task CompleteExecutionAsync(PersistenceContext db, string session, double created, string outcome, bool replay, CancellationToken ct) =>
        db.Database.ExecuteSqlAsync($"""
            UPDATE session_v2 SET time_idle = max({created}, coalesce(time_idle + 1, {created})), idle_outcome = {outcome},
                time_suspended = CASE WHEN {replay} THEN time_suspended ELSE NULL END,
                resume_attempts = CASE WHEN {replay} THEN resume_attempts ELSE 0 END, time_updated = time_updated WHERE id = {session}
            """, ct);

    internal static async Task<long?> IncrementResumeAsync(PersistenceContext db, string session, CancellationToken ct)
    {
        await foreach (var row in db.Database.SqlQuery<ResumeCounterRow>($"""
            UPDATE session_v2 SET resume_attempts = resume_attempts + 1, time_updated = time_updated
            WHERE id = {session} RETURNING resume_attempts AS Attempts, typeof(resume_attempts) AS StorageType
            """).AsAsyncEnumerable().WithCancellation(ct).ConfigureAwait(true))
            // ExecuteScalar's original `is long` must not start accepting a REAL
            // value produced by SQLite arithmetic overflow through GetInt64.
            return row.StorageType == "integer" ? row.Attempts : null;
        return null;
    }

    internal static Task PutInstructionBlobAsync(PersistenceContext db, string hash, string value, CancellationToken ct) =>
        db.Database.ExecuteSqlAsync($"INSERT INTO instruction_blob (hash, value) VALUES ({hash}, {value}) ON CONFLICT DO NOTHING", ct);

    internal static Task PutInstructionStateAsync(PersistenceContext db, string session, long sequence, string values, CancellationToken ct) =>
        db.Database.ExecuteSqlAsync($"""
            INSERT INTO instruction_state (session_id, epoch_start, through_seq, initial_values, current_values)
            VALUES ({session}, {sequence}, {sequence}, {values}, {values})
            ON CONFLICT (session_id) DO UPDATE SET through_seq = excluded.through_seq, current_values = excluded.current_values
            """, ct);

    internal static Task SaveCompactionAsync(PersistenceContext db, string session, string message, string data, double created, long updated, CancellationToken ct) =>
        db.Database.ExecuteSqlAsync($"""
            UPDATE session_message SET data = {data}, time_created = {created}, time_updated = {updated}
            WHERE id = {message} AND session_id = {session}
            """, ct);

    internal static Task<int> InsertInboxAsync(PersistenceContext db, string id, string session, string type, string payload,
        string delivery, long sequence, double created, CancellationToken ct) => db.Database.ExecuteSqlAsync($"""
            INSERT INTO session_inbox (id, session_id, type, payload, delivery, enqueued_seq, time_created)
            VALUES ({id}, {session}, {type}, {payload}, {delivery}, {sequence}, {created}) ON CONFLICT DO NOTHING
            """, ct);

    internal static Task TouchSessionAsync(PersistenceContext db, string session, double created, CancellationToken ct) =>
        db.Database.ExecuteSqlAsync($"UPDATE session_v2 SET time_updated = {created} WHERE id = {session}", ct);

    internal static Task SaveAssistantAsync(PersistenceContext db, string session, string message, string data, double created, long updated, CancellationToken ct) =>
        db.Database.ExecuteSqlAsync($"""
            UPDATE session_message SET data = {data}, time_created = {created}, time_updated = {updated}
            WHERE session_id = {session} AND id = {message} AND type = 'assistant'
            """, ct);

    // Replay creation times are finite doubles, even in INTEGER-affinity
    // projection columns. Do not round them through the integral read model.
    internal static Task<int> InsertMessageAsync(PersistenceContext db, string id, string session, string type, long sequence,
        double created, double updated, string data, CancellationToken ct) => db.Database.ExecuteSqlAsync($"""
            INSERT INTO session_message (id, session_id, type, seq, time_created, time_updated, data)
            VALUES ({id}, {session}, {type}, {sequence}, {created}, {updated}, {data})
            """, ct);

    internal static Task<int> InsertSessionAsync(PersistenceContext db, string id, string project, string? workspace, string? parent,
        string slug, string directory, string? path, string? title, string? agent, string? model, string? metadata, string version,
        double created, CancellationToken ct) => db.Database.ExecuteSqlAsync($"""
            INSERT INTO session_v2 (id, project_id, workspace_id, parent_id, slug, directory, path,
                title, agent, model, metadata, version, time_created, time_updated)
            VALUES ({id}, {project}, {workspace}, {parent}, {slug}, {directory}, {path},
                {title}, {agent}, {model}, {metadata}, {version}, {created}, {created}) ON CONFLICT DO NOTHING
            """, ct);

    internal static Task<int> AddUsageAsync(PersistenceContext db, string session, double cost, double input, double output,
        double reasoning, double read, double write, CancellationToken ct) => db.Database.ExecuteSqlAsync($"""
            UPDATE session_v2 SET cost = cost + {cost}, tokens_input = tokens_input + {input}, tokens_output = tokens_output + {output},
                tokens_reasoning = tokens_reasoning + {reasoning}, tokens_cache_read = tokens_cache_read + {read},
                tokens_cache_write = tokens_cache_write + {write}, time_updated = time_updated WHERE id = {session}
            """, ct);

    internal static async Task<ConsumedInbox?> ConsumeInboxAsync(PersistenceContext db, string session, string id, CancellationToken ct)
    {
        await foreach (var row in db.Database.SqlQuery<ConsumedInbox>($"""
            DELETE FROM session_inbox WHERE id = {id} AND session_id = {session} RETURNING type, payload
            """).AsAsyncEnumerable().WithCancellation(ct).ConfigureAwait(true)) return row;
        return null;
    }

    internal static Task<int> AddPermissionAsync(PersistenceContext db, string id, string project, string action, string resource, long now, CancellationToken ct) =>
        db.Database.ExecuteSqlAsync($"""
            INSERT INTO permission (id, project_id, action, resource, time_created, time_updated)
            VALUES ({id}, {project}, {action}, {resource}, {now}, {now}) ON CONFLICT DO NOTHING
            """, ct);

    internal static Task<int> PutInstructionAsync(PersistenceContext db, string session, string key, string? value, long now, CancellationToken ct) =>
        db.Database.ExecuteSqlAsync($"""
            INSERT INTO instruction_entry (session_id, key, value, removed, time_created, time_updated)
            VALUES ({session}, {key}, {value}, 0, {now}, {now})
            ON CONFLICT (session_id, key) DO UPDATE SET value = excluded.value, removed = 0, time_updated = excluded.time_updated
            WHERE instruction_entry.removed = 1 OR instruction_entry.value IS NOT excluded.value
            """, ct);

    internal static Task ReserveSequenceAsync(PersistenceContext db, string aggregate, long sequence, CancellationToken ct) =>
        db.Database.ExecuteSqlAsync($"""
            INSERT INTO event_sequence (aggregate_id, seq) VALUES ({aggregate}, {sequence})
            ON CONFLICT (aggregate_id) DO UPDATE SET seq = max(event_sequence.seq, excluded.seq)
            """, ct);

    internal static Task ReserveReplayAsync(PersistenceContext db, string aggregate, long sequence, string? owner, bool adopt, CancellationToken ct) =>
        db.Database.ExecuteSqlAsync($"""
            INSERT INTO event_sequence (aggregate_id, seq, owner_id) VALUES ({aggregate}, {sequence}, {owner})
            ON CONFLICT (aggregate_id) DO UPDATE SET seq = max(event_sequence.seq, excluded.seq),
                owner_id = CASE WHEN {adopt} THEN excluded.owner_id ELSE event_sequence.owner_id END
            """, ct);

    internal static Task PutKvAsync(PersistenceContext db, string key, string value, long now, CancellationToken ct) =>
        db.Database.ExecuteSqlAsync($"""
            INSERT INTO kv (key, value, time_created, time_updated) VALUES ({key}, {value}, {now}, {now})
            ON CONFLICT (key) DO UPDATE SET value = excluded.value, time_updated = excluded.time_updated
            """, ct);

    internal static Task<int> PutWorktreeAsync(PersistenceContext db, string project, string directory, string? strategy, long created, CancellationToken ct) =>
        db.Database.ExecuteSqlAsync($"""
            INSERT INTO worktree(project_id, directory, strategy, time_created) VALUES ({project}, {directory}, {strategy}, {created})
            ON CONFLICT(project_id, directory) DO UPDATE SET strategy = excluded.strategy
            WHERE worktree.strategy IS NOT excluded.strategy
            """, ct);

    internal static Task PutProjectAsync(PersistenceContext db, string id, string canonical, string? vcs, long now, CancellationToken ct) =>
        db.Database.ExecuteSqlAsync($"""
            INSERT INTO project (id, worktree, vcs, sandboxes, time_created, time_updated)
            VALUES ({id}, {canonical}, {vcs}, '[]', {now}, {now})
            ON CONFLICT(id) DO UPDATE SET vcs = excluded.vcs, time_updated = excluded.time_updated
            WHERE project.vcs IS NOT excluded.vcs
            """, ct);
}

internal sealed class ConsumedInbox
{
    public string type { get; set; } = "";
    public string payload { get; set; } = "";
}

internal sealed class ResumeCounterRow
{
    public long? Attempts { get; set; }
    public string StorageType { get; set; } = "";
}
