namespace OpenCode.Core.Event;

using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using OpenCode.Core.Database;
using Microsoft.EntityFrameworkCore;
using OpenCode.Core.Persistence;
using OpenCode.Core.Session;
using OpenCode.Schema;

internal sealed record SessionShellStartedData([property: JsonPropertyName("sessionID"), JsonRequired] SessionId SessionId,
    [property: JsonPropertyName("shell"), JsonRequired] ShellInfo Shell);
internal sealed record SessionShellEndedData([property: JsonPropertyName("sessionID"), JsonRequired] SessionId SessionId,
    [property: JsonPropertyName("shell"), JsonRequired] ShellInfo Shell, [property: JsonPropertyName("output"), JsonRequired] ShellOutput Output);

[JsonSerializable(typeof(SessionShellStartedData))]
[JsonSerializable(typeof(SessionShellEndedData))]
internal partial class SessionShellEventJsonContext : JsonSerializerContext;

/// <summary>Source session shell facts and message-updater/projector rules. Does not run a shell or admit a notification.</summary>
internal sealed class SessionShellPersistence(IDatabase database)
{
    internal static readonly DurableEventDefinition<SessionShellStartedData> Started = new("session.shell.started", 1, "sessionID",
        SessionShellEventJsonContext.Default.SessionShellStartedData, ProjectAsync);
    internal static readonly DurableEventDefinition<SessionShellEndedData> Ended = new("session.shell.ended", 1, "sessionID",
        SessionShellEventJsonContext.Default.SessionShellEndedData, ProjectAsync);

    internal Task<OpenCodeEvent> StartedAsync(SessionId sessionId, ShellInfo shell, CancellationToken ct, EventId? eventId,
        IReadOnlyDictionary<string, JsonElement>? metadata) => PublishAsync(sessionId, Started,
            new SessionShellStartedData(sessionId, Copy(shell)), ct, eventId, metadata);

    internal Task<OpenCodeEvent> EndedAsync(SessionId sessionId, ShellInfo shell, ShellOutput output, CancellationToken ct, EventId? eventId,
        IReadOnlyDictionary<string, JsonElement>? metadata) => PublishAsync(sessionId, Ended,
            new SessionShellEndedData(sessionId, Copy(shell), output), ct, eventId, metadata);

    private Task<OpenCodeEvent> PublishAsync<T>(SessionId sessionId, DurableEventDefinition<T> definition, T data, CancellationToken ct,
        EventId? eventId, IReadOnlyDictionary<string, JsonElement>? metadata)
    {
        var captured = metadata is null ? null : new ReadOnlyDictionary<string, JsonElement>(
            metadata.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal));
        return SessionRunCoordinator.AdmitAsync(sessionId, () => new EventStore(database).TransactAsync(sessionId.Value, async (transaction, token) =>
        {
            if (!await transaction.Db.Sessions.AnyAsync(row => row.id == sessionId.Value, token)) throw new SessionMutationNotFoundException(sessionId);
            if (await transaction.Db.HighestProjectionAsync(sessionId.Value, token) > await transaction.LatestSequenceAsync(sessionId.Value, token))
                throw new NotSupportedException("Unsequenced projections require canonical migration before shell publication.");
            return await transaction.AppendAsync(definition, data, token, eventId, captured);
        }, ct), ct);
    }

    private static ShellInfo Copy(ShellInfo shell) => shell with
    {
        Metadata = new ReadOnlyDictionary<string, JsonElement>(shell.Metadata.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal))
    };

    private static async Task ProjectAsync(EventTransaction transaction, OpenCodeEvent committed, CancellationToken ct)
    {
        var sessionId = committed.Data.GetProperty("sessionID").GetString()!;
        var shell = committed.Data.GetProperty("shell");
        if (committed.Type == "session.shell.started")
        {
            var data = new JsonObject
            {
                ["shellID"] = shell.GetProperty("id").GetString(), ["command"] = shell.GetProperty("command").GetString(),
                ["status"] = shell.GetProperty("status").GetString(), ["time"] = new JsonObject { ["created"] = committed.Created }
            };
            var metadata = committed.Metadata is null ? null : new JsonObject(committed.Metadata.Select(pair =>
                new KeyValuePair<string, JsonNode?>(pair.Key, JsonNode.Parse(pair.Value.GetRawText()))));
            if (shell.GetProperty("metadata").TryGetProperty("background", out var background) && background.ValueKind == JsonValueKind.True)
            {
                metadata ??= new JsonObject();
                metadata["background"] = true;
            }
            if (metadata is not null) data["metadata"] = metadata;
            await SqliteIntrinsics.InsertMessageAsync(transaction.Db, "msg_" + committed.Id.Value[4..], sessionId, "shell",
                checked((long)committed.Durable!.Seq), committed.Created, transaction.Clock.GetUtcNow().ToUnixTimeMilliseconds(), data.ToJsonString(), ct);
            return;
        }
        var shellId = shell.GetProperty("id").GetString()!;
        var current = await transaction.Db.Messages.Where(row => row.session_id == sessionId && row.type == "shell"
            && SqliteFunctions.JsonText(row.data, "$.shellID") == shellId).OrderByDescending(row => row.seq)
            .Select(row => new { row.id, row.data }).FirstOrDefaultAsync(ct);
        if (current is null) return; // Source records the end fact even when no shell message remains.
        var id = current.id;
        SessionQueries.Decode(SessionId.FromExisting(sessionId), MessageId.FromExisting(id), "shell", current.data);
        var message = JsonNode.Parse(current.data)!.AsObject();
        message["status"] = shell.GetProperty("status").GetString();
        if (shell.TryGetProperty("exit", out var exit)) message["exit"] = JsonNode.Parse(exit.GetRawText());
        else message.Remove("exit");
        message["output"] = JsonNode.Parse(committed.Data.GetProperty("output").GetRawText());
        message["time"]!["completed"] = committed.Created;
        var json = message.ToJsonString();
        var updated = transaction.Clock.GetUtcNow().ToUnixTimeMilliseconds();
        await transaction.Db.Messages.Where(row => row.session_id == sessionId && row.id == id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.data, json).SetProperty(row => row.time_updated, updated), ct);
    }
}
