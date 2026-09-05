namespace OpenCode.Core.Session.Transfer;

using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using OpenCode.Core.Event;
using Microsoft.EntityFrameworkCore;
using OpenCode.Core.Persistence;
using OpenCode.Core.Projects;
using OpenCode.Core.Instructions;
using OpenCode.Schema;

internal sealed record SessionForkedData(
    [property: JsonPropertyName("sessionID"), JsonRequired] SessionId SessionId,
    [property: JsonPropertyName("parentID"), JsonRequired] SessionId ParentId,
    [property: JsonPropertyName("boundary"), JsonRequired] ForkBoundary Boundary,
    [property: JsonPropertyName("instructions")] IReadOnlyDictionary<string, string>? Instructions,
    [property: JsonPropertyName("instructionEntries")] IReadOnlyList<InstructionEntrySnapshot>? InstructionEntries);

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SessionForkedData))]
[JsonSerializable(typeof(IReadOnlyDictionary<string, string>))]
internal partial class TransferJsonContext : JsonSerializerContext;

/// <summary>session/projector.ts projectFork. Invoked only inside EventTransaction.AppendAsync.</summary>
internal static class ForkProjector
{
    // util/slug.ts vocabulary; no shared native slug service exists yet.
    private static readonly string[] Adjectives = ["brave", "calm", "clever", "cosmic", "crisp", "curious", "eager", "gentle", "glowing", "happy", "hidden", "jolly", "kind", "lucky", "mighty", "misty", "neon", "nimble", "playful", "proud", "quick", "quiet", "shiny", "silent", "stellar", "sunny", "swift", "tidy", "witty"];
    private static readonly string[] Nouns = ["cabin", "cactus", "canyon", "circuit", "comet", "eagle", "engine", "falcon", "forest", "garden", "harbor", "island", "knight", "lagoon", "meadow", "moon", "mountain", "nebula", "orchid", "otter", "panda", "pixel", "planet", "river", "rocket", "sailor", "squid", "star", "tiger", "wizard", "wolf"];

    internal static readonly OpenCode.Core.Event.DurableEventDefinition<SessionForkedData> Forked = new(
        "session.forked", 2, "sessionID", TransferJsonContext.Default.SessionForkedData, ProjectAsync);

    internal static async Task ProjectAsync(EventTransaction transaction, OpenCodeEvent committed, CancellationToken ct)
    {
        var data = committed.Data.Deserialize(TransferJsonContext.Default.SessionForkedData)!;
        // projectFork reads/decodes the parent before resolving its boundary.
        var parent = await transaction.Db.Sessions.Where(row => row.id == data.ParentId.Value)
            .Select(row => new { row.title, row.directory, row.path }).FirstOrDefaultAsync(ct).ConfigureAwait(true)
            ?? throw new SessionMutationNotFoundException(data.ParentId);
        var directory = ProjectPaths.DirectoryStorage(parent.directory);
        var subpath = parent.path is null ? null : ProjectPaths.Relative(parent.path);
        var boundaryId = data.Boundary switch
        {
            ForkBoundaryBefore before => before.MessageId,
            ForkBoundaryThrough through => through.MessageId,
            _ => throw new JsonException("Unknown fork boundary.")
        };
        var boundarySeq = await transaction.Db.Messages.Where(row => row.session_id == data.ParentId.Value && row.id == boundaryId.Value)
            .Select(row => (long?)row.seq).FirstOrDefaultAsync(ct).ConfigureAwait(true) ?? throw new SessionForkMessageNotFoundException(data.ParentId, boundaryId);
        var exclusive = data.Boundary is ForkBoundaryBefore;
        var copiedSeq = await transaction.Db.Messages.Where(row => row.session_id == data.ParentId.Value && (exclusive ? row.seq < boundarySeq : row.seq <= boundarySeq))
            .MaxAsync(row => (long?)row.seq, ct).ConfigureAwait(true);
        var title = parent.title;
        if (title is not null)
        {
            var match = Regex.Match(title, @"^(?<title>.+) \(fork #(?<number>[0-9]+)\)$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
            title = match.Success
                ? $"{match.Groups["title"].Value} (fork #{BigInteger.Parse(match.Groups["number"].Value, CultureInfo.InvariantCulture) + 1})"
                : $"{title} (fork #1)";
        }
        if (await SqliteIntrinsics.ForkSessionAsync(transaction.Db, data.SessionId.Value, data.ParentId.Value,
            JsonSerializer.Serialize(data.Boundary, OpenCodeJsonContext.Default.ForkBoundary),
            $"{Adjectives[Random.Shared.Next(Adjectives.Length)]}-{Nouns[Random.Shared.Next(Nouns.Length)]}", title, directory, subpath,
            committed.Created, ct).ConfigureAwait(true) != 1)
            throw new InvalidOperationException("Fork session was already projected or its parent is missing.");

        if (data.InstructionEntries is not null)
            foreach (var entry in data.InstructionEntries)
            {
                await SqliteIntrinsics.ForkInstructionEntryAsync(transaction.Db, data.SessionId.Value, entry.Key,
                    entry.Value.ValueKind == JsonValueKind.Null ? null : InstructionJson.Stringify(entry.Value), entry.Removed, committed.Created, ct).ConfigureAwait(true);
            }

        if (copiedSeq is { } last)
        {
            // This is the event's projection, not an independent clone operation.
            // Preserve seq gaps and embedded payload references; only the row ID changes.
            await SqliteIntrinsics.ForkMessagesAsync(transaction.Db, data.SessionId.Value, data.ParentId.Value,
                "msg_" + committed.Id.Value[EventId.Prefix.Length..], last, ct).ConfigureAwait(true);
            // Reserve the selected high-water mark even when its message was unsettled.
            await transaction.ReserveSequenceAsync(data.SessionId.Value, last, ct).ConfigureAwait(true);
        }
        if (data.Instructions is not null)
        {
            await SqliteIntrinsics.ForkInstructionStateAsync(transaction.Db, data.SessionId.Value, checked((long)committed.Durable!.Seq),
                JsonSerializer.Serialize(data.Instructions, TransferJsonContext.Default.IReadOnlyDictionaryStringString), ct).ConfigureAwait(true);
        }
    }
}
