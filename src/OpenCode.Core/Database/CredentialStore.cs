namespace OpenCode.Core.Database;

using System.Text.Json;
using System.Collections.Immutable;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OpenCode.Core.Persistence;
using OpenCode.Schema;

public abstract record CredentialNotification
{
    private protected CredentialNotification() { }
    public sealed record Updated : CredentialNotification;
    public sealed record Switched(string IntegrationId, string? CredentialId) : CredentialNotification;
}

// Publish these notifications only after this committed result is returned. The
// store deliberately does not invent a bus or hide publication inside a DB transaction.
public sealed record CredentialMutation(StoredCredential? Credential, ImmutableArray<CredentialNotification> Notifications);

public sealed record StoredCredential(
    string Id,
    string IntegrationId,
    string Label,
    string ValueJson,
    bool Active
)
{
    // Active remains a boolean for shipped callers. Selection must retain SQLite's
    // distinct NULL/false/true ordering rather than treating this flag as enabled.
    public bool? StoredActive { get; init; } = Active;
    internal JsonElement Value { get; init; }
}

public sealed class CredentialStore
{
    private readonly IDatabase _database;
    public TimeProvider Clock => _database.Clock;

    public CredentialStore(IDatabase database)
    {
        _database = database;
    }

    public Task<IReadOnlyList<StoredCredential>> ListCredentialsAsync(CancellationToken ct = default) =>
        ReadCredentialsAsync(null, ct);

    public Task<IReadOnlyList<StoredCredential>> ListCredentialsForIntegrationAsync(string integrationId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(integrationId);
        return ReadCredentialsAsync(integrationId, ct);
    }

    public async Task<StoredCredential?> GetActiveCredentialAsync(string integrationId, CancellationToken ct = default)
    {
        var credentials = await ListCredentialsForIntegrationAsync(integrationId, ct);
        // Integration.resolveConnections reverses Credential.list and selects [0].
        return credentials.LastOrDefault();
    }

    public async Task<StoredCredential?> GetCredentialAsync(string id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await using var connection = _database.CreateConnection();
        return await ReadOneAsync(connection, null, id, ct);
    }

    public Task<CredentialMutation> CreateAsync(string integrationId, JsonElement value, string? label = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(integrationId);
        ct.ThrowIfCancellationRequested();
        var parsed = ParseValue(value.GetRawText());
        var id = CredentialId.Create().Value;
        return _database.RunInTransactionAsync(async (connection, transaction) =>
        {
            await using var db = new PersistenceContext(connection, transaction);
            var now = Clock.GetUtcNow().ToUnixTimeMilliseconds();
            await db.Set<CredentialRow>().Where(row => row.integration_id == integrationId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.active, (long?)0).SetProperty(row => row.time_updated, now), ct);
            await db.InsertAsync(new CredentialRow { id = id, integration_id = integrationId, label = label ?? "default",
                value = parsed.GetRawText(), active = 1, time_created = now, time_updated = now }, ct);
            return new CredentialMutation(new StoredCredential(id, integrationId, label ?? "default", parsed.GetRawText(), true)
            { Value = parsed }, [new CredentialNotification.Updated(), new CredentialNotification.Switched(integrationId, id)]);
        }, ct);
    }

    public Task<CredentialMutation> ActivateAsync(string id, CancellationToken ct = default) =>
        _database.RunInTransactionAsync(async (connection, transaction) =>
        {
            var credential = await ReadOneAsync(connection, transaction, id, ct);
            if (credential is null) return new CredentialMutation(null, []);
            await using var db = new PersistenceContext(connection, transaction);
            if (await db.Set<CredentialRow>().Where(row => row.integration_id == credential.IntegrationId).OrderByDescending(row => row.active)
                .ThenByDescending(row => row.time_created).ThenByDescending(row => row.id).Select(row => row.id).FirstOrDefaultAsync(ct) == id)
                return new CredentialMutation(credential, []);
            var now = Clock.GetUtcNow().ToUnixTimeMilliseconds();
            await db.Set<CredentialRow>().Where(row => row.integration_id == credential.IntegrationId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.active, (long?)0).SetProperty(row => row.time_updated, now), ct);
            await db.Set<CredentialRow>().Where(row => row.id == id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.active, (long?)1).SetProperty(row => row.time_updated, now), ct);
            return new CredentialMutation(credential with { Active = true, StoredActive = true },
                [new CredentialNotification.Switched(credential.IntegrationId, id)]);
        }, ct);

    public Task<CredentialMutation> UpdateAsync(string id, string? label = null, JsonElement? value = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var parsed = value is { } input ? (JsonElement?)ParseValue(input.GetRawText()) : null;
        return _database.RunInTransactionAsync(async (connection, transaction) =>
        {
            var credential = await ReadOneAsync(connection, transaction, id, ct);
            if (credential is null || parsed is null && (label is null || label == credential.Label))
                return new CredentialMutation(credential, []);
            await using var db = new PersistenceContext(connection, transaction);
            var json = parsed?.GetRawText() ?? credential.ValueJson;
            var now = Clock.GetUtcNow().ToUnixTimeMilliseconds();
            await db.Set<CredentialRow>().Where(row => row.id == id).ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.label, label ?? credential.Label).SetProperty(row => row.value, json).SetProperty(row => row.time_updated, now), ct);
            return new CredentialMutation(credential with
            {
                Label = label ?? credential.Label, ValueJson = parsed?.GetRawText() ?? credential.ValueJson,
                Value = parsed ?? credential.Value
            }, label is not null && label != credential.Label ? [new CredentialNotification.Updated()] : []);
        }, ct);
    }

    public Task<CredentialMutation> RemoveAsync(string id, CancellationToken ct = default) =>
        _database.RunInTransactionAsync(async (connection, transaction) =>
        {
            await using var db = new PersistenceContext(connection, transaction);
            var existing = await db.Set<CredentialRow>().Where(row => row.id == id).Select(row => new { row.integration_id }).FirstOrDefaultAsync(ct);
            if (existing is null) return new CredentialMutation(null, []);
            var integration = existing.integration_id;
            var selected = string.IsNullOrEmpty(integration) ? null : await db.Set<CredentialRow>().Where(row => row.integration_id == integration)
                .OrderByDescending(row => row.active).ThenByDescending(row => row.time_created).ThenByDescending(row => row.id)
                .Select(row => row.id).FirstOrDefaultAsync(ct);
            await db.Set<CredentialRow>().Where(row => row.id == id).ExecuteDeleteAsync(ct);
            if (selected != id) return new CredentialMutation(null, [new CredentialNotification.Updated()]);
            var replacement = await db.Set<CredentialRow>().Where(row => row.integration_id == integration)
                .OrderByDescending(row => row.time_created).ThenByDescending(row => row.id).Select(row => row.id).FirstOrDefaultAsync(ct);
            if (replacement is not null)
            {
                var now = Clock.GetUtcNow().ToUnixTimeMilliseconds();
                await db.Set<CredentialRow>().Where(row => row.integration_id == integration)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.active, (long?)0).SetProperty(row => row.time_updated, now), ct);
                await db.Set<CredentialRow>().Where(row => row.id == replacement)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.active, (long?)1).SetProperty(row => row.time_updated, now), ct);
            }
            return new CredentialMutation(null,
                [new CredentialNotification.Updated(), new CredentialNotification.Switched(integration!, replacement)]);
        }, ct);

    private static async Task<StoredCredential?> ReadOneAsync(SqliteConnection connection, SqliteTransaction? transaction, string id, CancellationToken ct)
    {
        await using var db = new PersistenceContext(connection, transaction);
        var row = await db.Set<CredentialRow>().FirstOrDefaultAsync(row => row.id == id && row.integration_id != null && row.integration_id != "", ct);
        return row is null ? null : ReadCredential(row);
    }

    private async Task<IReadOnlyList<StoredCredential>> ReadCredentialsAsync(string? integrationId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // The injected database owns channel placement. Never open another path or
        // import auth.json when this channel has no credentials.
        await using var conn = _database.CreateConnection();
        await using var db = new PersistenceContext(conn);
        return (await db.Set<CredentialRow>().Where(row => row.integration_id != null && row.integration_id != ""
                && (integrationId == null || row.integration_id == EF.Functions.Collate(integrationId, "BINARY")))
            .OrderBy(row => row.active).ThenBy(row => row.time_created).ThenBy(row => row.id).ToListAsync(ct)).Select(ReadCredential).ToArray();
    }

    private static StoredCredential ReadCredential(CredentialRow row)
    {
        bool? active = row.active switch
        {
            null => null, 0 => false, 1 => true,
            _ => throw new InvalidDataException("Stored credential active state must be NULL, zero, or one.")
        };
        var value = row.value;
        return new StoredCredential(row.id, row.integration_id!, row.label, value, active == true)
        { StoredActive = active, Value = ParseValue(value) };
    }

    private static JsonElement ParseValue(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var value = document.RootElement;
            if (value.ValueKind != JsonValueKind.Object || !HasString(value, "type")) throw InvalidValue();
            if (value.TryGetProperty("metadata", out var metadata) && metadata.ValueKind != JsonValueKind.Object) throw InvalidValue();
            switch (value.GetProperty("type").GetString())
            {
                case "key":
                    if (!HasString(value, "key")) throw InvalidValue();
                    if (value.TryGetProperty("configuration", out var configuration))
                    {
                        if (configuration.ValueKind != JsonValueKind.Object) throw InvalidValue();
                        foreach (var property in configuration.EnumerateObject())
                        {
                            var field = property.Value;
                            if (field.ValueKind is JsonValueKind.String or JsonValueKind.True or JsonValueKind.False) continue;
                            if (field.ValueKind == JsonValueKind.Number && field.TryGetDouble(out var number) && double.IsFinite(number)) continue;
                            if (field.ValueKind == JsonValueKind.Array && field.EnumerateArray().All(item => item.ValueKind == JsonValueKind.String)) continue;
                            throw InvalidValue();
                        }
                    }
                    break;
                case "oauth":
                    if (!HasString(value, "methodID") || !HasString(value, "refresh") || !HasString(value, "access")
                        || !value.TryGetProperty("expires", out var expires) || expires.ValueKind != JsonValueKind.Number
                        || !expires.TryGetDecimal(out var time) || time < 0 || decimal.Truncate(time) != time) throw InvalidValue();
                    break;
                default:
                    throw InvalidValue();
            }
            return value.Clone();
        }
        catch (JsonException)
        {
            // Do not include secret-bearing JSON in diagnostics or exception causes.
            throw InvalidValue();
        }
    }

    private static bool HasString(JsonElement value, string name) =>
        value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String;

    private static InvalidDataException InvalidValue() => new("Stored credential value does not match the current key/OAuth contract.");
}
