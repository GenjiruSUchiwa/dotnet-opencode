namespace OpenCode.Core.Permissions;

using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using OpenCode.Core.Persistence;
using OpenCode.Core.Database;
using OpenCode.Schema;

/// <summary>The authenticated host's saved-approval catalog, distinct from pending permission requests.
/// Listing without a project and removal by ID follow PermissionSaved.Service's global administration API.</summary>
public interface IPermissionSavedStore : IPermissionGrantStore
{
    ValueTask<IReadOnlyList<PermissionSavedInfo>> ListSavedAsync(ProjectId? projectId = null, CancellationToken ct = default);
    ValueTask RemoveAsync(PermissionSavedId id, CancellationToken ct = default);
}

/// <summary>Saved allow rules in the existing permission table. The host supplies its authoritative durable
/// database; this store never chooses a database path, bootstraps a schema, or falls back to memory.</summary>
public sealed class SqlitePermissionGrantStore : IPermissionSavedStore
{
    // Multiple Location permission services (or store adapters) can share one database. Their individual
    // pending-request gates do not serialize grant transactions, so share one gate per database object.
    private static readonly ConditionalWeakTable<IDatabase, SemaphoreSlim> Gates = new();
    private readonly IDatabase _database;
    private readonly SemaphoreSlim _gate;
    public bool Persistent => true;

    public SqlitePermissionGrantStore(IDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _gate = Gates.GetValue(database, _ => new SemaphoreSlim(1, 1));
    }

    public async ValueTask<IReadOnlyList<PermissionRule>> ListAsync(string projectId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(projectId);
        return (await ListSavedAsync(ProjectId.FromExisting(projectId), ct))
            .Select(item => new PermissionRule(item.Action, item.Resource, PermissionEffect.Allow)).ToArray();
    }

    public async ValueTask<IReadOnlyList<PermissionSavedInfo>> ListSavedAsync(ProjectId? projectId = null, CancellationToken ct = default)
    {
        if (projectId is { } project && !project.IsInitialized()) throw new ArgumentException("Project ID must be an initialized string.", nameof(projectId));
        await _gate.WaitAsync(ct);
        try
        {
            ct.ThrowIfCancellationRequested();
            await using var connection = _database.CreateConnection();
            await using var db = new PersistenceContext(connection);
            var projectValue = projectId?.Value;
            var rows = await db.Set<PermissionRow>().Where(row => projectValue == null || row.project_id == EF.Functions.Collate(projectValue, "BINARY"))
                .Select(row => new { row.id, row.project_id, row.action, row.resource }).ToListAsync(ct);
            return Array.AsReadOnly(rows.Select(row => new PermissionSavedInfo(PermissionSavedId.FromExisting(row.id),
                ProjectId.FromExisting(row.project_id), row.action, row.resource)).ToArray());
        }
        finally { _gate.Release(); }
    }

    public async ValueTask AddAsync(string projectId, string action, IReadOnlyList<string> resources, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(projectId);
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(resources);
        var saved = resources.ToArray();
        if (saved.Any(resource => resource is null)) throw new ArgumentException("Saved permission resources must be strings.", nameof(resources));
        ct.ThrowIfCancellationRequested();
        if (saved.Length == 0) return;
        await _gate.WaitAsync(ct);
        try
        {
            ct.ThrowIfCancellationRequested();
            await _database.RunInTransactionAsync(async (connection, transaction) =>
            {
                await using var db = new PersistenceContext(connection, transaction);
                var now = _database.Clock.GetUtcNow().ToUnixTimeMilliseconds();
                foreach (var value in saved)
                {
                    ct.ThrowIfCancellationRequested();
                    await SqliteIntrinsics.AddPermissionAsync(db, PermissionSavedId.Create().Value, projectId, action, value, now, ct);
                }
                // Commit the whole batch before PermissionService completes an "always" reply.
                return true;
            }, ct);
        }
        finally { _gate.Release(); }
    }

    public async ValueTask RemoveAsync(PermissionSavedId id, CancellationToken ct = default)
    {
        if (!id.IsInitialized()) throw new ArgumentException("Saved permission ID must be initialized.", nameof(id));
        await _gate.WaitAsync(ct);
        try
        {
            ct.ThrowIfCancellationRequested();
            await _database.RunInTransactionAsync(async (connection, transaction) =>
            {
                await using var db = new PersistenceContext(connection, transaction);
                return await db.Set<PermissionRow>().Where(row => row.id == EF.Functions.Collate(id.Value, "BINARY")).ExecuteDeleteAsync(ct);
            }, ct);
        }
        finally { _gate.Release(); }
    }
}
