namespace OpenCode.Core.Event;

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using OpenCode.Core.Database;
using Microsoft.EntityFrameworkCore;
using OpenCode.Core.Persistence;
using OpenCode.Schema;

[JsonSerializable(typeof(JsonElement))]
internal partial class AssistantEventJsonContext : JsonSerializerContext;

internal sealed class AssistantProjector(IDatabase database)
{
    internal Task<OpenCodeEvent> AppendAsync(string type, JsonElement data, CancellationToken ct, EventId? id)
    {
        var version = AssistantEventData.Version(type);
        var encoded = AssistantEventData.Validate(type, data);
        var sessionId = encoded.GetProperty("sessionID").GetString()!;
        return new EventStore(database).TransactAsync(sessionId, async (transaction, token) =>
        {
            if (!await transaction.Db.Sessions.AnyAsync(row => row.id == sessionId, token).ConfigureAwait(true)) throw new InvalidOperationException("Assistant events require an existing session.");
            if (await transaction.Db.Messages.Where(row => row.session_id == sessionId).MaxAsync(row => (long?)row.seq, token).ConfigureAwait(true) is { } seq
                && seq > await transaction.LatestSequenceAsync(sessionId, token).ConfigureAwait(true))
                throw new NotSupportedException("Unsequenced direct-SQL history requires canonical migration before assistant events.");
            return await transaction.AppendAsync(new DurableEventDefinition<JsonElement>(type, version, "sessionID",
                AssistantEventJsonContext.Default.JsonElement, ProjectAsync), encoded, token, id).ConfigureAwait(true);
        }, ct);
    }

    internal static async Task ProjectAsync(EventTransaction transaction, OpenCodeEvent committed, CancellationToken ct)
    {
        var data = JsonNode.Parse(committed.Data.GetRawText())!.AsObject();
        var sessionId = data["sessionID"]!.GetValue<string>();
        var messageId = data["assistantMessageID"]!.GetValue<string>();
        var assistant = await LoadAsync(transaction, sessionId, messageId, ct).ConfigureAwait(true);
        if (committed.Type == "session.step.started")
        {
            if (assistant is not null)
            {
                Copy(assistant, data, "agent", "model");
                foreach (var field in new[] { "retry", "error", "finish", "rawFinish", "providerState" }) assistant.Remove(field);
                var time = AssistantEventData.Object(assistant, "time");
                time.Remove("streamed");
                time.Remove("completed");
                if (data["snapshot"]?.GetValue<string>() is { Length: > 0 })
                {
                    var snapshot = assistant["snapshot"]?.DeepClone().AsObject() ?? new JsonObject();
                    snapshot["start"] = data["snapshot"]!.DeepClone();
                    assistant["snapshot"] = snapshot;
                }
                await SaveAsync(transaction, sessionId, messageId, assistant, ct).ConfigureAwait(true);
                return;
            }
            // Only the newest assistant is eligible. Do not seek an older incomplete row.
            var current = await transaction.Db.Messages.Where(row => row.session_id == sessionId && row.type == "assistant")
                .OrderByDescending(row => row.seq).Select(row => new { row.id, row.data }).FirstOrDefaultAsync(ct).ConfigureAwait(true);
            if (current is not null)
            {
                var previous = JsonNode.Parse(current.data)!.AsObject();
                if (previous["time"]?["completed"] is null)
                {
                    previous.Remove("retry");
                    AssistantEventData.Object(previous, "time")["completed"] = committed.Created;
                    await SaveAsync(transaction, sessionId, current.id, previous, ct).ConfigureAwait(true);
                }
            }
            var created = new JsonObject
            {
                ["agent"] = data["agent"]!.DeepClone(),
                ["model"] = data["model"]!.DeepClone(),
                ["time"] = new JsonObject { ["created"] = committed.Created },
                ["content"] = new JsonArray()
            };
            if (data["snapshot"]?.GetValue<string>() is { Length: > 0 })
                created["snapshot"] = new JsonObject { ["start"] = data["snapshot"]!.DeepClone() };
            await SqliteIntrinsics.InsertMessageAsync(transaction.Db, messageId, sessionId, "assistant", checked((long)committed.Durable!.Seq),
                committed.Created, transaction.Clock.GetUtcNow().ToUnixTimeMilliseconds(), created.ToJsonString(), ct).ConfigureAwait(true);
            return;
        }

        // Upstream updateOwnedAssistant ignores an absent/wrong-session assistant.
        // Usage is independently projected even when the referenced message is absent.
        if (assistant is not null)
        {
            Update(assistant, data, committed.Type, checked((long)committed.Created));
            await SaveAsync(transaction, sessionId, messageId, assistant, ct).ConfigureAwait(true);
        }
        if (committed.Type is "session.step.ended" or "session.step.failed" &&
            data.ContainsKey("cost") && data.ContainsKey("tokens"))
        {
            var tokens = AssistantEventData.Object(data, "tokens");
            var cache = AssistantEventData.Object(tokens, "cache");
            await SqliteIntrinsics.AddUsageAsync(transaction.Db, sessionId, AssistantEventData.Finite(data, "cost"),
                AssistantEventData.Finite(tokens, "input"), AssistantEventData.Finite(tokens, "output"), AssistantEventData.Finite(tokens, "reasoning"),
                AssistantEventData.Finite(cache, "read"), AssistantEventData.Finite(cache, "write"), ct).ConfigureAwait(true);
        }
    }

    private static void Update(JsonObject assistant, JsonObject data, string type, long created)
    {
        var time = AssistantEventData.Object(assistant, "time");
        var content = assistant["content"] as JsonArray ?? throw new JsonException("Assistant content must be an array.");
        // Ordinals are durable event facts, but upstream selects the latest matching
        // content here. Do not reinterpret ordinals as array indices.
        JsonObject? Latest(string kind) => content.OfType<JsonObject>().LastOrDefault(item => item["type"]?.GetValue<string>() == kind);
        var tool = type.StartsWith("session.tool.", StringComparison.Ordinal)
            ? content.OfType<JsonObject>().LastOrDefault(item => item["type"]?.GetValue<string>() == "tool" &&
                item["id"]?.GetValue<string>() == data["id"]!.GetValue<string>())
            : null;
        switch (type)
        {
            case "session.retry.scheduled":
                assistant["retry"] = new JsonObject
                {
                    ["attempt"] = data["attempt"]!.DeepClone(), ["at"] = data["at"]!.DeepClone(), ["error"] = data["error"]!.DeepClone()
                };
                break;
            case "session.step.streamed": time["streamed"] = created; break;
            case "session.step.ended":
            case "session.step.failed":
                time["completed"] = created;
                Copy(assistant, data, "rawFinish", "providerState");
                assistant["finish"] = data["finish"]?.DeepClone() ?? JsonValue.Create("error");
                if (type == "session.step.failed")
                {
                    Copy(assistant, data, "error");
                    assistant.Remove("retry");
                }
                if (data.ContainsKey("cost") && data.ContainsKey("tokens")) Copy(assistant, data, "cost", "tokens");
                if (data["snapshot"]?.GetValue<string>() is { Length: > 0 } || data.ContainsKey("files"))
                {
                    var snapshot = assistant["snapshot"]?.DeepClone().AsObject() ?? new JsonObject();
                    if (data["snapshot"] is { } end) snapshot["end"] = end.DeepClone();
                    else snapshot.Remove("end");
                    Copy(snapshot, data, "files");
                    assistant["snapshot"] = snapshot;
                }
                break;
            case "session.text.started": content.Add(new JsonObject { ["type"] = "text", ["text"] = "" }); break;
            case "session.text.ended":
                if (Latest("text") is { } text) Copy(text, data, "text", "state");
                break;
            case "session.reasoning.started":
                var reasoning = new JsonObject
                {
                    ["type"] = "reasoning", ["text"] = "", ["time"] = new JsonObject { ["created"] = created }
                };
                Copy(reasoning, data, "state");
                content.Add(reasoning);
                break;
            case "session.reasoning.ended":
                var pending = content.OfType<JsonObject>().LastOrDefault(item =>
                    item["type"]?.GetValue<string>() == "reasoning" && item["time"]?["completed"] is null);
                if (pending is not null)
                {
                    Copy(pending, data, "text");
                    pending["time"] = new JsonObject
                    {
                        ["created"] = pending["time"]?["created"]?.DeepClone() ?? JsonValue.Create(created), ["completed"] = created
                    };
                    if (data.ContainsKey("state")) Copy(pending, data, "state");
                }
                break;
            case "session.tool.input.started":
                content.Add(new JsonObject
                {
                    ["type"] = "tool", ["id"] = data["id"]!.DeepClone(), ["name"] = data["name"]!.DeepClone(),
                    ["time"] = new JsonObject { ["created"] = created },
                    ["state"] = new JsonObject { ["status"] = "streaming", ["input"] = "" }
                });
                break;
            case "session.tool.input.ended":
                if (tool?["state"]?["status"]?.GetValue<string>() == "streaming")
                    tool["state"]!["input"] = data["text"]!.DeepClone();
                break;
            case "session.tool.called":
                if (tool is not null)
                {
                    Copy(tool, data, "executed");
                    SetOptional(tool, "providerState", data["state"]);
                    AssistantEventData.Object(tool, "time")["ran"] = created;
                    tool["state"] = new JsonObject
                    {
                        ["status"] = "running", ["input"] = data["input"]!.DeepClone(), ["metadata"] = new JsonObject()
                    };
                }
                break;
            case "session.tool.success":
            case "session.tool.failed":
                var status = tool?["state"]?["status"]?.GetValue<string>();
                if (tool is null || (status != "running" && (type != "session.tool.failed" || status != "streaming"))) break;
                tool["executed"] = data["executed"]!.GetValue<bool>() || tool["executed"]?.GetValue<bool>() == true;
                SetOptional(tool, "providerResultState", data["resultState"]);
                AssistantEventData.Object(tool, "time")["completed"] = created;
                var result = new JsonObject
                {
                    ["status"] = type == "session.tool.success" ? "completed" : "error",
                    ["input"] = status == "streaming" ? new JsonObject() : tool["state"]!["input"]!.DeepClone()
                };
                Copy(result, data, "content", "metadata");
                if (type == "session.tool.failed") Copy(result, data, "error");
                tool["state"] = result;
                break;
        }
    }

    private static async Task<JsonObject?> LoadAsync(EventTransaction transaction, string sessionId, string messageId, CancellationToken ct)
    {
        if (await transaction.Db.Messages.Where(row => row.session_id == sessionId && row.id == messageId && row.type == "assistant")
            .Select(row => row.data).FirstOrDefaultAsync(ct).ConfigureAwait(true) is not { } json) return null;
        var assistant = JsonNode.Parse(json)!.AsObject();
        AssistantEventData.String(assistant, "agent");
        AssistantEventData.Model(AssistantEventData.Object(assistant, "model"));
        AssistantEventData.Finite(AssistantEventData.Object(assistant, "time"), "created");
        if (assistant["content"] is not JsonArray) throw new JsonException("Assistant content must be an array.");
        return assistant;
    }

    private static async Task SaveAsync(EventTransaction transaction, string sessionId, string messageId, JsonObject data, CancellationToken ct)
    {
        await SqliteIntrinsics.SaveAssistantAsync(transaction.Db, sessionId, messageId, data.ToJsonString(),
            AssistantEventData.Finite(AssistantEventData.Object(data, "time"), "created"), transaction.Clock.GetUtcNow().ToUnixTimeMilliseconds(), ct).ConfigureAwait(true);
    }

    private static void Copy(JsonObject target, JsonObject source, params string[] fields)
    {
        foreach (var field in fields) SetOptional(target, field, source[field]);
    }

    private static void SetOptional(JsonObject target, string field, JsonNode? value)
    {
        if (value is null) target.Remove(field);
        else target[field] = value.DeepClone();
    }
}
