namespace OpenCode.Core.Integrations.Wellknown;

using System.Text.Json;
using OpenCode.Core.Database;
using Microsoft.EntityFrameworkCore;
using OpenCode.Core.Persistence;

/// <summary>Source's single ordered KV registry in the host-selected database. No legacy import or config-file writes.</summary>
public sealed class WellknownSourceStore(IDatabase database)
{
    public const string Key = "wellknown:sources";

    public async Task<string[]> ReadAsync(CancellationToken ct = default)
    {
        var connection = database.CreateConnection();
        await using var connectionLifetime = connection.ConfigureAwait(true);
        var db = new PersistenceContext(connection);
        await using var dbLifetime = db.ConfigureAwait(true);
        return Decode(await db.Set<KvRow>().Where(row => row.key == Key).Select(row => row.value).FirstOrDefaultAsync(ct).ConfigureAwait(true));
    }

    public Task<string[]> AddAsync(string origin, CancellationToken ct = default) => MutateAsync(
        origins => origins.Append(origin).Distinct(StringComparer.Ordinal).ToArray(), ct);
    public Task<string[]> RemoveAsync(string origin, CancellationToken ct = default) => MutateAsync(
        origins => origins.Where(item => item != origin).ToArray(), ct);

    private Task<string[]> MutateAsync(Func<string[], string[]> change, CancellationToken ct) => database.RunInTransactionAsync(async (connection, transaction) =>
    {
        var db = new PersistenceContext(connection, transaction);
        await using var dbLifetime = db.ConfigureAwait(true);
        var origins = change(Decode(await db.Set<KvRow>().Where(row => row.key == Key).Select(row => row.value).FirstOrDefaultAsync(ct).ConfigureAwait(true)));
        await SqliteIntrinsics.PutKvAsync(db, Key, JsonSerializer.Serialize(origins, WellknownJsonContext.Default.StringArray),
            database.Clock.GetUtcNow().ToUnixTimeMilliseconds(), ct).ConfigureAwait(true);
        return origins;
    }, ct);

    private static string[] Decode(string? text)
    {
        if (text is null) return [];
        var values = JsonSerializer.Deserialize(text, WellknownJsonContext.Default.StringArray);
        // Do not repair a corrupt registry by discarding the user's existing sources.
        if (values is null || values.Any(value => value is null)) throw new JsonException("Invalid wellknown source registry; it was not overwritten.");
        return values;
    }
}
