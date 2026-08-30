namespace OpenCode.Core.Database;

using System.Text.Json;
using Microsoft.Data.Sqlite;
using OpenCode.Schema;

public sealed class SessionStore
{
    private readonly IDatabase _database;

    public SessionStore(IDatabase database)
    {
        _database = database;
    }

    public async Task<IReadOnlyList<SessionInfo>> ListSessionsAsync(int limit = 50, CancellationToken ct = default)
    {
        await using var conn = _database.CreateConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, project_id, slug, directory, time_created, time_updated, time_idle,
                   title, agent, cost, tokens_input, tokens_output, idle_outcome
            FROM session_v2
            ORDER BY time_updated DESC
            LIMIT @limit
        """;
        cmd.Parameters.AddWithValue("@limit", limit);

        var list = new List<SessionInfo>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var id = new SessionId(reader.GetString(0));
            var projectId = new ProjectId(reader.GetString(1));
            var slug = reader.GetString(2);
            var directory = reader.GetString(3);
            var timeCreated = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4));
            var timeUpdated = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(5));
            DateTimeOffset? timeIdle = reader.IsDBNull(6) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(6));

            var title = reader.IsDBNull(7) ? null : reader.GetString(7);
            var agent = reader.IsDBNull(8) ? null : reader.GetString(8);
            var cost = reader.IsDBNull(9) ? 0.0 : reader.GetDouble(9);
            var tokensIn = reader.IsDBNull(10) ? 0 : reader.GetInt64(10);
            var tokensOut = reader.IsDBNull(11) ? 0 : reader.GetInt64(11);

            SessionOutcome? outcome = null;
            if (!reader.IsDBNull(12))
            {
                Enum.TryParse<SessionOutcome>(reader.GetString(12), true, out var parsed);
                outcome = parsed;
            }

            list.Add(new SessionInfo(
                Id: id,
                ProjectId: projectId,
                Slug: slug,
                Directory: directory,
                Time: new SessionTime(timeCreated, timeUpdated, timeIdle),
                Tokens: new TokenUsageInfo(Input: tokensIn, Output: tokensOut),
                Cost: new Money(cost),
                Title: title,
                Agent: agent,
                Outcome: outcome
            ));
        }

        return list;
    }

    public async Task<ProjectId> EnsureProjectAsync(string directory, CancellationToken ct = default)
    {
        var normalizedDir = Path.GetFullPath(directory).Replace('\\', '/');
        await using var conn = _database.CreateConnection();

        await using var findCmd = conn.CreateCommand();
        findCmd.CommandText = "SELECT id FROM project WHERE worktree = @dir LIMIT 1";
        findCmd.Parameters.AddWithValue("@dir", normalizedDir);
        var existing = await findCmd.ExecuteScalarAsync(ct);
        if (existing is string existingId)
        {
            return new ProjectId(existingId);
        }

        var newId = ProjectId.Create();
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await using var insertCmd = conn.CreateCommand();
        insertCmd.CommandText = """
            INSERT INTO project (id, worktree, time_created, time_updated, sandboxes)
            VALUES (@id, @worktree, @now, @now, '[]')
        """;
        insertCmd.Parameters.AddWithValue("@id", newId.Value);
        insertCmd.Parameters.AddWithValue("@worktree", normalizedDir);
        insertCmd.Parameters.AddWithValue("@now", nowMs);
        await insertCmd.ExecuteNonQueryAsync(ct);

        return newId;
    }

    public async Task<SessionInfo> CreateSessionAsync(
        string directory,
        string? title = null,
        ProjectId? projectId = null,
        CancellationToken ct = default)
    {
        var id = SessionId.Create();
        var prjId = projectId ?? await EnsureProjectAsync(directory, ct);
        var slug = Guid.NewGuid().ToString("N")[..8];
        var now = DateTimeOffset.UtcNow;
        var nowMs = now.ToUnixTimeMilliseconds();

        await using var conn = _database.CreateConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO session_v2 (
                id, project_id, slug, directory, title, version,
                time_created, time_updated, cost, tokens_input, tokens_output,
                tokens_reasoning, tokens_cache_read, tokens_cache_write
            ) VALUES (
                @id, @projectId, @slug, @directory, @title, '2',
                @now, @now, 0, 0, 0, 0, 0, 0
            )
        """;
        cmd.Parameters.AddWithValue("@id", id.Value);
        cmd.Parameters.AddWithValue("@projectId", prjId.Value);
        cmd.Parameters.AddWithValue("@slug", slug);
        cmd.Parameters.AddWithValue("@directory", directory);
        cmd.Parameters.AddWithValue("@title", (object?)title ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@now", nowMs);

        await cmd.ExecuteNonQueryAsync(ct);

        return new SessionInfo(
            Id: id,
            ProjectId: prjId,
            Slug: slug,
            Directory: directory,
            Time: new SessionTime(now, now),
            Tokens: new TokenUsageInfo(),
            Cost: new Money(0),
            Title: title
        );
    }

    public async Task AddMessageAsync(SessionId sessionId, SessionMessage message, CancellationToken ct = default)
    {
        await using var conn = _database.CreateConnection();

        await using var seqCmd = conn.CreateCommand();
        seqCmd.CommandText = "SELECT COALESCE(MAX(seq), -1) + 1 FROM session_message WHERE session_id = @sessionId";
        seqCmd.Parameters.AddWithValue("@sessionId", sessionId.Value);
        var nextSeq = Convert.ToInt64(await seqCmd.ExecuteScalarAsync(ct));

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO session_message (id, session_id, type, seq, time_created, time_updated, data)
            VALUES (@id, @sessionId, @type, @seq, @now, @now, @data)
        """;
        cmd.Parameters.AddWithValue("@id", message.Id.Value);
        cmd.Parameters.AddWithValue("@sessionId", sessionId.Value);
        cmd.Parameters.AddWithValue("@type", message switch
        {
            UserMessage => "user",
            AssistantMessage => "assistant",
            ShellMessage => "shell",
            _ => "unknown"
        });
        cmd.Parameters.AddWithValue("@seq", nextSeq);

        var nowMs = message.Time.Created.ToUnixTimeMilliseconds();
        cmd.Parameters.AddWithValue("@now", nowMs);

        var dataJson = message switch
        {
            UserMessage u => JsonSerializer.Serialize(new { text = u.Text, time = new { created = nowMs } }),
            AssistantMessage a => JsonSerializer.Serialize(new {
                model = a.Model,
                content = a.Content,
                time = new { created = nowMs }
            }),
            _ => JsonSerializer.Serialize(message, OpenCodeJsonContext.Default.SessionMessage)
        };
        cmd.Parameters.AddWithValue("@data", dataJson);

        await cmd.ExecuteNonQueryAsync(ct);
    }
}
