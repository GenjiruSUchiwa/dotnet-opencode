namespace OpenCode.Core.Session.Transfer;

using System.Text.Json;
using System.Text.Json.Serialization;
using OpenCode.Core.Event;
using Microsoft.EntityFrameworkCore;
using OpenCode.Core.Persistence;
using OpenCode.Schema;

internal sealed record SessionMovedData(
    [property: JsonPropertyName("sessionID"), JsonRequired] SessionId SessionId,
    [property: JsonPropertyName("location"), JsonRequired] LocationRef Location,
    [property: JsonPropertyName("projectID"), JsonRequired] ProjectId ProjectId,
    [property: JsonPropertyName("subpath")] string? Subpath);

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SessionMovedData))]
internal partial class MoveJsonContext : JsonSerializerContext;

internal static class MoveProjector
{
    internal static readonly OpenCode.Core.Event.DurableEventDefinition<SessionMovedData> Moved = new(
        "session.moved", 1, "sessionID", MoveJsonContext.Default.SessionMovedData, ProjectAsync);

    internal static async Task ProjectAsync(EventTransaction transaction, OpenCodeEvent committed, CancellationToken ct)
    {
        var data = committed.Data.Deserialize(MoveJsonContext.Default.SessionMovedData)!;
        var row = await transaction.Db.Sessions.Where(row => row.id == data.SessionId.Value)
            .Select(row => new { row.directory, row.workspace_id, row.project_id, row.path }).FirstOrDefaultAsync(ct).ConfigureAwait(true)
            ?? throw new SessionMutationNotFoundException(data.SessionId);
        var previous = new MessageLocation(new LocationRef(row.directory, row.workspace_id is null ? null : WorkspaceId.FromExisting(row.workspace_id)),
            ProjectId.FromExisting(row.project_id), row.path);
        SessionMessage message = new LocationSwitchedMessage
        {
            Id = MessageId.FromExisting("msg_" + committed.Id.Value[EventId.Prefix.Length..]),
            Time = new MessageTime(DateTimeOffset.FromUnixTimeMilliseconds(checked((long)committed.Created))),
            Metadata = committed.Metadata,
            Location = data.Location,
            ProjectId = data.ProjectId,
            Subpath = data.Subpath,
            Previous = previous
        };
        var encoded = JsonSerializer.SerializeToNode(message, OpenCodeJsonContext.Default.SessionMessage)!.AsObject();
        encoded.Remove("id");
        encoded.Remove("type");
        await SqliteIntrinsics.InsertMessageAsync(transaction.Db, message.Id.Value, data.SessionId.Value, "location-switched",
            checked((long)committed.Durable!.Seq), committed.Created, transaction.Clock.GetUtcNow().ToUnixTimeMilliseconds(), encoded.ToJsonString(), ct).ConfigureAwait(true);
        var directory = OperatingSystem.IsWindows() ? data.Location.Directory.Replace('\\', '/') : data.Location.Directory;
        var workspace = data.Location.WorkspaceId?.Value;
        await transaction.Db.Sessions.Where(row => row.id == data.SessionId.Value).ExecuteUpdateAsync(setters => setters
            .SetProperty(row => row.directory, directory).SetProperty(row => row.workspace_id, workspace)
            .SetProperty(row => row.project_id, data.ProjectId.Value).SetProperty(row => row.path, data.Subpath), ct).ConfigureAwait(true);
        await SqliteIntrinsics.TouchSessionAsync(transaction.Db, data.SessionId.Value, committed.Created, ct).ConfigureAwait(true);
        // Retain the instruction epoch. Destination observation freezes its delta in later System history.
    }
}
