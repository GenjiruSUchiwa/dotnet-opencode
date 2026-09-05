namespace OpenCode.Core.Llm;

using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenCode.Core.Config;

internal sealed class AnthropicStreamParser : LlmStreamParser
{
    internal static readonly IReadOnlySet<string> EventNames = new HashSet<string>(StringComparer.Ordinal)
    { "message", "message_start", "message_delta", "message_stop", "content_block_start", "content_block_delta", "content_block_stop", "ping", "error" };
    internal override bool HandlesProviderErrors => true;

    private sealed class Block(string kind)
    {
        internal string Kind { get; } = kind;
        internal string? Id;
        internal string? Name;
        internal string? Signature;
        internal string? RedactedData;
        internal readonly StringBuilder Input = new();
        internal bool Hosted => Kind == "server_tool_use";
    }

    private readonly SortedDictionary<int, Block> _blocks = new();
    private readonly HashSet<int> _indices = new();
    private readonly HashSet<string> _toolIds = new(StringComparer.Ordinal);
    private readonly JsonObject _rawUsage = new();
    private string? _stopSequence;
    private bool _stopped;

    internal override IReadOnlyList<LlmEvent> Step(JsonElement root)
    {
        var type = String(Get(root, "type")) ?? throw Invalid("Anthropic event type is missing.");
        var events = new List<LlmEvent>();
        if (type is "ping" or "message") return events;
        if (!EventNames.Contains(type)) return events;
        if (_stopped) throw Invalid("Anthropic received an event after message_stop.");
        switch (type)
        {
            case "message_start":
                UpdateUsage(Get(Get(root, "message"), "usage"));
                break;
            case "content_block_start":
                StartBlock(root, events);
                break;
            case "content_block_delta":
                Delta(root, events);
                break;
            case "content_block_stop":
                if (!Missing(Get(root, "index"))) CloseBlock(Get(root, "index").GetInt32(), events, incomplete: false);
                break;
            case "message_delta":
                var delta = Get(root, "delta");
                if (delta.ValueKind != JsonValueKind.Object) throw Invalid("Anthropic message_delta requires a delta object.");
                UpdateUsage(Get(root, "usage"));
                if (String(Get(delta, "stop_reason")) is { } reason)
                    Reason = new LlmFinish(reason switch
                    {
                        "end_turn" or "stop_sequence" or "pause_turn" => LlmFinishReason.Stop,
                        "max_tokens" or "model_context_window_exceeded" => LlmFinishReason.Length,
                        "tool_use" => LlmFinishReason.ToolCalls,
                        "refusal" => LlmFinishReason.ContentFilter,
                        _ => LlmFinishReason.Unknown
                    }, reason);
                _stopSequence = String(Get(delta, "stop_sequence")) ?? _stopSequence;
                break;
            case "message_stop":
                foreach (var index in _blocks.Keys.ToArray())
                    CloseBlock(index, events, Reason?.Normalized is LlmFinishReason.Length or LlmFinishReason.ContentFilter);
                Finish(events, _stopSequence is null ? ImmutableDictionary<string, JsonElement>.Empty
                    : Metadata("anthropic", JsonSerializer.SerializeToElement(new { stopSequence = _stopSequence })));
                _stopped = true;
                break;
            case "error":
                // Decode attaches the entire frame and observed HTTP context. Never
                // echo arbitrary provider error text (which can contain credentials).
                throw new LlmException(ProviderFailure.Classify("Anthropic reported a provider error.", body: root.GetRawText()));
        }
        return events;
    }

    private void StartBlock(JsonElement root, List<LlmEvent> events)
    {
        var value = Get(root, "content_block");
        if (value.ValueKind != JsonValueKind.Object) throw Invalid("Anthropic content_block must be an object.");
        var kind = String(Get(value, "type")) ?? throw Invalid("Anthropic content block type is missing.");
        var rawIndex = Get(root, "index");
        if (Missing(rawIndex) && kind is "tool_use" or "server_tool_use") throw Invalid("Anthropic tool block index is missing.");
        var index = Missing(rawIndex) ? 0 : rawIndex.GetInt32();
        if (index < 0 || !_indices.Add(index)) throw Invalid("Anthropic content block index is invalid or duplicated.");
        var block = new Block(kind);
        _blocks.Add(index, block);
        switch (kind)
        {
            case "text":
                var text = String(Get(value, "text")) ?? throw Invalid("Anthropic text block is missing text.");
                Start(events);
                events.Add(new LlmEvent.TextStart($"text-{index}"));
                if (text.Length > 0) events.Add(new LlmEvent.TextDelta($"text-{index}", text));
                break;
            case "thinking":
                var thinking = String(Get(value, "thinking")) ?? throw Invalid("Anthropic thinking block is missing thinking text.");
                block.Signature = String(Get(value, "signature"));
                Start(events);
                events.Add(new LlmEvent.ReasoningStart($"reasoning-{index}") { ProviderMetadata = ReplayMetadata(block) });
                if (thinking.Length > 0) events.Add(new LlmEvent.ReasoningDelta($"reasoning-{index}", thinking) { ProviderMetadata = ReplayMetadata(block) });
                break;
            case "redacted_thinking":
                block.RedactedData = String(Get(value, "data")) ?? throw Invalid("Anthropic redacted thinking is missing its replay payload.");
                Start(events);
                events.Add(new LlmEvent.ReasoningStart($"reasoning-{index}") { ProviderMetadata = ReplayMetadata(block) });
                break;
            case "tool_use":
            case "server_tool_use":
                block.Id = String(Get(value, "id"));
                block.Name = String(Get(value, "name"));
                if (string.IsNullOrEmpty(block.Id) || string.IsNullOrEmpty(block.Name) || !_toolIds.Add(block.Id))
                    throw Invalid("Anthropic tool identity is missing or duplicated.");
                var input = Get(value, "input");
                if (input.ValueKind != JsonValueKind.Undefined && (input.ValueKind != JsonValueKind.Object || input.EnumerateObject().Any()))
                    block.Input.Append(input.GetRawText());
                Start(events);
                events.Add(new LlmEvent.ToolInputStart(block.Id, block.Name, block.Hosted));
                break;
            case "web_search_tool_result":
            case "code_execution_tool_result":
            case "web_fetch_tool_result":
                var id = String(Get(value, "tool_use_id")) ?? throw Invalid("Hosted tool result is missing its call ID.");
                var result = Get(value, "content");
                if (result.ValueKind == JsonValueKind.Undefined) throw Invalid("Hosted tool result is missing content.");
                var name = kind[..^"_tool_result".Length];
                var error = String(Get(result, "type"))?.EndsWith("_tool_result_error", StringComparison.Ordinal) == true;
                Start(events);
                events.Add(new LlmEvent.ToolResult(id, name, error ? new LlmToolResult.Error(result.Clone()) : new LlmToolResult.Json(result.Clone()), true)
                { ProviderMetadata = Metadata("anthropic", JsonSerializer.SerializeToElement(new { blockType = kind, result })) });
                break;
            default:
                Start(events);
                events.Add(new LlmEvent.ProviderState(value.Clone()) { ProviderMetadata = Metadata("anthropic", value) });
                break;
        }
    }

    private void Delta(JsonElement root, List<LlmEvent> events)
    {
        var delta = Get(root, "delta");
        if (delta.ValueKind != JsonValueKind.Object) throw Invalid("Anthropic block delta must be an object.");
        var kind = String(Get(delta, "type"));
        if (kind is not ("text_delta" or "thinking_delta" or "signature_delta" or "input_json_delta"))
        {
            Start(events);
            events.Add(new LlmEvent.ProviderState(delta.Clone()) { ProviderMetadata = Metadata("anthropic", delta) });
            return;
        }
        var rawIndex = Get(root, "index");
        if (kind == "input_json_delta" && Missing(rawIndex)) throw Invalid("Anthropic tool delta requires an index.");
        var index = Missing(rawIndex) ? 0 : rawIndex.GetInt32();
        if (!_blocks.TryGetValue(index, out var block)) throw Invalid("Anthropic delta has no open content block.");
        switch (kind)
        {
            case "text_delta" when block.Kind == "text":
                if (String(Get(delta, "text")) is { Length: > 0 } text) events.Add(new LlmEvent.TextDelta($"text-{index}", text));
                return;
            case "thinking_delta" when block.Kind == "thinking":
                if (String(Get(delta, "thinking")) is { Length: > 0 } thinking) events.Add(new LlmEvent.ReasoningDelta($"reasoning-{index}", thinking));
                return;
            case "signature_delta" when block.Kind == "thinking":
                // The upstream protocol treats each signature delta as authoritative.
                if (String(Get(delta, "signature")) is { Length: > 0 } signature) block.Signature = signature;
                return;
            case "input_json_delta" when block.Kind is "tool_use" or "server_tool_use":
                if (String(Get(delta, "partial_json")) is not { Length: > 0 } fragment) return;
                block.Input.Append(fragment);
                events.Add(new LlmEvent.ToolInputDelta(block.Id!, block.Name!, fragment, ParseInput(block.Input.ToString())));
                return;
            default:
                throw Invalid("Anthropic delta does not match its open content block.");
        }
    }

    private void CloseBlock(int index, List<LlmEvent> events, bool incomplete)
    {
        if (!_blocks.Remove(index, out var block)) return;
        if (block.Kind == "text") events.Add(new LlmEvent.TextEnd($"text-{index}"));
        if (block.Kind is "thinking" or "redacted_thinking")
            events.Add(new LlmEvent.ReasoningEnd($"reasoning-{index}") { ProviderMetadata = ReplayMetadata(block) });
        if (block.Kind is not ("tool_use" or "server_tool_use")) return;
        var raw = block.Input.ToString();
        events.Add(new LlmEvent.ToolInputEnd(block.Id!, block.Name!));
        if (incomplete)
        {
            events.Add(new LlmEvent.ToolInputError(block.Id!, block.Name!, raw));
            return;
        }
        if (ParseInput(raw) is not { } input)
        {
            if (block.Hosted) throw Invalid("Anthropic hosted tool arguments are invalid JSON.");
            events.Add(new LlmEvent.ToolInputError(block.Id!, block.Name!, raw));
            return;
        }
        events.Add(new LlmEvent.ToolCall(block.Id!, block.Name!, input, block.Hosted));
    }

    private void UpdateUsage(JsonElement value)
    {
        if (Missing(value)) return;
        if (value.ValueKind != JsonValueKind.Object) throw Invalid("Anthropic usage must be an object.");
        var nonCached = Number(Get(value, "input_tokens")) ?? Usage?.NonCachedInputTokens;
        var read = Number(Get(value, "cache_read_input_tokens")) ?? Usage?.CacheReadInputTokens;
        var write = Number(Get(value, "cache_creation_input_tokens")) ?? Usage?.CacheWriteInputTokens;
        var output = Number(Get(value, "output_tokens")) ?? Usage?.OutputTokens;
        var reasoning = Number(Get(Get(value, "output_tokens_details"), "thinking_tokens")) ?? Usage?.ReasoningTokens;
        var input = nonCached is null && read is null && write is null ? (double?)null : (nonCached ?? 0) + (read ?? 0) + (write ?? 0);
        ConfigLoader.MergeOverlay(_rawUsage, JsonNode.Parse(value.GetRawText())!.AsObject());
        Usage = new LlmUsage(input, output, nonCached, read, write, reasoning, Total(input, output, null))
        { ProviderMetadata = Metadata("anthropic", JsonSerializer.SerializeToElement(_rawUsage)) };
    }

    private static ImmutableDictionary<string, JsonElement> ReplayMetadata(Block block)
    {
        var value = new JsonObject();
        if (block.Signature is not null) value["signature"] = block.Signature;
        if (block.RedactedData is not null) value["redactedData"] = block.RedactedData;
        return value.Count == 0 ? ImmutableDictionary<string, JsonElement>.Empty : Metadata("anthropic", JsonSerializer.SerializeToElement(value));
    }

    private static JsonElement? ParseInput(string input)
    {
        try { using var document = JsonDocument.Parse(input.Length == 0 ? "{}" : input); return document.RootElement.Clone(); }
        catch (JsonException) { return null; }
    }

    internal override IReadOnlyList<LlmEvent> Complete()
    {
        if (!_stopped) throw Invalid("Anthropic stream ended without message_stop.", true);
        return [];
    }
}
