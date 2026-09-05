namespace OpenCode.Core.Llm;

using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

internal sealed class ResponsesStreamParser : LlmStreamParser
{
    private sealed class Message
    {
        internal JsonElement? Phase;
        internal bool Open;
        internal bool Closed;
        internal bool HasDelta;
    }
    private sealed class Summary(string id)
    {
        internal string Id { get; } = id;
        internal bool Open = true;
        internal bool HasDelta;
    }
    private sealed class ReasoningItem
    {
        internal bool Open = true;
        internal JsonElement? Encrypted;
        internal readonly SortedDictionary<int, Summary> Parts = new();
    }
    private sealed class Tool(string callId)
    {
        internal string CallId { get; } = callId;
        internal string? ItemId;
        internal string? Name;
        internal bool Started;
        internal readonly StringBuilder Input = new();
    }

    private readonly Dictionary<int, string> _outputs = new();
    private readonly Dictionary<string, Message> _messages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ReasoningItem> _reasoning = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Tool> _tools = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _toolItems = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _completedTools = new(StringComparer.Ordinal);
    private string? _message;
    private bool _hasFunctionCall;
    internal bool IsTerminal { get; private set; }
    internal override bool HandlesProviderErrors => true;

    internal override IReadOnlyList<LlmEvent> Step(JsonElement root)
    {
        var type = String(Get(root, "type")) ?? throw Invalid("Responses event type is missing.");
        if (IsTerminal) throw Invalid("Responses received an event after completion.");
        var events = new List<LlmEvent>();
        if (type is "error" or "response.failed") throw Failure(root);
        switch (type)
        {
            case "response.output_item.added":
                var item = Get(root, "item");
                if (Missing(item)) break;
                RequireObject(item);
                var itemType = String(Get(item, "type")) ?? throw Invalid("Responses output item type is missing.");
                var id = String(Get(item, "id")) ?? (itemType == "function_call" ? String(Get(item, "call_id")) : null);
                if (!Missing(Get(root, "output_index")) && id is not null) _outputs[Index(Get(root, "output_index"))] = id;
                if (itemType == "message") AddMessage(id ?? throw Invalid("Responses message ID is missing."), item, events);
                else if (itemType == "reasoning") AddReasoning(id ?? throw Invalid("Responses reasoning ID is missing."), item, events);
                else if (itemType == "function_call") AddTool(item, events);
                else UnsupportedItem(item, events);
                break;
            case "response.output_item.done":
                DoneItem(Get(root, "item"), events);
                break;
            case "response.output_text.delta":
            case "response.refusal.delta":
                TextDelta(ItemId(root), String(Get(root, "delta")), events);
                break;
            case "response.output_text.done":
            case "response.refusal.done":
                var textId = ItemId(root);
                if (_messages.TryGetValue(textId, out var message) && !message.HasDelta && !message.Closed)
                    TextDelta(textId, String(Get(root, type == "response.refusal.done" ? "refusal" : "text")), events);
                break;
            case "response.reasoning.delta":
            case "response.reasoning_summary_text.delta":
            case "response.reasoning_text.delta":
                ReasoningDelta(ItemId(root), SummaryIndex(root), String(Get(root, "delta")), events);
                break;
            case "response.reasoning.done":
            case "response.reasoning_summary_text.done":
            case "response.reasoning_text.done":
                var reasoningId = ItemId(root);
                var summary = SummaryIndex(root);
                if (_reasoning.TryGetValue(reasoningId, out var reasoning) && reasoning.Open
                    && (!reasoning.Parts.TryGetValue(summary, out var part) || !part.HasDelta))
                    ReasoningDelta(reasoningId, summary, String(Get(root, "text")), events);
                break;
            case "response.reasoning_summary_part.added":
                var summaryId = ItemId(root);
                if (!Missing(Get(root, "summary_index"))) StartSummary(summaryId, SummaryIndex(root), events);
                break;
            case "response.reasoning_summary_part.done":
                _ = ItemId(root);
                // A part-done is not the reasoning-item boundary; the next part or
                // output_item.done supplies closure and the final encrypted state.
                break;
            case "response.function_call_arguments.delta":
            case "response.function_call_arguments.done":
                Arguments(ItemId(root), type.EndsWith(".done", StringComparison.Ordinal), root, events);
                break;
            case "response.completed":
            case "response.incomplete":
                CompleteResponse(root, type == "response.completed", events);
                break;
            case "response.created":
            case "response.in_progress":
            case "response.queued":
            case "response.content_part.added":
            case "response.content_part.done":
                break;
            default:
                events.Add(new LlmEvent.ProviderState(root.Clone()) { ProviderMetadata = Metadata("openai", root) });
                break;
        }
        return events;
    }

    private void AddMessage(string id, JsonElement item, List<LlmEvent> events)
    {
        foreach (var (key, previous) in _messages)
            if (key != id && previous.Open) CloseMessage(key, previous, null, events);
        if (!_messages.TryGetValue(id, out var message)) _messages[id] = message = new Message();
        if (message.Closed) return;
        message.Phase = Phase(item) ?? message.Phase;
        _message = id;
    }

    private void TextDelta(string id, string? text, List<LlmEvent> events)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (_message != id || !_messages.TryGetValue(id, out var message) || message.Closed) throw Invalid("Responses text has no current output message.");
        Start(events);
        if (!message.Open) events.Add(new LlmEvent.TextStart(id) { ProviderMetadata = MessageMetadata(id, message.Phase) });
        message.Open = true;
        message.HasDelta = true;
        events.Add(new LlmEvent.TextDelta(id, text));
    }

    private void CloseMessage(string id, Message message, string? text, List<LlmEvent> events)
    {
        if (message.Closed) return;
        if (!message.Open && !string.IsNullOrEmpty(text))
        {
            Start(events);
            events.Add(new LlmEvent.TextStart(id) { ProviderMetadata = MessageMetadata(id, message.Phase) });
            message.Open = true;
        }
        if (message.Open) events.Add(new LlmEvent.TextEnd(id, text) { ProviderMetadata = MessageMetadata(id, message.Phase) });
        message.Open = false;
        message.Closed = true;
        if (_message == id) _message = null;
    }

    private void AddReasoning(string id, JsonElement item, List<LlmEvent> events)
    {
        if (_reasoning.ContainsKey(id)) return;
        if (_reasoning.Values.Any(value => value.Open)) throw Invalid("Responses started reasoning before the previous item ended.");
        var reasoning = new ReasoningItem { Encrypted = Encrypted(item) };
        reasoning.Parts.Add(0, new Summary(id + ":0"));
        _reasoning.Add(id, reasoning);
        Start(events);
        events.Add(new LlmEvent.ReasoningStart(id + ":0") { ProviderMetadata = ReasoningMetadata(id, reasoning.Encrypted) });
    }

    private void StartSummary(string id, int index, List<LlmEvent> events)
    {
        if (!_reasoning.TryGetValue(id, out var item) || !item.Open) return;
        if (item.Parts.ContainsKey(index)) return;
        foreach (var part in item.Parts.Values)
            if (part.Open)
            {
                events.Add(new LlmEvent.ReasoningEnd(part.Id) { ProviderMetadata = Metadata("openai", JsonSerializer.SerializeToElement(new { itemId = id })) });
                part.Open = false;
            }
        var summary = new Summary(id + ":" + index.ToString(System.Globalization.CultureInfo.InvariantCulture));
        item.Parts.Add(index, summary);
        Start(events);
        events.Add(new LlmEvent.ReasoningStart(summary.Id) { ProviderMetadata = ReasoningMetadata(id, item.Encrypted) });
    }

    private void ReasoningDelta(string id, int index, string? text, List<LlmEvent> events)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (!_reasoning.TryGetValue(id, out var item) || !item.Open) throw Invalid("Responses reasoning has no open item.");
        StartSummary(id, index, events);
        var part = item.Parts[index];
        if (!part.Open) return;
        part.HasDelta = true;
        events.Add(new LlmEvent.ReasoningDelta(part.Id, text));
    }

    private void DoneReasoning(string id, JsonElement item, List<LlmEvent> events)
    {
        if (_reasoning.TryGetValue(id, out var existing) && !existing.Open) return;
        var summaries = Parts(Get(item, "summary"), "summary_text", "text");
        var content = Parts(Get(item, "content"), "reasoning_text", "text");
        var full = JoinReasoning(summaries) ?? JoinReasoning(content);
        if (existing is null)
        {
            existing = new ReasoningItem { Encrypted = Encrypted(item) };
            existing.Parts.Add(0, new Summary(id));
            _reasoning[id] = existing;
            Start(events);
            events.Add(new LlmEvent.ReasoningStart(id) { ProviderMetadata = ReasoningMetadata(id, existing.Encrypted) });
        }
        else if (item.TryGetProperty("encrypted_content", out _)) existing.Encrypted = Encrypted(item);
        foreach (var (index, part) in existing.Parts)
        {
            if (!part.Open) continue;
            var text = existing.Parts.Count == 1 ? full : index < summaries.Count ? summaries[index] : null;
            events.Add(new LlmEvent.ReasoningEnd(part.Id, string.IsNullOrEmpty(text) ? null : text) { ProviderMetadata = ReasoningMetadata(id, existing.Encrypted) });
            part.Open = false;
        }
        existing.Open = false;
    }

    private Tool RegisterTool(JsonElement item, List<LlmEvent> events)
    {
        var callId = String(Get(item, "call_id"));
        if (string.IsNullOrEmpty(callId)) throw Invalid("Responses function call_id is missing.");
        var name = String(Get(item, "name"));
        var itemId = String(Get(item, "id"));
        if (!_tools.TryGetValue(callId, out var tool)) _tools.Add(callId, tool = new Tool(callId));
        if (!string.IsNullOrEmpty(name) && tool.Name is not null && tool.Name != name) throw Invalid("Responses function name changed for a call ID.");
        if (!string.IsNullOrEmpty(name)) tool.Name = name;
        if (itemId is not null)
        {
            if (_toolItems.TryGetValue(itemId, out var previous) && previous != callId && _tools.ContainsKey(previous))
                throw Invalid("Responses reused an item ID for a pending function call.");
            _toolItems[itemId] = callId;
            tool.ItemId = itemId;
        }
        else _toolItems.TryAdd(callId, callId);
        if (!tool.Started && tool.Name is not null)
        {
            Start(events);
            events.Add(new LlmEvent.ToolInputStart(callId, tool.Name) { ProviderMetadata = ToolMetadata(tool) });
            tool.Started = true;
        }
        return tool;
    }

    private void AddTool(JsonElement item, List<LlmEvent> events)
    {
        var callId = String(Get(item, "call_id")) ?? throw Invalid("Responses function call_id is missing.");
        if (_completedTools.ContainsKey(callId)) return;
        var already = _tools.ContainsKey(callId);
        var tool = RegisterTool(item, events);
        if (!already && String(Get(item, "arguments")) is { } arguments) tool.Input.Append(arguments);
    }

    private void Arguments(string itemId, bool done, JsonElement root, List<LlmEvent> events)
    {
        if (!_toolItems.TryGetValue(itemId, out var callId) || !_tools.TryGetValue(callId, out var tool)) return;
        var value = String(Get(root, done ? "arguments" : "delta"));
        if (value is null) return;
        if (done)
        {
            var current = tool.Input.ToString();
            if (!value.StartsWith(current, StringComparison.Ordinal)) { tool.Input.Clear().Append(value); return; }
            value = value[current.Length..];
        }
        if (value.Length == 0) return;
        tool.Input.Append(value);
        if (tool.Started) events.Add(new LlmEvent.ToolInputDelta(callId, tool.Name!, value, ParseInput(tool.Input.ToString())));
    }

    private void DoneTool(JsonElement item, List<LlmEvent> events)
    {
        var callId = String(Get(item, "call_id"));
        var name = String(Get(item, "name"));
        if (string.IsNullOrEmpty(callId) || string.IsNullOrEmpty(name)) throw Invalid("Responses completed function identity is missing.");
        if (_completedTools.TryGetValue(callId, out var completedName))
        {
            if (completedName != name) throw Invalid("Responses completed call identity changed.");
            return;
        }
        var tool = RegisterTool(item, events);
        if (String(Get(item, "arguments")) is { } final) tool.Input.Clear().Append(final);
        FinishTool(tool, events, String(Get(item, "status")) is "incomplete" or "failed");
    }

    private void FinishTool(Tool tool, List<LlmEvent> events, bool incomplete)
    {
        var raw = tool.Input.ToString();
        if (!tool.Started || tool.Name is null)
        {
            if (!incomplete) throw Invalid("Responses function call is missing its name.");
            events.Add(new LlmEvent.ProviderState(JsonSerializer.SerializeToElement(new { call_id = tool.CallId, id = tool.ItemId, name = tool.Name, arguments = raw })));
            _tools.Remove(tool.CallId);
            return;
        }
        events.Add(new LlmEvent.ToolInputEnd(tool.CallId, tool.Name) { ProviderMetadata = ToolMetadata(tool) });
        if (incomplete || ParseInput(raw) is not { } input)
            events.Add(new LlmEvent.ToolInputError(tool.CallId, tool.Name, raw) { ProviderMetadata = ToolMetadata(tool) });
        else events.Add(new LlmEvent.ToolCall(tool.CallId, tool.Name, input) { ProviderMetadata = ToolMetadata(tool) });
        _tools.Remove(tool.CallId);
        _completedTools[tool.CallId] = tool.Name;
        if (!incomplete) _hasFunctionCall = true;
    }

    private void DoneItem(JsonElement item, List<LlmEvent> events)
    {
        if (Missing(item)) return;
        RequireObject(item);
        var kind = String(Get(item, "type"));
        if (kind == "function_call") { DoneTool(item, events); return; }
        if (kind == "reasoning") { DoneReasoning(String(Get(item, "id")) ?? throw Invalid("Responses reasoning ID is missing."), item, events); return; }
        if (kind != "message") { UnsupportedItem(item, events); return; }
        var id = String(Get(item, "id")) ?? throw Invalid("Responses message ID is missing.");
        if (!_messages.TryGetValue(id, out var message)) _messages[id] = message = new Message();
        message.Phase = Phase(item) ?? message.Phase;
        var parts = Get(item, "content");
        var text = new List<string>();
        if (!Missing(parts))
        {
            if (parts.ValueKind != JsonValueKind.Array) throw Invalid("Responses message content must be an array.");
            foreach (var part in parts.EnumerateArray())
            {
                var type = String(Get(part, "type"));
                if (type is "output_text" or "refusal") text.Add(String(Get(part, type == "refusal" ? "refusal" : "text")) ?? throw Invalid("Responses text content is missing."));
                else events.Add(new LlmEvent.ProviderState(part.Clone()) { ProviderMetadata = Metadata("openai", part) });
            }
        }
        CloseMessage(id, message, text.Count == 0 ? null : string.Concat(text), events);
    }

    private void CompleteResponse(JsonElement root, bool completed, List<LlmEvent> events)
    {
        var response = Get(root, "response");
        if (!Missing(response)) RequireObject(response);
        if (!Missing(Get(response, "error"))) throw Failure(root);
        var output = Get(response, "output");
        if (!Missing(output))
        {
            if (output.ValueKind != JsonValueKind.Array) throw Invalid("Responses final output must be an array.");
            foreach (var item in output.EnumerateArray())
            {
                var kind = String(Get(item, "type"));
                if (kind == "function_call")
                {
                    var callId = String(Get(item, "call_id"));
                    if (callId is null || _completedTools.ContainsKey(callId)) continue;
                    if (completed) DoneTool(item, events);
                    else if (_tools.TryGetValue(callId, out var pending))
                    {
                        // An incomplete envelope may improve diagnostics, but never
                        // authorizes a pending function even when its JSON is complete.
                        if (String(Get(item, "arguments")) is { } final) pending.Input.Clear().Append(final);
                    }
                    else events.Add(new LlmEvent.ProviderState(item.Clone()) { ProviderMetadata = Metadata("openai", item) });
                }
                else if (kind is "message" or "reasoning") DoneItem(item, events);
                else UnsupportedItem(item, events);
            }
        }
        foreach (var tool in _tools.Values.ToArray()) FinishTool(tool, events, incomplete: !completed);
        foreach (var (id, item) in _reasoning)
        {
            if (!item.Open) continue;
            foreach (var part in item.Parts.Values)
                if (part.Open)
                {
                    events.Add(new LlmEvent.ReasoningEnd(part.Id) { ProviderMetadata = ReasoningMetadata(id, item.Encrypted) });
                    part.Open = false;
                }
            item.Open = false;
        }
        foreach (var (id, message) in _messages) if (message.Open) CloseMessage(id, message, null, events);
        var usage = Get(response, "usage");
        if (!Missing(usage))
        {
            RequireObject(usage);
            var input = Number(Get(usage, "input_tokens"));
            var tokens = Number(Get(usage, "output_tokens"));
            var read = Number(Get(Get(usage, "input_tokens_details"), "cached_tokens"));
            var write = Number(Get(Get(usage, "input_tokens_details"), "cache_write_tokens"));
            Usage = new LlmUsage(input, tokens, input is null ? null : Math.Max(0, input.Value - (read ?? 0) - (write ?? 0)),
                read, write, Number(Get(Get(usage, "output_tokens_details"), "reasoning_tokens")), Total(input, tokens, Number(Get(usage, "total_tokens"))))
            { ProviderMetadata = Metadata("openai", usage) };
        }
        var raw = String(Get(Get(response, "incomplete_details"), "reason"));
        var normalized = raw switch
        {
            "max_output_tokens" => LlmFinishReason.Length,
            "content_filter" => LlmFinishReason.ContentFilter,
            null => _hasFunctionCall ? LlmFinishReason.ToolCalls : completed ? LlmFinishReason.Stop : LlmFinishReason.Unknown,
            _ => _hasFunctionCall ? LlmFinishReason.ToolCalls : LlmFinishReason.Unknown
        };
        Reason = new LlmFinish(normalized, raw);
        var metadata = new JsonObject();
        if (String(Get(response, "id")) is { } responseId) metadata["responseId"] = responseId;
        if (String(Get(response, "service_tier")) is { } tier) metadata["serviceTier"] = tier;
        Finish(events, metadata.Count == 0 ? ImmutableDictionary<string, JsonElement>.Empty : Metadata("openai", JsonSerializer.SerializeToElement(metadata)));
        IsTerminal = true;
    }

    private static void UnsupportedItem(JsonElement item, List<LlmEvent> events)
    {
        var kind = String(Get(item, "type"));
        if (kind is "web_search_call" or "web_search_preview_call" or "file_search_call" or "code_interpreter_call"
            or "computer_call" or "image_generation_call" or "mcp_call")
            throw new LlmException(new LlmFailure.Unsupported("Provider-hosted Responses tools require a separate adapter; they are not local function calls."));
        events.Add(new LlmEvent.ProviderState(item.Clone()) { ProviderMetadata = Metadata("openai", item) });
    }

    private string ItemId(JsonElement root)
    {
        var id = String(Get(root, "item_id")) ?? throw Invalid("Responses event item_id is missing.");
        return !Missing(Get(root, "output_index")) && _outputs.TryGetValue(Index(Get(root, "output_index")), out var mapped) ? mapped : id;
    }
    private static int SummaryIndex(JsonElement root) => Missing(Get(root, "summary_index")) ? 0 : Index(Get(root, "summary_index"));
    private static int Index(JsonElement value) { var index = value.GetInt32(); return index >= 0 ? index : throw Invalid("Responses index must not be negative."); }
    private static void RequireObject(JsonElement value) { if (value.ValueKind != JsonValueKind.Object) throw Invalid("Responses expected an object."); }
    private static JsonElement? Phase(JsonElement item)
    {
        var phase = Get(item, "phase");
        return phase.ValueKind == JsonValueKind.Null || phase.ValueKind == JsonValueKind.String && phase.GetString() is "commentary" or "final_answer" ? phase.Clone() : null;
    }
    private static JsonElement? Encrypted(JsonElement item)
    {
        var value = Get(item, "encrypted_content");
        if (value.ValueKind == JsonValueKind.Undefined) return null;
        if (value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)) throw Invalid("Invalid Responses encrypted reasoning state.");
        return value.Clone();
    }
    private static ImmutableDictionary<string, JsonElement> MessageMetadata(string id, JsonElement? phase)
    {
        var value = new JsonObject { ["itemId"] = id };
        if (phase is { } supplied) value["phase"] = JsonNode.Parse(supplied.GetRawText());
        return Metadata("openai", JsonSerializer.SerializeToElement(value));
    }
    private static ImmutableDictionary<string, JsonElement> ReasoningMetadata(string id, JsonElement? encrypted) =>
        Metadata("openai", JsonSerializer.SerializeToElement(new JsonObject { ["itemId"] = id,
            ["reasoningEncryptedContent"] = encrypted is { } value ? JsonNode.Parse(value.GetRawText()) : null }));
    private static ImmutableDictionary<string, JsonElement> ToolMetadata(Tool tool) => tool.ItemId is null
        ? ImmutableDictionary<string, JsonElement>.Empty : Metadata("openai", JsonSerializer.SerializeToElement(new { itemId = tool.ItemId }));
    private static List<string?> Parts(JsonElement parts, string type, string field)
    {
        if (Missing(parts)) return [];
        if (parts.ValueKind != JsonValueKind.Array) throw Invalid("Responses item content must be an array.");
        return parts.EnumerateArray().Select(part => String(Get(part, "type")) == type ? String(Get(part, field)) : null).ToList();
    }
    private static string? JoinReasoning(List<string?> parts) => parts.Any(part => !string.IsNullOrEmpty(part))
        ? string.Join("\n\n", parts.OfType<string>()) : null;
    private static JsonElement? ParseInput(string input)
    {
        try { using var document = JsonDocument.Parse(input.Length == 0 ? "{}" : input); return document.RootElement.Clone(); }
        catch (JsonException) { return null; }
    }
    private static LlmException Failure(JsonElement root)
        => new(FailureReason(root, null));

    internal static LlmFailure HttpFailure(LlmHttpContext context, string body)
        => ProviderFailure.Classify($"Provider request failed with HTTP {(int)context.Status}.", (int)context.Status, body, http: context);

    private static LlmFailure FailureReason(JsonElement root, int? httpStatus)
    {
        var error = Get(root, "error");
        if (Missing(error)) error = Get(Get(root, "response"), "error");
        var code = (ErrorCode(Get(root, "code")) ?? ErrorCode(Get(error, "code")) ?? ErrorCode(Get(error, "type")))?.ToLowerInvariant();
        var statusValue = Get(root, "status");
        if (statusValue.ValueKind != JsonValueKind.Number) statusValue = Get(root, "status_code");
        var status = httpStatus is { } observed ? observed
            : statusValue.ValueKind == JsonValueKind.Number ? statusValue.GetDouble() : (double?)null;
        // Stateful transport recovery is not enabled by a classifier alone.
        if (code == "previous_response_not_found")
            return new LlmFailure.InvalidRequest("Responses continuation was rejected; automatic recovery is not implemented.");
        return ProviderFailure.Classify("Responses reported a failure.",
            status is >= int.MinValue and <= int.MaxValue && status == Math.Truncate(status.Value) ? (int)status.Value : null,
            root.ValueKind == JsonValueKind.Undefined ? null : root.GetRawText());
    }

    private static string? ErrorCode(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    internal override IReadOnlyList<LlmEvent> Complete()
    {
        if (!IsTerminal) throw Invalid("Responses stream ended without a terminal event.", true);
        return [];
    }
}
