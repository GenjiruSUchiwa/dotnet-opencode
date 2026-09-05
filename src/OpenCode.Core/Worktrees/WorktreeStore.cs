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
        await using var connection = database.CreateConnection();
        await using var db = new PersistenceContext(connection);
        return (await db.Set<WorktreeRow>().Where(row => row.project_id == project.Value).OrderByDescending(row => row.time_created)
            .ThenBy(row => row.directory).Select(row => new { row.directory, row.strategy }).ToListAsync(ct))
            .Select(row => new WorktreeDirectory(ProjectPaths.Platform(row.directory), row.strategy)).ToArray();
    }

    internal async Task<WorktreeDirectory?> FindAsync(ProjectId project, string directory, CancellationToken ct)
    {
        await using var connection = database.CreateConnection();
        await using var db = new PersistenceContext(connection);
        var stored = ProjectPaths.Storage(directory);
        var row = await db.Set<WorktreeRow>().Where(row => row.project_id == project.Value && row.directory == stored)
            .Select(row => new { row.directory, row.strategy }).FirstOrDefaultAsync(ct);
        return row is null ? null : new(ProjectPaths.Platform(row.directory), row.strategy);
    }

    internal async Task<string?> PrimaryAsync(ProjectId project, CancellationToken ct)
    {
        await using var connection = database.CreateConnection();
        await using var db = new PersistenceContext(connection);
        return await db.Set<ProjectRow>().Where(row => row.id == project.Value).Select(row => row.worktree).FirstOrDefaultAsync(ct) is { } path
            ? ProjectPaths.Platform(path) : null;
    }

    internal async Task<string?> StartupAsync(ProjectId project, CancellationToken ct)
    {
        await using var connection = database.CreateConnection();
        await using var db = new PersistenceContext(connection);
        return await db.Set<ProjectRow>().Where(row => row.id == project.Value).Select(row => row.commands).FirstOrDefaultAsync(ct) is { } json
            ? JsonSerializer.Deserialize(json, OpenCodeJsonContext.Default.ProjectCommands)?.Start : null;
    }

    internal Task<bool> PutAsync(ProjectId project, string directory, string? strategy, CancellationToken ct) =>
        database.RunInTransactionAsync((connection, transaction) => PutAsync(connection, transaction, project, directory, strategy, ct, database.Clock), ct);
    internal Task<bool> RemoveAsync(ProjectId project, string directory, CancellationToken ct) =>
        database.RunInTransactionAsync((connection, transaction) => RemoveAsync(connection, transaction, project, directory, ct), ct);

    internal static async Task<bool> PutAsync(SqliteConnection connection, SqliteTransaction transaction, ProjectId project, string directory,
        string? strategy, CancellationToken ct, TimeProvider clock)
    {
        await using var db = new PersistenceContext(connection, transaction);
        return await SqliteIntrinsics.PutWorktreeAsync(db, project.Value, ProjectPaths.Storage(directory), strategy,
            clock.GetUtcNow().ToUnixTimeMilliseconds(), ct) != 0;
    }

    internal static async Task<bool> RemoveAsync(SqliteConnection connection, SqliteTransaction transaction, ProjectId project, string directory, CancellationToken ct)
    {
        await using var db = new PersistenceContext(connection, transaction);
        var stored = ProjectPaths.Storage(directory);
        return await db.Set<WorktreeRow>().Where(row => row.project_id == project.Value && row.directory == stored).ExecuteDeleteAsync(ct) != 0;
    }
}
