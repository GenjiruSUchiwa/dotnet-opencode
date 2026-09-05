namespace OpenCode.Core.Projects;

using System.Text.Json;
using OpenCode.Core.Database;
using Microsoft.EntityFrameworkCore;
using OpenCode.Core.Persistence;
using OpenCode.Schema;

/// <summary>Source project.ts list/fromRow projection over the host's existing database.</summary>
public sealed class ProjectQueries(IDatabase database)
{
    public async Task<IReadOnlyList<ProjectInfo>> ListAsync(CancellationToken ct = default)
    {
        await using var connection = database.CreateConnection();
        await using var db = new PersistenceContext(connection);
        return (await db.Set<ProjectRow>().OrderByDescending(row => row.time_updated).ThenBy(row => row.id).ToListAsync(ct)).Select(FromRow).ToArray();
    }

    internal static ProjectInfo FromRow(ProjectRow row)
    {
        using var sandboxes = JsonDocument.Parse(row.sandboxes);
        return new(ProjectId.FromExisting(row.id), ProjectPaths.Platform(row.worktree), new(row.time_created, row.time_updated, row.time_initialized),
            sandboxes.RootElement.EnumerateArray().Select(value => ProjectPaths.Platform(value.GetString() ?? throw new JsonException("Project sandbox paths must be strings."))).ToArray(),
            row.vcs, row.name, new[] { row.icon_url, row.icon_url_override, row.icon_color }.Any(value => !string.IsNullOrEmpty(value))
                ? new ProjectIcon(row.icon_url, row.icon_url_override, row.icon_color) : null,
            row.commands is null ? null : JsonSerializer.Deserialize(row.commands, OpenCodeJsonContext.Default.ProjectCommands));
    }

}
