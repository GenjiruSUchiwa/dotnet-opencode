namespace OpenCode.Core.Event;

using System.Text.Json;
using OpenCode.Core.Database;
using Microsoft.EntityFrameworkCore;
using OpenCode.Core.Persistence;
using OpenCode.Schema;

internal sealed class SessionCreation(IDatabase database)
{
    internal static readonly DurableEventDefinition<SessionCreatedEventData> Created = new(
        "session.created", 1, "sessionID", OpenCodeJsonContext.Default.SessionCreatedEventData, ProjectAsync);

    internal Task<SessionInfo> CreateAsync(SessionCreatedEventData data, CancellationToken ct) =>
        new EventStore(database).TransactAsync(data.SessionId.Value, async (transaction, token) =>
        {
            var existing = await transaction.Db.SessionDetails.FirstOrDefaultAsync(row => row.id == data.SessionId.Value, token).ConfigureAwait(true);
            if (existing is not null) return SessionStore.ReadSession(existing);
            if (await transaction.LatestSequenceAsync(data.SessionId.Value, token).ConfigureAwait(true) >= 0)
                throw new NotSupportedException("An existing aggregate without its session requires canonical removal/recovery before ID reuse.");
            await transaction.AppendAsync(Created, data, token).ConfigureAwait(true);
            var projected = await transaction.Db.SessionDetails.FirstOrDefaultAsync(row => row.id == data.SessionId.Value, token).ConfigureAwait(true)
                ?? throw new InvalidOperationException("Creation projection did not create a session.");
            return SessionStore.ReadSession(projected);
        }, ct);

    private static async Task ProjectAsync(EventTransaction transaction, OpenCodeEvent committed, CancellationToken ct)
    {
        var data = committed.Data.Deserialize(OpenCodeJsonContext.Default.SessionCreatedEventData)!;
        if (await SqliteIntrinsics.InsertSessionAsync(transaction.Db, data.SessionId.Value, data.ProjectId.Value,
            data.Location.WorkspaceId?.Value, data.ParentId?.Value, data.Slug,
            OperatingSystem.IsWindows() ? data.Location.Directory.Replace('\\', '/') : data.Location.Directory,
            OperatingSystem.IsWindows() ? data.Subpath?.Replace('\\', '/') : data.Subpath, data.Title, data.Agent,
            data.Model is null ? null : JsonSerializer.Serialize(data.Model, OpenCodeJsonContext.Default.ModelRef),
            data.Metadata is null ? null : JsonSerializer.Serialize(data.Metadata, OpenCodeJsonContext.Default.Options), data.Version, committed.Created, ct).ConfigureAwait(true) != 1)
            throw new InvalidOperationException("Session was already projected.");
    }
}
