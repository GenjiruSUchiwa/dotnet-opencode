namespace OpenCode.Core.Projects;

using System.Text.Json;
using OpenCode.Core.Database;
using Microsoft.EntityFrameworkCore;
using OpenCode.Core.Persistence;
using OpenCode.Schema;

public sealed class ProjectNotFoundException(ProjectId id) : Exception($"Project not found: {id.Value}")
{ public ProjectId ProjectId { get; } = id; }

/// <summary>Project display producer rows; project.updated is ephemeral and published only after commit.</summary>
public sealed class ProjectMutations(IDatabase database, Action<OpenCodeEvent> publish)
{
    public async Task<ProjectInfo> UpdateAsync(ProjectId id, string? name = null, ProjectIcon? icon = null,
        ProjectCommands? commands = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(publish);
        ArgumentNullException.ThrowIfNull(id.Value);
        var iconOverride = icon?.Override;
        var color = icon?.Color;
        var start = commands?.Start;
        var project = await database.RunInTransactionAsync(async (connection, transaction) =>
        {
            await using var db = new PersistenceContext(connection, transaction);
            var storedName = string.IsNullOrEmpty(name) ? null : name;
            var storedOverride = string.IsNullOrEmpty(iconOverride) ? null : iconOverride;
            var storedColor = string.IsNullOrEmpty(color) ? null : color;
            var storedCommands = string.IsNullOrEmpty(start) ? null : JsonSerializer.Serialize(new ProjectCommands(start), OpenCodeJsonContext.Default.ProjectCommands);
            var now = database.Clock.GetUtcNow().ToUnixTimeMilliseconds();
            if (await db.Set<ProjectRow>().Where(row => row.id == id.Value).ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.name, row => name != null ? storedName : row.name)
                .SetProperty(row => row.icon_url_override, row => iconOverride != null ? storedOverride : row.icon_url_override)
                .SetProperty(row => row.icon_color, row => color != null ? storedColor : row.icon_color)
                .SetProperty(row => row.commands, row => start != null ? storedCommands : row.commands)
                .SetProperty(row => row.time_updated, now), ct) != 1) throw new ProjectNotFoundException(id);
            return ProjectQueries.FromRow(await db.Set<ProjectRow>().FirstAsync(row => row.id == id.Value, ct));
        }, ct);
        // The generated icon URL, canonical directory, VCS and sandbox fields are
        // not writable here. A publication failure cannot roll back a committed row.
        publish(ProjectEventDefinitions.Updated.Create(EventId.Create(), database.Clock.GetUtcNow().ToUnixTimeMilliseconds(), project));
        return project;
    }
}
