namespace OpenCode.Core.Event;

using System.Text.Json;
using OpenCode.Core.Database;
using Microsoft.EntityFrameworkCore;
using OpenCode.Core.Persistence;
using OpenCode.Core.Jobs;
using OpenCode.Core.Session;
using OpenCode.Schema;

public sealed class JobBackgroundConflictException(MessageId notificationId, string detail)
    : InvalidOperationException($"Background notification {notificationId}: {detail}")
{
    public MessageId NotificationId { get; } = notificationId;
}

/// <summary>Source Job background KV records in the existing channel database. No event family or memory fallback.</summary>
public sealed class JobBackgroundStore(IDatabase database) : IJobBackgroundStore
{
    public async Task<IReadOnlyList<JobBackground>> ListAsync(CancellationToken ct)
    {
        await using var connection = database.CreateConnection();
        await using var db = new PersistenceContext(connection);
        var rows = await db.Set<KvRow>().Where(row => string.Compare(row.key, JobBackground.Prefix) >= 0 && string.Compare(row.key, "job.background0") < 0)
            .OrderBy(row => row.key).Select(row => new { row.key, row.value }).ToListAsync(ct);
        var result = new List<JobBackground>();
        foreach (var row in rows)
            if (Decode(row.key, row.value) is { } marker) result.Add(marker);
        return result;
    }

    public Task SaveAsync(JobBackground value, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(value);
        value.Validate();
        var encoded = JsonSerializer.Serialize(value, JobJsonContext.Default.JobBackground);
        var owner = value.Recovery.OwnerSessionId;
        return SessionRunCoordinator.AdmitAsync(owner, () => new EventStore(database).TransactAsync(owner.Value, async (transaction, token) =>
        {
            var stored = await transaction.Db.Set<KvRow>().Where(row => row.key == value.Key).Select(row => row.value).FirstOrDefaultAsync(token);
            if (stored is not null)
            {
                var prior = Decode(value.Key, stored) ?? throw new JobBackgroundConflictException(value.NotificationId, "the existing marker is malformed or has a different key identity.");
                RequireIdentity(prior, value);
            }
            await SqliteIntrinsics.PutKvAsync(transaction.Db, value.Key, encoded, database.Clock.GetUtcNow().ToUnixTimeMilliseconds(), token);
            return true;
        }, ct), ct);
    }

    public Task RemoveAsync(MessageId notificationId, CancellationToken ct) => RemoveAsync(notificationId, null, ct);

    internal async Task RemoveAsync(MessageId notificationId, SessionId? expectedOwner, CancellationToken ct)
    {
        _ = MessageId.FromExisting(notificationId.Value);
        var key = JobBackground.Prefix + notificationId.Value;
        JobBackground observed;
        await using (var connection = database.CreateConnection())
        await using (var db = new PersistenceContext(connection))
        {
            if (await db.Set<KvRow>().Where(row => row.key == key).Select(row => row.value).FirstOrDefaultAsync(ct) is not { } stored) return;
            observed = Decode(key, stored) ?? throw new JobBackgroundConflictException(notificationId, "the marker is malformed or has a different key identity.");
        }
        var owner = observed.Recovery.OwnerSessionId;
        if (expectedOwner is { } expected && expected != owner)
            throw new JobBackgroundConflictException(notificationId, "the marker belongs to a different Session.");
        await SessionRunCoordinator.AdmitAsync(owner, () => new EventStore(database).TransactAsync(owner.Value, async (transaction, token) =>
        {
            if (await transaction.Db.Set<KvRow>().Where(row => row.key == key).Select(row => row.value).FirstOrDefaultAsync(token) is not { } stored) return false;
            var marker = Decode(key, stored) ?? throw new JobBackgroundConflictException(notificationId, "the marker changed to malformed or mismatched data.");
            RequireIdentity(observed, marker);
            await transaction.Db.Set<KvRow>().Where(row => row.key == key).ExecuteDeleteAsync(token);
            return true;
        }, ct), ct);
    }

    private static JobBackground? Decode(string key, string json)
    {
        try
        {
            var value = JsonSerializer.Deserialize(json, JobJsonContext.Default.JobBackground);
            if (value is null || value.Key != key) return null;
            value.Validate();
            return value;
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            // Source pendingBackground skips invalid descriptors. Keep the original row for its owner/diagnostics.
            return null;
        }
    }

    private static void RequireIdentity(JobBackground prior, JobBackground current)
    {
        if (prior.NotificationId != current.NotificationId || prior.Id != current.Id || prior.Recovery != current.Recovery)
            throw new JobBackgroundConflictException(current.NotificationId, "the notification ID cannot be reassigned to a different job or recovery owner.");
    }
}
