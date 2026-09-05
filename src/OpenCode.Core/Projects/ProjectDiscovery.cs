namespace OpenCode.Core.Projects;

using System.Text.Json;
using OpenCode.Core.Database;
using Microsoft.EntityFrameworkCore;
using OpenCode.Core.Persistence;
using OpenCode.Core.Event;
using OpenCode.Core.Locations;
using OpenCode.Core.Worktrees;
using OpenCode.Schema;

/// <summary>Project.resolve's durable worktree announcement over the existing local identity resolver.</summary>
public static class ProjectDiscovery
{
    private static readonly OpenCode.Core.Event.DurableEventDefinition<WorktreeResolvedEventData> Resolved =
        new("worktree.resolved", 1, "projectID", OpenCodeJsonContext.Default.WorktreeResolvedEventData, ProjectAsync);

    public static async Task<LocationInfo> ResolveAsync(IDatabase database, string? directory = null, string? workspace = null, CancellationToken ct = default)
    {
        var location = await CatalogLocation.ResolveIdentityAsync(database, directory, workspace, ct).ConfigureAwait(true);
        await RecordAsync(database, location, ct).ConfigureAwait(true);
        return location;
    }

    public static async Task RecordAsync(IDatabase database, LocationInfo location, CancellationToken ct = default)
    {
        if (location.WorkspaceId is not null) throw new NotSupportedException("Worktree discovery requires implicit-local placement.");
        await WorktreeService.RequirePluginsAsync(location.Directory, ct).ConfigureAwait(true);
        var root = location.Project.Directory;
        var git = WorktreePaths.Exists(Path.Combine(root, ".git"));
        var hg = !git && WorktreePaths.Exists(Path.Combine(root, ".hg"));
        if (!git && !hg) return;
        var cache = git ? (await new GitWorktreeStrategy().DiscoverAsync(root, ct).ConfigureAwait(true)).CommonDirectory : Path.Combine(root, ".hg");
        var previous = "global";
        try
        {
            var text = (await File.ReadAllTextAsync(Path.Combine(cache, "opencode"), ct).ConfigureAwait(true)).Trim();
            if (text.Length > 0) previous = text;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        await ReconcileCanonicalAsync(database, location, ct).ConfigureAwait(true);
        var directories = new[] { new WorktreeDirectory(location.Project.Canonical) }
            .Concat(root == location.Project.Canonical ? [] : new[] { new WorktreeDirectory(root, git ? "git" : null) });
        foreach (var item in directories)
        {
            var adopted = await MarkerlessProjectsAsync(database, location.Project.Id, item.Directory, ct).ConfigureAwait(true);
            await new EventStore(database).TransactAsync(location.Project.Id.Value, async (transaction, token) =>
            {
                var directory = ProjectPaths.Storage(item.Directory);
                if (await transaction.Db.Set<WorktreeRow>().AnyAsync(row => row.project_id == location.Project.Id.Value && row.directory == directory, token).ConfigureAwait(true)) return false;
                await transaction.AppendAsync(Resolved, new WorktreeResolvedEventData(location.Project.Id, item.Directory, ProjectId.FromExisting(previous),
                    adopted.Count == 0 ? null : adopted), token).ConfigureAwait(true);
                // Classification is a producer-row fact, not a new field added to
                // the source durable event. It commits with that announcement.
                await SqliteIntrinsics.AddWorktreeAsync(transaction.Db, location.Project.Id.Value, directory, item.Strategy,
                    database.Clock.GetUtcNow().ToUnixTimeMilliseconds(), token).ConfigureAwait(true);
                return true;
            }, ct).ConfigureAwait(true);
        }
    }

    private static async Task ReconcileCanonicalAsync(IDatabase database, LocationInfo location, CancellationToken ct)
    {
        var stored = await new WorktreeStore(database).PrimaryAsync(location.Project.Id, ct).ConfigureAwait(true);
        if (stored is null || stored == location.Project.Canonical) return;
        try { if (WorktreePaths.Exists(stored)) return; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return; }
        var project = await database.RunInTransactionAsync(async (connection, transaction) =>
        {
            var db = new PersistenceContext(connection, transaction);
            await using var dbLifetime = db.ConfigureAwait(true);
            var canonical = ProjectPaths.Storage(location.Project.Canonical);
            var updated = database.Clock.GetUtcNow().ToUnixTimeMilliseconds();
            await db.Set<ProjectRow>().Where(row => row.id == location.Project.Id.Value).ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.worktree, canonical).SetProperty(row => row.time_updated, updated), ct).ConfigureAwait(true);
            var row = await db.Set<ProjectRow>().FirstOrDefaultAsync(row => row.id == location.Project.Id.Value, ct).ConfigureAwait(true);
            return row is null ? null : ProjectQueries.FromRow(row);
        }, ct).ConfigureAwait(true);
        if (project is not null) SessionEvents.Notify(ProjectEventDefinitions.Updated.Create(EventId.Create(), database.Clock.GetUtcNow().ToUnixTimeMilliseconds(), project), true);
    }

    private static async Task<IReadOnlyList<ProjectId>> MarkerlessProjectsAsync(IDatabase database, ProjectId project, string directory, CancellationToken ct)
    {
        var connection = database.CreateConnection();
        await using var connectionLifetime = connection.ConfigureAwait(true);
        var db = new PersistenceContext(connection);
        await using var dbLifetime = db.ConfigureAwait(true);
        var start = ProjectPaths.Storage(directory);
        var end = start + "\uffff";
        var candidates = (await db.Set<ProjectRow>().Where(row => row.vcs == null && string.Compare(row.worktree, start) >= 0 && string.Compare(row.worktree, end) <= 0)
            .Select(row => new { row.id, row.worktree }).ToListAsync(ct).ConfigureAwait(true)).Select(row => (Id: ProjectId.FromExisting(row.id), Directory: ProjectPaths.Platform(row.worktree))).ToArray();
        var adopted = new List<ProjectId>();
        foreach (var item in candidates)
        {
            if (item.Id == project || !Contains(directory, item.Directory)) continue;
            for (var current = new DirectoryInfo(item.Directory); current is not null && Contains(directory, current.FullName); current = current.Parent)
            {
                if (!WorktreePaths.Exists(Path.Combine(current.FullName, ".git")) && !WorktreePaths.Exists(Path.Combine(current.FullName, ".hg"))) continue;
                if (WorktreePaths.Canonical(current.FullName) == directory) adopted.Add(item.Id);
                break;
            }
        }
        return adopted;
    }

    private static async Task ProjectAsync(EventTransaction transaction, OpenCodeEvent committed, CancellationToken ct)
    {
        var data = committed.Data.Deserialize(OpenCodeJsonContext.Default.WorktreeResolvedEventData)!;
        var candidates = new[] { data.Previous, ProjectId.FromExisting("global") }.Concat(data.Adopted ?? []).Where(id => id != data.ProjectId).Distinct().ToArray();
        if (candidates.Length == 0) return;
        var storedRows = await transaction.Db.Sessions.Where(row => row.workspace_id == null)
            .Join(transaction.Db.Set<ProjectRow>(), session => session.project_id, project => project.id,
                (session, project) => new { session.id, session.directory, session.project_id, project.worktree }).ToListAsync(ct).ConfigureAwait(true);
        var rows = new List<(SessionId Id, string Directory)>();
        foreach (var row in storedRows)
            {
                var project = ProjectId.FromExisting(row.project_id);
                if (!candidates.Contains(project)) continue;
                var adopted = data.Adopted?.Contains(project) == true;
                var stored = row.directory;
                if (!adopted && (string.CompareOrdinal(stored, ProjectPaths.Storage(data.Directory)) < 0 || string.CompareOrdinal(stored, ProjectPaths.Storage(data.Directory) + "\uffff") > 0)) continue;
                var directory = adopted ? ProjectPaths.Platform(row.worktree) : Path.GetFullPath(ProjectPaths.Platform(stored));
                if (Contains(data.Directory, directory)) rows.Add((SessionId.FromExisting(row.id), directory));
            }
        // Source adoption changes identity/subpath only. It is not a Session move,
        // activity update, transcript rewrite, instruction reset, or interruption.
        foreach (var row in rows)
        {
            var relative = Path.GetRelativePath(data.Directory, row.Directory).Replace('\\', '/');
            var path = relative == "." ? "" : relative;
            await transaction.Db.Sessions.Where(session => session.id == row.Id.Value).ExecuteUpdateAsync(setters => setters
                .SetProperty(session => session.project_id, data.ProjectId.Value).SetProperty(session => session.path, path), ct).ConfigureAwait(true);
        }
    }

    private static bool Contains(string parent, string child)
    {
        var relative = Path.GetRelativePath(parent, child);
        return relative == "." || (!Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal));
    }
}
