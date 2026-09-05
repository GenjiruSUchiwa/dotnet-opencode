namespace OpenCode.Core.Event;

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using OpenCode.Core.Database;
using Microsoft.EntityFrameworkCore;
using OpenCode.Core.Persistence;
using OpenCode.Core.Session;
using OpenCode.Core.Instructions;
using OpenCode.Schema;

internal sealed record InstructionsUpdatedData(
    [property: JsonPropertyName("sessionID"), JsonRequired] SessionId SessionId,
    [property: JsonPropertyName("delta"), JsonRequired] IReadOnlyDictionary<string, string> Delta,
    [property: JsonPropertyName("text"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Text = null);

[JsonSerializable(typeof(InstructionsUpdatedData))]
internal partial class InstructionEventJsonContext : JsonSerializerContext;

internal sealed class InstructionPersistence(IDatabase database)
{
    internal static readonly DurableEventDefinition<InstructionsUpdatedData> Updated = new(
        "session.instructions.updated", 2, "sessionID", InstructionEventJsonContext.Default.InstructionsUpdatedData, ProjectAsync);

    internal async Task<IReadOnlyList<InstructionEntrySnapshot>> EntriesAsync(SessionId id, CancellationToken ct)
    {
        var connection = database.CreateConnection();
        await using var connectionLifetime = connection.ConfigureAwait(true);
        var db = new PersistenceContext(connection);
        await using var dbLifetime = db.ConfigureAwait(true);
        var session = id.Value;
        var rows = db.Set<InstructionEntryRow>().Where(row => row.session_id == session).OrderBy(row => row.key)
            .Select(row => new { row.key, row.value, row.removed });
        var entries = new List<InstructionEntrySnapshot>();
        await foreach (var row in rows.ReadAsync(-1, ct).ConfigureAwait(true))
        {
            using var value = JsonDocument.Parse(row.value ?? "null");
            entries.Add(new InstructionEntrySnapshot(row.key, value.RootElement.Clone(), row.removed));
        }
        return entries;
    }

    internal Task PrepareAsync(SessionId id, IReadOnlyList<InstructionSource> sources, bool preview, CancellationToken ct) =>
        new EventStore(database).TransactAsync(id.Value, async (transaction, token) =>
        {
            var state = await StateAsync(transaction, id, token).ConfigureAwait(true);
            RequireSources(sources, state);
            // A retained epoch must remain renderable even if a producer disappeared.
            if (state is not null) await RenderInitialAsync(transaction, sources, state.Initial, token).ConfigureAwait(true);
            var observed = await ObserveAsync(transaction, sources, state, token).ConfigureAwait(true);
            if (preview || state is not null && observed.Delta.Count == 0) return false;
            foreach (var blob in observed.Blobs)
            {
                await SqliteIntrinsics.PutInstructionBlobAsync(transaction.Db, blob.Key, InstructionJson.Stringify(blob.Value), token).ConfigureAwait(true);
            }
            var rendered = state is null ? "" : observed.Text;
            await transaction.AppendAsync(Updated, new InstructionsUpdatedData(id, observed.Delta, rendered.Length == 0 ? null : rendered), token,
                metadata: state is null ? new Dictionary<string, JsonElement> { ["instructions"] = JsonSerializer.SerializeToElement(new { initial = true }) } : null).ConfigureAwait(true);
            return true;
        }, ct);

    /// <summary>SessionHistory.preview / InstructionState.preview: only SELECTs; never establish an epoch or persist blobs.</summary>
    internal Task<(string Initial, string Update, IReadOnlyList<JsonElement> Messages)> PreviewAsync(SessionId id,
        IReadOnlyList<InstructionSource> sources, CancellationToken ct) =>
        new EventStore(database).TransactAsync(id.Value, async (transaction, token) =>
        {
            var state = await StateAsync(transaction, id, token).ConfigureAwait(true);
            RequireSources(sources, state);
            var observed = await ObserveAsync(transaction, sources, state, token).ConfigureAwait(true);
            var initial = state is null ? observed.Text : await RenderInitialAsync(transaction, sources, state.Initial, token).ConfigureAwait(true);
            var rows = SessionQueries.Context(transaction.Db, id.Value).OrderBy(row => row.seq)
                .Select(row => new { row.id, row.type, row.data });
            var messages = new List<JsonElement>();
            var unsettled = false;
            await foreach (var row in rows.ReadAsync(-1, token).ConfigureAwait(true))
            {
                // Decode the full selected context, then retain only the source's settled prefix.
                var message = SessionQueries.Decode(id, MessageId.FromExisting(row.id), row.type, row.data);
                if (message is AssistantMessage { Time.Completed: null }) unsettled = true;
                if (!unsettled) messages.Add(JsonSerializer.SerializeToElement(message, OpenCodeJsonContext.Default.SessionMessage));
            }
            return (initial, state is null ? "" : observed.Text, (IReadOnlyList<JsonElement>)messages);
        }, ct);

    private sealed record Observed(Dictionary<string, string> Delta, Dictionary<string, JsonElement> Blobs, string Text);

    private static async Task<Observed> ObserveAsync(EventTransaction transaction, IReadOnlyList<InstructionSource> sources, State? state, CancellationToken ct)
    {
        var previous = state?.Current ?? new Dictionary<string, string>(StringComparer.Ordinal);
        var delta = new Dictionary<string, string>(StringComparer.Ordinal);
        var blobs = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var text = new List<string>();
        foreach (var source in sources)
        {
            if (source.Availability == InstructionAvailability.Unavailable) continue;
            var oldHash = previous.GetValueOrDefault(source.Key);
            if (source.Availability == InstructionAvailability.Removed)
            {
                if (oldHash is null) continue;
                delta.Add(source.Key, "removed");
                if (source.Removed is not null) text.Add(RequireText(source.Key, source.Removed(await BlobAsync(transaction, oldHash, ct).ConfigureAwait(true))));
                continue;
            }
            var hash = InstructionJson.Hash(source.Value);
            if (hash == oldHash) continue;
            delta.Add(source.Key, hash);
            blobs[hash] = source.Value;
            text.Add(RequireText(source.Key, oldHash is null ? source.Initial(source.Value) : source.Changed(await BlobAsync(transaction, oldHash, ct).ConfigureAwait(true), source.Value)));
        }
        return new(delta, blobs, string.Join("\n\n", text));
    }

    internal Task<(string Initial, IReadOnlyList<JsonElement> Messages)> LoadAsync(SessionId id,
        IReadOnlyList<InstructionSource> sources, CancellationToken ct) =>
        new EventStore(database).TransactAsync(id.Value, async (transaction, token) =>
        {
            var state = await StateAsync(transaction, id, token).ConfigureAwait(true) ?? throw new InvalidOperationException("Instruction baseline is missing during request assembly.");
            RequireSources(sources, state);
            var initial = await RenderInitialAsync(transaction, sources, state.Initial, token).ConfigureAwait(true);
            var rows = SessionQueries.Context(transaction.Db, id.Value).OrderBy(row => row.seq)
                .Select(row => new { row.id, row.type, row.data });
            var messages = new List<JsonElement>();
            await foreach (var row in rows.ReadAsync(-1, token).ConfigureAwait(true))
            {
                var data = new JsonObject { ["type"] = row.type, ["id"] = row.id };
                foreach (var pair in JsonNode.Parse(row.data)!.AsObject())
                    if (pair.Key is not ("id" or "type")) data[pair.Key] = pair.Value?.DeepClone();
                messages.Add(JsonSerializer.SerializeToElement(data));
            }
            return (initial, (IReadOnlyList<JsonElement>)messages);
        }, ct);

    private sealed record State(Dictionary<string, string> Initial, Dictionary<string, string> Current);

    private static async Task<State?> StateAsync(EventTransaction transaction, SessionId id, CancellationToken ct)
    {
        var row = await transaction.Db.Set<InstructionStateRow>().Where(row => row.session_id == id.Value)
            .Select(row => new { row.initial_values, row.current_values }).FirstOrDefaultAsync(ct).ConfigureAwait(true);
        return row is null ? null : new State(JsonSerializer.Deserialize<Dictionary<string, string>>(row.initial_values)!,
            JsonSerializer.Deserialize<Dictionary<string, string>>(row.current_values)!);
    }

    private static void RequireSources(IReadOnlyList<InstructionSource> sources, State? state)
    {
        if (sources.Select(source => source.Key).Distinct(StringComparer.Ordinal).Count() != sources.Count)
            throw new InvalidOperationException("Duplicate instruction source key.");
        var keys = sources.Select(source => source.Key).ToHashSet(StringComparer.Ordinal);
        var blocked = state is null
            ? sources.Where(source => source.Availability == InstructionAvailability.Unavailable).Select(source => source.Key).ToArray()
            : state.Initial.Keys.Concat(state.Current.Keys).Where(key => !keys.Contains(key)).Distinct().ToArray();
        if (blocked.Length > 0) throw new InstructionInitializationBlockedException(blocked);
    }

    private static async Task<JsonElement> BlobAsync(EventTransaction transaction, string hash, CancellationToken ct)
    {
        if (await transaction.Db.Set<InstructionBlobRow>().Where(row => row.hash == hash).Select(row => row.value).FirstOrDefaultAsync(ct).ConfigureAwait(true) is not { } json)
            throw new InvalidOperationException("Referenced instruction blob is missing.");
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static async Task<string> RenderInitialAsync(EventTransaction transaction, IReadOnlyList<InstructionSource> sources,
        Dictionary<string, string> values, CancellationToken ct)
    {
        var parts = new List<string>();
        foreach (var source in sources)
            if (values.TryGetValue(source.Key, out var hash)) parts.Add(RequireText(source.Key, source.Initial(await BlobAsync(transaction, hash, ct).ConfigureAwait(true))));
        return string.Join("\n\n", parts);
    }

    private static string RequireText(string key, string text) => text.Length > 0 ? text :
        throw new InvalidOperationException($"Instruction source {key} rendered empty text.");

    private static async Task ProjectAsync(EventTransaction transaction, OpenCodeEvent committed, CancellationToken ct)
    {
        var data = committed.Data.Deserialize(InstructionEventJsonContext.Default.InstructionsUpdatedData)!;
        var state = await StateAsync(transaction, data.SessionId, ct).ConfigureAwait(true);
        var current = state?.Current ?? new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in data.Delta)
        {
            if (pair.Value == "removed") current.Remove(pair.Key);
            else current[pair.Key] = pair.Value;
        }
        await SqliteIntrinsics.PutInstructionStateAsync(transaction.Db, data.SessionId.Value, checked((long)committed.Durable!.Seq), JsonSerializer.Serialize(current), ct).ConfigureAwait(true);
        if (data.Text is null) return;
        var json = JsonSerializer.Serialize(new
        {
            text = data.Text, description = "Instructions updated: " + string.Join(", ", data.Delta.Keys),
            time = new { created = committed.Created }
        });
        await SqliteIntrinsics.InsertMessageAsync(transaction.Db, "msg_" + committed.Id.Value[4..], data.SessionId.Value, "system",
            checked((long)committed.Durable!.Seq), committed.Created, transaction.Clock.GetUtcNow().ToUnixTimeMilliseconds(), json, ct).ConfigureAwait(true);
    }
}
