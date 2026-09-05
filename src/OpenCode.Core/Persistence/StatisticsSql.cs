namespace OpenCode.Core.Persistence;

using Microsoft.EntityFrameworkCore;

/// <summary>SQLite JSON table expansion and materialized aggregation, retained verbatim in shape.</summary>
internal static class StatisticsSql
{
    internal static Task<List<ToolSummaryRow>> SummaryAsync(PersistenceContext db, string? project, double from, double to, CancellationToken ct) =>
        db.Database.SqlQuery<ToolSummaryRow>($"""
            WITH calls AS MATERIALIZED (
              SELECT json_extract(content.value, '$.state.status') AS status
              FROM session_message AS message JOIN session_v2 AS session ON session.id = message.session_id,
                json_each(message.data, '$.content') AS content
              WHERE message.type = 'assistant' AND message.time_created >= {from} AND message.time_created < {to}
                AND (session.fork_session_id IS NULL OR message.time_created >= session.time_created)
                AND json_extract(content.value, '$.type') = 'tool'
                AND ({project} IS NULL OR session.project_id = {project})
            )
            SELECT count(*) AS Calls, count(*) FILTER (WHERE status = 'completed') AS Succeeded,
              count(*) FILTER (WHERE status = 'error') AS Failed,
              count(*) FILTER (WHERE status IS NULL OR status NOT IN ('completed', 'error')) AS Unfinished FROM calls
            """).ToListAsync(ct);

    internal static Task<List<ToolDetailRow>> DetailAsync(PersistenceContext db, string? project, double from, double to, CancellationToken ct) =>
        db.Database.SqlQuery<ToolDetailRow>($"""
            SELECT json_extract(content.value, '$.name') AS Name, json_extract(content.value, '$.state.status') AS Status,
              CASE WHEN json_extract(content.value, '$.time.completed') IS NULL THEN NULL
              ELSE json_extract(content.value, '$.time.completed')
                - coalesce(json_extract(content.value, '$.time.ran'), json_extract(content.value, '$.time.created')) END AS Duration
            FROM session_message AS message JOIN session_v2 AS session ON session.id = message.session_id,
              json_each(message.data, '$.content') AS content
            WHERE message.type = 'assistant' AND message.time_created >= {from} AND message.time_created < {to}
              AND (session.fork_session_id IS NULL OR message.time_created >= session.time_created)
              AND json_extract(content.value, '$.type') = 'tool'
              AND ({project} IS NULL OR session.project_id = {project})
            """).ToListAsync(ct);
}

internal sealed class ToolSummaryRow
{
    public int Calls { get; set; }
    public int Succeeded { get; set; }
    public int Failed { get; set; }
    public int Unfinished { get; set; }
}

internal sealed class ToolDetailRow
{
    public string? Name { get; set; }
    public string? Status { get; set; }
    public double? Duration { get; set; }
}
