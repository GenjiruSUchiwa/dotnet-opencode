namespace OpenCode.Core.Session.Archive;

using System.Text.Json;
using System.Text.Json.Nodes;
using OpenCode.Schema;

/// <summary>Exact field selection from core/session/transfer.ts. Not a general
/// secret scrubber: source intentionally leaves some metadata/errors/paths intact.</summary>
public static class SessionArchiveSanitizer
{
    public static SessionTransferData Sanitize(SessionTransferData input)
    {
        var data = JsonSerializer.SerializeToNode(input, OpenCodeJsonContext.Default.SessionTransferData)!.AsObject();
        var info = data["info"]!.AsObject();
        var id = info["id"]!.GetValue<string>();
        Text(info, "title", "session-title", id);
        Metadata(info, "metadata", "session-metadata", id);
        var location = info["location"]!.AsObject();
        location["directory"] = "/" + Redact("session-directory", id, location["directory"]!.GetValue<string>());
        foreach (var pair in Objects(info["revert"]?["files"]).Select((file, index) => (file, index)))
        {
            Text(pair.file, "file", "revert-file", pair.index.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Text(pair.file, "patch", "revert-patch", pair.index.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        foreach (var message in Objects(data["messages"]))
        {
            var messageId = message["id"]!.GetValue<string>();
            Metadata(message, "metadata", "message-metadata", messageId);
            switch (message["type"]!.GetValue<string>())
            {
                case "user":
                    Text(message, "text", "text", messageId);
                    foreach (var pair in Objects(message["files"]).Select((file, index) => (file, index)))
                    {
                        var index = pair.index.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        pair.file["data"] = "";
                        pair.file["source"] = new JsonObject { ["type"] = "inline" };
                        Text(pair.file, "name", "file-name", index);
                        Text(pair.file, "description", "file-description", index);
                        if (pair.file["mention"] is JsonObject mention) Text(mention, "text", "file-mention", index);
                    }
                    foreach (var kind in new[] { "agent", "skill" })
                        foreach (var pair in Objects(message[kind + "s"]).Select((item, index) => (item, index)))
                        {
                            var index = pair.index.ToString(System.Globalization.CultureInfo.InvariantCulture);
                            Text(pair.item, "name", kind + "-name", index);
                            if (kind == "skill") Text(pair.item, "text", "skill", index);
                            if (pair.item["mention"] is JsonObject mention) Text(mention, "text", kind + "-mention", index);
                        }
                    break;
                case "synthetic":
                    Text(message, "text", "synthetic", messageId);
                    Text(message, "description", "synthetic-description", messageId);
                    break;
                case "system": case "skill":
                    Text(message, "text", message["type"]!.GetValue<string>(), messageId);
                    break;
                case "shell":
                    Text(message, "command", "shell-command", messageId);
                    if (message["output"] is JsonObject output) Text(output, "output", "shell-output", messageId);
                    break;
                case "assistant":
                    foreach (var content in Objects(message["content"]))
                    {
                        var type = content["type"]!.GetValue<string>();
                        if (type is "text" or "reasoning")
                        {
                            Text(content, "text", type, messageId);
                            State(content, "state", type + "-state", messageId);
                            continue;
                        }
                        State(content, "providerState", "tool-provider-state", messageId);
                        State(content, "providerResultState", "tool-provider-result-state", messageId);
                        var state = content["state"]!.AsObject();
                        if (state["status"]!.GetValue<string>() == "streaming")
                        {
                            Text(state, "input", "tool-input", messageId);
                            continue;
                        }
                        state["input"] = Marker("tool-input", messageId);
                        if (state["status"]!.GetValue<string>() == "running") state["metadata"] = Marker("tool-metadata", messageId);
                        else State(state, "metadata", "tool-metadata", messageId);
                        foreach (var item in Objects(state["content"]))
                        {
                            if (item["type"]!.GetValue<string>() == "text") Text(item, "text", "tool-output", messageId);
                            else
                            {
                                Text(item, "uri", "tool-file-uri", messageId);
                                Text(item, "name", "tool-file-name", messageId);
                            }
                        }
                    }
                    break;
                case "compaction":
                    if (message["status"]!.GetValue<string>() == "failed") break;
                    Text(message, "summary", "compaction-summary", messageId);
                    Text(message, "recent", "compaction-recent", messageId);
                    break;
            }
        }
        return data.Deserialize(OpenCodeJsonContext.Default.SessionTransferData)!;
    }

    private static IEnumerable<JsonObject> Objects(JsonNode? value) => value is JsonArray array ? array.Select(item => item!.AsObject()) : [];
    private static JsonObject Marker(string kind, string id) => new() { ["redacted"] = kind + ":" + id };
    private static string Redact(string kind, string id, string value) => value.Trim().Length == 0 ? value : $"[redacted:{kind}:{id}]";
    private static void Text(JsonObject obj, string property, string kind, string id)
    {
        if (obj[property] is { } value) obj[property] = Redact(kind, id, value.GetValue<string>());
    }
    private static void Metadata(JsonObject obj, string property, string kind, string id)
    {
        if (obj[property] is JsonObject { Count: > 0 }) obj[property] = Marker(kind, id);
    }
    private static void State(JsonObject obj, string property, string kind, string id)
    {
        if (obj[property] is not null) obj[property] = Marker(kind, id);
    }
}
