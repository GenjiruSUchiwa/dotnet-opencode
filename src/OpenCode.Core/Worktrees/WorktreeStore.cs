namespace OpenCode.Core.Worktrees;

using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OpenCode.Core.Persistence;
using OpenCode.Core.Database;
using OpenCode.Core.Projects;
using OpenCode.Schema;

internal sealed class WorktreeStore(IDatabase database)
{
    internal async Task<IReadOnlyList<WorktreeDirectory>> ListAsync(ProjectId project, CancellationToken ct)
    {
        var connection = database.CreateConnection();
        await using var connectionLifetime = connection.ConfigureAwait(true);
        var db = new PersistenceContext(connection);
        await using var dbLifetime = db.ConfigureAwait(true);
        var id = project.Value;
        return (await db.Set<WorktreeRow>().Where(row => row.project_id == id).OrderByDescending(row => row.time_created)
            .ThenBy(row => row.directory).Select(row => new { row.directory, row.strategy }).ToListAsync(ct).ConfigureAwait(true))
            .Select(row => new WorktreeDirectory(ProjectPaths.Platform(row.directory), row.strategy)).ToArray();
    }

    internal async Task<WorktreeDirectory?> FindAsync(ProjectId project, string directory, CancellationToken ct)
    {
        var connection = database.CreateConnection();
        await using var connectionLifetime = connection.ConfigureAwait(true);
        var db = new PersistenceContext(connection);
        await using var dbLifetime = db.ConfigureAwait(true);
        var stored = ProjectPaths.Storage(directory);
        var id = project.Value;
        var row = await db.Set<WorktreeRow>().Where(row => row.project_id == id && row.directory == stored)
            .Select(row => new { row.directory, row.strategy }).FirstOrDefaultAsync(ct).ConfigureAwait(true);
        return row is null ? null : new(ProjectPaths.Platform(row.directory), row.strategy);
    }

    internal async Task<string?> PrimaryAsync(ProjectId project, CancellationToken ct)
    {
        var connection = database.CreateConnection();
        await using var connectionLifetime = connection.ConfigureAwait(true);
        var db = new PersistenceContext(connection);
        await using var dbLifetime = db.ConfigureAwait(true);
        var id = project.Value;
        return await db.Set<ProjectRow>().Where(row => row.id == id).Select(row => row.worktree).FirstOrDefaultAsync(ct).ConfigureAwait(true) is { } path
            ? ProjectPaths.Platform(path) : null;
    }

    internal async Task<string?> StartupAsync(ProjectId project, CancellationToken ct)
    {
        var connection = database.CreateConnection();
        await using var connectionLifetime = connection.ConfigureAwait(true);
        var db = new PersistenceContext(connection);
        await using var dbLifetime = db.ConfigureAwait(true);
        var id = project.Value;
        return await db.Set<ProjectRow>().Where(row => row.id == id).Select(row => row.commands).FirstOrDefaultAsync(ct).ConfigureAwait(true) is { } json
            ? JsonSerializer.Deserialize(json, OpenCodeJsonContext.Default.ProjectCommands)?.Start : null;
    }

    internal Task<bool> PutAsync(ProjectId project, string directory, string? strategy, CancellationToken ct) =>
        database.RunInTransactionAsync((connection, transaction) => PutAsync(connection, transaction, project, directory, strategy, ct, database.Clock), ct);
    internal Task<bool> RemoveAsync(ProjectId project, string directory, CancellationToken ct) =>
        database.RunInTransactionAsync((connection, transaction) => RemoveAsync(connection, transaction, project, directory, ct), ct);

    internal static async Task<bool> PutAsync(SqliteConnection connection, SqliteTransaction transaction, ProjectId project, string directory,
        string? strategy, CancellationToken ct, TimeProvider clock)
    {
        var db = new PersistenceContext(connection, transaction);
        await using var dbLifetime = db.ConfigureAwait(true);
        return await SqliteIntrinsics.PutWorktreeAsync(db, project.Value, ProjectPaths.Storage(directory), strategy,
            clock.GetUtcNow().ToUnixTimeMilliseconds(), ct).ConfigureAwait(true) != 0;
    }

    internal static async Task<bool> RemoveAsync(SqliteConnection connection, SqliteTransaction transaction, ProjectId project, string directory, CancellationToken ct)
    {
        var db = new PersistenceContext(connection, transaction);
        await using var dbLifetime = db.ConfigureAwait(true);
        var stored = ProjectPaths.Storage(directory);
        var id = project.Value;
        return await db.Set<WorktreeRow>().Where(row => row.project_id == id && row.directory == stored).ExecuteDeleteAsync(ct).ConfigureAwait(true) != 0;
    }
}
