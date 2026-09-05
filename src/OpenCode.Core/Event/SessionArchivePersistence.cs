namespace OpenCode.Core.Event;

using System.Text.Json;
using System.Text.Json.Nodes;
using OpenCode.Core.Database;
using Microsoft.EntityFrameworkCore;
using OpenCode.Core.Persistence;
using OpenCode.Core.Projects;
using OpenCode.Core.Session;
using OpenCode.Core.Session.Archive;
using OpenCode.Schema;

/// <summary>Source projected-archive import, using Created's projector and one canonical Session transaction.</summary>
public sealed class SessionArchivePersistence(IDatabase database, string version = OpenCodeChannel.ServiceVersion) : ISessionArchivePersistence
{
    private static readonly string[] Adjectives = ["brave", "calm", "clever", "cosmic", "crisp", "curious", "eager", "gentle", "glowing", "happy", "hidden", "jolly", "kind", "lucky", "mighty", "misty", "neon", "nimble", "playful", "proud", "quick", "quiet", "shiny", "silent", "stellar", "sunny", "swift", "tidy", "witty"];
    private static readonly string[] Nouns = ["cabin", "cactus", "canyon", "circuit", "comet", "eagle", "engine", "falcon", "forest", "garden", "harbor", "island", "knight", "lagoon", "meadow", "moon", "mountain", "nebula", "orchid", "otter", "panda", "pixel", "planet", "river", "rocket", "sailor", "squid", "star", "tiger", "wizard", "wolf"];

    public Task<SessionInfo> ImportAsync(SessionArchiveImport input, CancellationToken ct) =>
        SessionRunCoordinator.AdmitAsync(input.Id, async () =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(version);
            var messages = input.Messages.Where(SessionArchiveService.IsSettled).Select(message =>
            {
                var data = JsonSerializer.SerializeToNode(message, OpenCodeJsonContext.Default.SessionMessage)!.AsObject();
                var type = data["type"]!.GetValue<string>();
                data.Remove("id"); data.Remove("type");
                return (message.Id, Type: type, Created: message.Time.Created.ToUnixTimeMilliseconds(), Data: data.ToJsonString());
            }).ToArray();
            if (messages.Select(message => message.Id).Distinct().Count() != messages.Length)
                throw new JsonException("Archive contains duplicate settled message IDs.");
            var metadata = input.Metadata?.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal);
            // Source resolution/upsert is outside the Session commit, but never trusts archive project/location bindings.
            var destination = await ProjectDiscovery.ResolveAsync(database, input.Location.Directory, input.Location.WorkspaceId?.Value, ct);
            var location = new LocationRef(Path.GetFullPath(input.Location.Directory), input.Location.WorkspaceId);
            var subpath = Path.GetRelativePath(destination.Project.Directory, location.Directory).Replace('\\', '/');
            return await new EventStore(database).TransactAsync(input.Id.Value, async (transaction, token) =>
            {
                if (await transaction.Db.Sessions.AnyAsync(row => row.id == input.Id.Value, token)) throw new SessionArchiveConflictException(input.Id);
                // A removed/unreconciled aggregate is not a fresh archive destination.
                if (await transaction.LatestSequenceAsync(input.Id.Value, token) >= 0) throw new SessionArchiveConflictException(input.Id);
                if (input.ParentId is { } parent)
                {
                    if (!await transaction.Db.Sessions.AnyAsync(row => row.id == parent.Value, token)) throw new SessionMutationNotFoundException(parent);
                }
                foreach (var message in messages)
                {
                    if (await transaction.Db.Messages.Where(row => row.id == message.Id.Value).Select(row => row.id)
                        .Concat(transaction.Db.Inbox.Where(row => row.id == message.Id.Value).Select(row => row.id)).AnyAsync(token))
                        throw new InvalidOperationException($"Archive message ID already exists: {message.Id}");
                }
                var created = await transaction.AppendAsync(SessionCreation.Created, new SessionCreatedEventData(input.Id, destination.Project.Id,
                    $"{Adjectives[Random.Shared.Next(Adjectives.Length)]}-{Nouns[Random.Shared.Next(Nouns.Length)]}", version, location,
                    input.Title, input.Agent, input.Model, input.ParentId, metadata, subpath == "." ? "" : subpath), token, location: location);
                for (var index = 0; index < messages.Length; index++)
                {
                    var message = messages[index];
                    await transaction.Db.InsertAsync(new MessageRow { id = message.Id.Value, session_id = input.Id.Value, type = message.Type,
                        seq = index + 1, time_created = message.Created, time_updated = database.Clock.GetUtcNow().ToUnixTimeMilliseconds(), data = message.Data }, token);
                }
                if (messages.Length > 0) await transaction.ReserveSequenceAsync(input.Id.Value, checked((long)created.Durable!.Seq + messages.Length), token);
                await SqliteIntrinsics.RestoreArchiveAsync(transaction.Db, input, token);
                return SessionStore.ReadSession(await transaction.Db.Sessions.FirstOrDefaultAsync(row => row.id == input.Id.Value, token)
                    ?? throw new InvalidOperationException("Import projection did not create its Session."));
            }, ct);
        }, ct);
}
