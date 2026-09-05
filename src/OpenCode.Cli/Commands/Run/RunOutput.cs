namespace OpenCode.Cli.Commands.Run;

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Encodings.Web;
using OpenCode.Client;
using OpenCode.Schema;

internal sealed class RunOutput(RunOptions options, SessionId session, string directory, TimeProvider clock)
{
    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private readonly Dictionary<string, (string Id, double Time)> _starts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _text = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _reasoning = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Tool> _tools = new(StringComparer.Ordinal);
    private readonly HashSet<string> _renderedTools = new(StringComparer.Ordinal);
    private bool _blank;
    public bool Failed { get; private set; }

    public void Event(ServerEventEnvelope item)
    {
        var data = JsonNode.Parse(item.Data.GetRawText())!.AsObject();
        var time = item.Created ?? clock.GetUtcNow().ToUnixTimeMilliseconds();
        var message = data["assistantMessageID"]?.GetValue<string>() ?? "";
        var partId = PartId(item.Id.Value);
        if (item.Type == "session.step.started")
        {
            var part = Part(partId, message, "step-start");
            Copy(part, "snapshot", data["snapshot"]);
            if (!Emit("step_start", time, new() { ["part"] = part }))
            {
                Empty();
                Console.Error.WriteLine($"> {data["agent"]?.GetValue<string>()} · {data["model"]?["id"]?.GetValue<string>()}");
                Empty();
            }
            return;
        }
        if (item.Type is "session.text.started" or "session.reasoning.started")
        {
            var kind = item.Type == "session.text.started" ? "text" : "reasoning";
            _starts[kind + "\0" + Key(message, data["ordinal"]!.ToJsonString())] = (partId, time);
            return;
        }
        if (item.Type is "session.text.ended" or "session.reasoning.ended")
        {
            var kind = item.Type == "session.text.ended" ? "text" : "reasoning";
            if (kind == "reasoning" && !options.Thinking) return;
            var key = Key(message, data["ordinal"]!.ToJsonString());
            var started = _starts.GetValueOrDefault(kind + "\0" + key, (partId, time));
            _starts.Remove(kind + "\0" + key);
            var part = Part(started.Item1, message, kind);
            part["text"] = data["text"]!.GetValue<string>();
            part["time"] = new JsonObject { ["start"] = started.Item2, ["end"] = time };
            if (kind == "reasoning") Copy(part, "metadata", data["state"]);
            (kind == "text" ? _text : _reasoning)[key] = data["text"]!.GetValue<string>();
            WriteText(part, time, kind);
            return;
        }
        if (item.Type == "session.step.ended")
        {
            var part = Part(partId, message, "step-finish");
            Copy(part, "reason", data["finish"]);
            foreach (var field in new[] { "snapshot", "cost", "tokens" }) Copy(part, field, data[field]);
            Emit("step_finish", time, new() { ["part"] = part });
            return;
        }
        if (item.Type == "session.step.failed") { ExecutionError(data["error"]!.AsObject(), time); return; }
        if (!item.Type.StartsWith("session.tool.", StringComparison.Ordinal)) return;
        var toolId = data["id"]!.GetValue<string>();
        var toolKey = Key(message, toolId);
        if (item.Type == "session.tool.input.started")
        { _tools[toolKey] = new(partId, time, data["name"]!.GetValue<string>()); return; }
        if (!_tools.TryGetValue(toolKey, out var tool)) tool = new(partId, time, "tool");
        switch (item.Type)
        {
            case "session.tool.input.ended": tool.Raw = data["text"]?.GetValue<string>(); break;
            case "session.tool.input.delta": tool.Raw = (tool.Raw ?? "") + data["delta"]?.GetValue<string>(); break;
            case "session.tool.called":
                tool.Input = data["input"]!.DeepClone();
                tool.Provider = new JsonObject();
                Copy(tool.Provider, "executed", data["executed"]);
                Copy(tool.Provider, "state", data["state"]);
                break;
            case "session.tool.progress": tool.Metadata = data["metadata"]!.DeepClone(); break;
            case "session.tool.success": case "session.tool.failed":
                var success = item.Type == "session.tool.success";
                var content = data["content"];
                var state = new JsonObject { ["status"] = success ? "completed" : "error", ["input"] = tool.Input.DeepClone(),
                    ["time"] = new JsonObject { ["start"] = tool.Time, ["end"] = time } };
                var metadata = new JsonObject();
                Copy(metadata, "providerCall", tool.Provider);
                var result = new JsonObject();
                Copy(result, "executed", data["executed"]); Copy(result, "state", data["resultState"]);
                metadata["providerResult"] = result;
                if (tool.Raw is not null) metadata["rawInput"] = tool.Raw;
                if (success)
                {
                    state["output"] = ToolText(tool.Name, content);
                    state["title"] = tool.Name;
                    Copy(metadata, "metadata", data["metadata"]);
                    Copy(metadata, "content", content);
                }
                else state["error"] = data["error"]?["message"]?.GetValue<string>();
                state["metadata"] = metadata;
                var part = new JsonObject { ["partID"] = tool.PartId, ["sessionID"] = session.Value, ["messageID"] = message,
                    ["type"] = "tool", ["id"] = toolId, ["tool"] = tool.Name, ["state"] = state };
                _renderedTools.Add(toolKey);
                _tools.Remove(toolKey);
                if (!Emit("tool_use", time, new() { ["part"] = part })) ToolTextOutput(tool.Name, tool.Input,
                    data["metadata"] ?? tool.Metadata, content, success ? null : data["error"]?["message"]?.GetValue<string>());
                return;
        }
        _tools[toolKey] = tool;
    }

    public void Reconcile(SessionMessage projected)
    {
        if (projected is not AssistantMessage) return;
        var message = JsonSerializer.SerializeToNode(projected, OpenCodeJsonContext.Default.SessionMessage)!.AsObject();
        var id = projected.Id.Value;
        var time = message["time"]!["completed"]?.GetValue<double>() ?? message["time"]!["created"]!.GetValue<double>();
        var created = message["time"]!["created"]!.GetValue<double>();
        var textOrdinal = 0;
        var reasoningOrdinal = 0;
        foreach (var node in message["content"]!.AsArray())
        {
            var content = node!.AsObject();
            var kind = content["type"]!.GetValue<string>();
            if (kind is "text" or "reasoning")
            {
                var ordinal = kind == "text" ? textOrdinal++ : reasoningOrdinal++;
                if (kind == "reasoning" && !options.Thinking) continue;
                var seen = kind == "text" ? _text : _reasoning;
                var key = Key(id, ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture));
                var text = content["text"]!.GetValue<string>();
                var rendered = seen.GetValueOrDefault(key, "");
                if (text == rendered || !text.StartsWith(rendered, StringComparison.Ordinal)) continue;
                var part = Part(ProjectedPartId(id, kind + "-" + ordinal), id, kind);
                part["text"] = text[rendered.Length..];
                part["time"] = new JsonObject { ["start"] = created, ["end"] = time };
                if (kind == "reasoning") Copy(part, "metadata", content["state"]);
                seen[key] = text;
                WriteText(part, time, kind);
                continue;
            }
            var toolId = content["id"]!.GetValue<string>();
            var state = content["state"]!.AsObject();
            var status = state["status"]!.GetValue<string>();
            if (status is "streaming" or "running" || !_renderedTools.Add(Key(id, toolId))) continue;
            var name = content["name"]!.GetValue<string>();
            var output = new JsonObject { ["status"] = status, ["input"] = state["input"]!.DeepClone(),
                ["time"] = new JsonObject { ["start"] = content["time"]?["ran"]?.GetValue<double>() ?? content["time"]!["created"]!.GetValue<double>(),
                    ["end"] = content["time"]?["completed"]?.GetValue<double>() ?? time } };
            var metadata = new JsonObject();
            Copy(metadata, "metadata", state["metadata"]); Copy(metadata, "content", state["content"]);
            output["metadata"] = metadata;
            if (status == "completed") { output["output"] = ToolText(name, state["content"]); output["title"] = name; }
            else output["error"] = state["error"]?["message"]?.GetValue<string>();
            var toolPart = new JsonObject { ["partID"] = ProjectedPartId(id, "tool-" + toolId), ["sessionID"] = session.Value,
                ["messageID"] = id, ["type"] = "tool", ["id"] = toolId, ["tool"] = name, ["state"] = output };
            if (!Emit("tool_use", time, new() { ["part"] = toolPart }))
                ToolTextOutput(name, state["input"], state["metadata"], state["content"], status == "error" ? state["error"]?["message"]?.GetValue<string>() : null);
        }
        if (!Failed && message["error"] is JsonObject error) ExecutionError(error, time);
    }

    public void ExecutionError(JsonObject error, double time)
    {
        Failed = true;
        if (!Emit("error", time, new() { ["error"] = error.DeepClone() })) ErrorText(error["message"]?.GetValue<string>() ?? "");
    }
    public static void Error(string format, SessionId? session, string message, TimeProvider clock)
    {
        if (format != "json") { ErrorText(message); return; }
        Console.WriteLine(new JsonObject { ["type"] = "error", ["timestamp"] = clock.GetUtcNow().ToUnixTimeMilliseconds(),
            ["sessionID"] = session?.Value ?? "", ["error"] = new JsonObject { ["type"] = "unknown", ["message"] = message } }.ToJsonString(Json));
    }
    private bool Emit(string type, double timestamp, JsonObject fields)
    {
        if (options.Format != "json") return false;
        var envelope = new JsonObject { ["type"] = type, ["timestamp"] = timestamp, ["sessionID"] = session.Value };
        foreach (var pair in fields) envelope[pair.Key] = pair.Value?.DeepClone();
        Console.WriteLine(envelope.ToJsonString(Json));
        return true;
    }
    private void WriteText(JsonObject part, double time, string kind)
    {
        if (Emit(kind, time, new() { ["part"] = part })) return;
        var text = part["text"]!.GetValue<string>().Trim();
        if (text.Length == 0) return;
        if (kind == "reasoning") text = "Thinking: " + text;
        if (Console.IsOutputRedirected) Console.WriteLine(text);
        else
        {
            Empty();
            Console.Error.WriteLine(kind == "reasoning" ? "\x1b[90m\x1b[3m" + text + "\x1b[0m\x1b[0m" : text);
            Empty();
        }
    }
    private JsonObject Part(string id, string message, string type) => new() { ["id"] = id, ["sessionID"] = session.Value, ["messageID"] = message, ["type"] = type };
    private static void Copy(JsonObject target, string key, JsonNode? value) { if (value is not null) target[key] = value.DeepClone(); }
    private static string Key(string message, string id) => message + "\0" + id;
    private static string PartId(string id) => "prt_" + (id.StartsWith("evt_", StringComparison.Ordinal) ? id[4..] : id);
    private static string ProjectedPartId(string id, string part) => "prt_" + (id.StartsWith("msg_", StringComparison.Ordinal) ? id[4..] : id) + "_" + part;
    private static string ToolText(string name, JsonNode? content)
    {
        if (content is not JsonArray array) return "";
        var texts = array.Where(item => item?["type"]?.GetValue<string>() == "text").Select(item => item!["text"]?.GetValue<string>() ?? "");
        if (name is "shell" or "bash") return texts.FirstOrDefault() ?? "";
        var joined = string.Join("\n", texts.Where(text => text.Length != 0));
        if (name != "read" || !joined.StartsWith('{')) return joined;
        try
        {
            if (JsonNode.Parse(joined) is not JsonObject parsed) return joined;
            if (parsed["content"] is JsonValue value && value.TryGetValue<string>(out var text)
                && (parsed["type"]?.GetValue<string>() == "text-page" || parsed["encoding"]?.GetValue<string>() == "utf8")) return text;
            if (parsed["entries"] is JsonArray entries)
                return string.Join("\n", entries.Select(entry => entry is JsonValue literal && literal.TryGetValue<string>(out var item) ? item
                    : entry is JsonObject obj && obj["path"] is JsonValue path && path.TryGetValue<string>(out var file) ? file : null).OfType<string>());
        }
        catch (JsonException) { }
        return joined;
    }
    private void ToolTextOutput(string name, JsonNode? input, JsonNode? metadata, JsonNode? content, string? error)
    {
        var text = ToolText(name, content);
        if (error is null || !string.IsNullOrWhiteSpace(text))
        {
            var info = RunToolPresentation.Describe(name, input, metadata, "completed", text, directory);
            if (info.Block)
            {
                Empty();
                Console.Error.WriteLine("\x1b[0m" + info.Icon + " \x1b[0m" + info.Title);
                if (!string.IsNullOrWhiteSpace(info.Body)) Console.Error.WriteLine(info.Body);
                Empty();
            }
            else Console.Error.WriteLine("\x1b[0m" + info.Icon + " \x1b[0m" + info.Title + " "
                + (!string.IsNullOrEmpty(info.Description) ? "\x1b[90m" + info.Description + "\x1b[0m" : ""));
        }
        if (error is null) return;
        var failed = RunToolPresentation.Describe(name, input, metadata, "error", text, directory);
        Console.Error.WriteLine("\x1b[0m✗ \x1b[0m" + failed.Title + " failed");
        ErrorText(error);
    }
    private void Empty() { if (_blank) return; Console.Error.WriteLine("\x1b[0m"); _blank = true; }
    private static void ErrorText(string message) => Console.Error.WriteLine("\x1b[91m\x1b[1mError: \x1b[0m"
        + (message.StartsWith("Error: ", StringComparison.Ordinal) ? message[7..] : message));
    private sealed class Tool(string partId, double time, string name)
    {
        public string PartId { get; } = partId;
        public double Time { get; } = time;
        public string Name { get; } = name;
        public JsonNode Input { get; set; } = new JsonObject();
        public JsonNode Metadata { get; set; } = new JsonObject();
        public JsonObject? Provider { get; set; }
        public string? Raw { get; set; }
    }
}
