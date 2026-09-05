namespace OpenCode.Core.WebSearch;

using System.Text.Json;
using System.Diagnostics.CodeAnalysis;
using OpenCode.Core.Database;
using Microsoft.EntityFrameworkCore;
using OpenCode.Core.Persistence;
using OpenCode.Schema;

/// <summary>Source websearch:provider preference in the already injected channel KV table. No file import or memory fallback.</summary>
public sealed class WebSearchSelectionStore(IDatabase database) : IWebSearchSelectionStore
{
    public const string Key = "websearch:provider";

    public async Task<WebSearchSelection?> ReadAsync(CancellationToken ct)
    {
        var connection = database.CreateConnection();
        await using var connectionLifetime = connection.ConfigureAwait(true);
        var db = new PersistenceContext(connection);
        await using var dbLifetime = db.ConfigureAwait(true);
        if (await db.Set<KvRow>().Where(row => row.key == Key).Select(row => row.value).FirstOrDefaultAsync(ct).ConfigureAwait(true) is not { } text) return null;
        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind == JsonValueKind.False) return new(Disabled: true);
            if (document.RootElement.ValueKind == JsonValueKind.String) return new(document.RootElement.GetString());
        }
        catch (JsonException) { }
        // Source removes malformed stored selection; do not erase a concurrently corrected value.
        await db.Set<KvRow>().Where(row => row.key == Key && row.value == text).ExecuteDeleteAsync(ct).ConfigureAwait(true);
        return null;
    }

    [SuppressMessage("Design", "MA0015", Justification = "The provider-selection validation text is part of the existing public error contract.")]
    public Task SaveAsync(WebSearchSelection selection, CancellationToken ct)
    {
        selection.Validate();
        if (!selection.Disabled && selection.ProviderId is null) throw new ArgumentException("Selection must be a provider, random, or false.");
        return database.RunInTransactionAsync(async (connection, transaction) =>
        {
            var db = new PersistenceContext(connection, transaction);
            await using var dbLifetime = db.ConfigureAwait(true);
            await SqliteIntrinsics.PutKvAsync(db, Key, selection.Disabled ? "false" : JsonSerializer.Serialize(selection.ProviderId, OpenCodeJsonContext.Default.String),
                database.Clock.GetUtcNow().ToUnixTimeMilliseconds(), ct).ConfigureAwait(true);
            return true;
        }, ct);
    }
}
