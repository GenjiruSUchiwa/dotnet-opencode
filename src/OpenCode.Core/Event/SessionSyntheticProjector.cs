namespace OpenCode.Core.Event;

using System.Text.Json;
using System.Text.Json.Serialization;
using OpenCode.Schema;
using OpenCode.Core.Persistence;

internal sealed record SessionSyntheticData(
    [property: JsonPropertyName("sessionID"), JsonRequired] SessionId SessionId,
    [property: JsonPropertyName("text"), JsonRequired] string Text,
    [property: JsonPropertyName("description"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Description = null,
    [property: JsonPropertyName("metadata"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyDictionary<string, JsonElement>? Metadata = null);

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SessionSyntheticData))]
internal partial class SessionSyntheticJsonContext : JsonSerializerContext;

internal static class SessionSyntheticProjector
{
    internal static readonly DurableEventDefinition<SessionSyntheticData> Definition = new(
        "session.synthetic", 1, "sessionID", SessionSyntheticJsonContext.Default.SessionSyntheticData, ProjectAsync);

    private static async Task ProjectAsync(EventTransaction transaction, OpenCodeEvent committed, CancellationToken ct)
    {
        var data = committed.Data.Deserialize(SessionSyntheticJsonContext.Default.SessionSyntheticData)!;
        var encoded = new System.Text.Json.Nodes.JsonObject
        {
            ["text"] = data.Text, ["time"] = new System.Text.Json.Nodes.JsonObject { ["created"] = committed.Created }
        };
        if (data.Description is not null) encoded["description"] = data.Description;
        if (data.Metadata is not null) encoded["metadata"] = JsonSerializer.SerializeToNode(data.Metadata, OpenCodeJsonContext.Default.Options);
        await SqliteIntrinsics.InsertMessageAsync(transaction.Db, "msg_" + committed.Id.Value[4..], data.SessionId.Value, "synthetic",
            checked((long)committed.Durable!.Seq), committed.Created, transaction.Clock.GetUtcNow().ToUnixTimeMilliseconds(), encoded.ToJsonString(), ct);
    }
}
