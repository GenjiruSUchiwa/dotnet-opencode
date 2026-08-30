namespace OpenCode.Core.Database;

using System.Text.Json;
using OpenCode.Schema;

public sealed record StoredCredential(
    string Id,
    string IntegrationId,
    string Label,
    string ValueJson,
    bool Active
);

public sealed class CredentialStore
{
    private readonly IDatabase _database;

    public CredentialStore(IDatabase database)
    {
        _database = database;
    }

    public async Task<IReadOnlyList<StoredCredential>> ListCredentialsAsync(CancellationToken ct = default)
    {
        await using var conn = _database.CreateConnection();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, integration_id, label, value, active FROM credential";

        var list = new List<StoredCredential>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var id = reader.GetString(0);
            var integrationId = reader.IsDBNull(1) ? "" : reader.GetString(1);
            var label = reader.IsDBNull(2) ? "" : reader.GetString(2);
            var valueJson = reader.IsDBNull(3) ? "{}" : reader.GetString(3);
            var active = !reader.IsDBNull(4) && reader.GetInt32(4) == 1;

            list.Add(new StoredCredential(id, integrationId, label, valueJson, active));
        }

        return list;
    }

    public async Task<StoredCredential?> GetActiveCredentialAsync(string integrationId, CancellationToken ct = default)
    {
        var all = await ListCredentialsAsync(ct);
        return all.FirstOrDefault(c => string.Equals(c.IntegrationId, integrationId, StringComparison.OrdinalIgnoreCase) && c.Active)
            ?? all.FirstOrDefault(c => string.Equals(c.IntegrationId, integrationId, StringComparison.OrdinalIgnoreCase));
    }
}
