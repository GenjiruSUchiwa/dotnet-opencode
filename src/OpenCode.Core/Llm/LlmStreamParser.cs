namespace OpenCode.Core.Llm;

using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

internal abstract class LlmStreamParser
{
    private bool _started;
    private bool _text;
    private bool _reasoning;
    private int _reasoningSegment;
    protected LlmUsage? Usage;
    protected LlmFinish? Reason;

    internal abstract IReadOnlyList<LlmEvent> Step(JsonElement root);
    internal abstract IReadOnlyList<LlmEvent> Complete();
    internal virtual bool HandlesProviderErrors => false;

    protected void Start(List<LlmEvent> events)
    {
        if (_started) return;
        _started = true;
        events.Add(new LlmEvent.StepStart());
    }

    protected void Text(List<LlmEvent> events, string text, ImmutableDictionary<string, JsonElement>? metadata = null)
    {
        Start(events);
        if (!_text) events.Add(new LlmEvent.TextStart("text-0"));
        _text = true;
        events.Add(new LlmEvent.TextDelta("text-0", text) { ProviderMetadata = metadata ?? ImmutableDictionary<string, JsonElement>.Empty });
    }

    protected void Reasoning(List<LlmEvent> events, string? text, ImmutableDictionary<string, JsonElement> metadata)
    {
        Start(events);
        var id = $"reasoning-{_reasoningSegment}";
        if (!_reasoning) events.Add(new LlmEvent.ReasoningStart(id) { ProviderMetadata = metadata });
        _reasoning = true;
        if (text is not null) events.Add(new LlmEvent.ReasoningDelta(id, text) { ProviderMetadata = metadata });
    }

    protected void EndReasoning(List<LlmEvent> events, ImmutableDictionary<string, JsonElement> metadata)
    {
        if (!_reasoning) return;
        events.Add(new LlmEvent.ReasoningEnd($"reasoning-{_reasoningSegment}") { ProviderMetadata = metadata });
        _reasoning = false;
        _reasoningSegment++;
    }

    protected void Finish(List<LlmEvent> events, ImmutableDictionary<string, JsonElement> metadata,
        ImmutableDictionary<string, JsonElement>? textMetadata = null)
    {
        Start(events);
        EndReasoning(events, metadata);
        if (_text) events.Add(new LlmEvent.TextEnd("text-0") { ProviderMetadata = textMetadata ?? ImmutableDictionary<string, JsonElement>.Empty });
        _text = false;
        var reason = Reason ?? new LlmFinish(LlmFinishReason.Unknown);
        events.Add(new LlmEvent.StepFinish(reason, Usage) { ProviderMetadata = metadata });
        events.Add(new LlmEvent.Finish(reason, Usage) { ProviderMetadata = metadata });
    }

    internal static JsonElement Get(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var property) ? property : default;
    internal static bool Missing(JsonElement value) => value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined;
    internal static string? String(JsonElement value) => Missing(value) ? null : value.GetString();
    internal static double? Number(JsonElement value) => Missing(value) ? null : value.GetDouble();
    internal static ImmutableDictionary<string, JsonElement> Metadata(string provider, JsonElement value) =>
        ImmutableDictionary<string, JsonElement>.Empty.Add(provider, value.Clone());
    protected static LlmException Invalid(string message, bool incomplete = false) =>
        new(new LlmFailure.InvalidProviderOutput(message, incomplete));
    protected static double? Total(double? input, double? output, double? total) => total
        ?? (input is null && output is null ? null : (input ?? 0) + (output ?? 0));
}

internal sealed class GoogleStreamParser(string providerMetadataKey) : LlmStreamParser
{
    private string? _reasoningSignature;
    private string? _textSignature;
    private JsonElement _feedback;
    private readonly HashSet<string> _callIds = new(StringComparer.Ordinal);
    private bool _tools;

    private ImmutableDictionary<string, JsonElement> Signature(string? signature) => signature is null
        ? ImmutableDictionary<string, JsonElement>.Empty
        : Metadata(providerMetadataKey, JsonSerializer.SerializeToElement(new { thoughtSignature = signature }));

    internal override IReadOnlyList<LlmEvent> Step(JsonElement root)
    {
        var events = new List<LlmEvent>();
        var usage = Get(root, "usageMetadata");
        if (!Missing(usage))
        {
            if (usage.ValueKind != JsonValueKind.Object) throw Invalid("Invalid Google usage.");
            var input = Number(Get(usage, "promptTokenCount"));
            var cached = Number(Get(usage, "cachedContentTokenCount"));
            var thoughts = Number(Get(usage, "thoughtsTokenCount"));
            var visible = Number(Get(usage, "candidatesTokenCount"));
            var output = visible is null ? null : visible + (thoughts ?? 0);
            Usage = new LlmUsage(input, output, input is null ? null : Math.Max(0, input.Value - (cached ?? 0)),
                cached, ReasoningTokens: thoughts, TotalTokens: Total(input, output, Number(Get(usage, "totalTokenCount"))))
            { ProviderMetadata = Metadata(providerMetadataKey, usage) };
        }
        var feedback = Get(root, "promptFeedback");
        if (!Missing(feedback)) _feedback = feedback.Clone();
        var candidates = Get(root, "candidates");
        if (Missing(candidates)) return events;
        if (candidates.ValueKind != JsonValueKind.Array) throw Invalid("Invalid Google candidates.");
        if (candidates.GetArrayLength() == 0) return events;
        var candidate = candidates[0];
        if (candidate.ValueKind != JsonValueKind.Object) throw Invalid("Invalid Google candidate.");
        var content = Get(candidate, "content");
        if (!Missing(content) && content.ValueKind != JsonValueKind.Object) throw Invalid("Invalid Google content.");
        var parts = Get(content, "parts");
        if (!Missing(parts))
        {
            if (parts.ValueKind != JsonValueKind.Array) throw Invalid("Invalid Google content parts.");
            foreach (var part in parts.EnumerateArray())
            {
                if (part.ValueKind != JsonValueKind.Object) throw Invalid("Invalid Google content part.");
                var signature = String(Get(part, "thoughtSignature"));
                var thought = Get(part, "thought");
                var reasoning = !Missing(thought) && thought.GetBoolean();
                var text = String(Get(part, "text"));
                if (signature is not null && reasoning) _reasoningSignature = signature;
                else if (signature is not null && text is not null) _textSignature = signature;
                if (text is { Length: > 0 })
                {
                    if (reasoning) Reasoning(events, text, Signature(signature));
                    else
                    {
                        EndReasoning(events, Signature(_reasoningSignature));
                        _reasoningSignature = null;
                        Text(events, text, Signature(_textSignature));
                    }
                    continue;
                }
                var call = Get(part, "functionCall");
                if (!Missing(call))
                {
                    if (call.ValueKind != JsonValueKind.Object) throw Invalid("Invalid Google function call.");
                    var name = String(Get(call, "name")) ?? throw Invalid("Google function call is missing a name.");
                    var supplied = String(Get(call, "id"));
                    var id = supplied is not null && _callIds.Add(supplied) ? supplied : "tool_" + Guid.NewGuid().ToString("N");
                    var input = Get(call, "args");
                    EndReasoning(events, Signature(_reasoningSignature));
                    _reasoningSignature = null;
                    Start(events);
                    events.Add(new LlmEvent.ToolCall(id, name,
                        input.ValueKind == JsonValueKind.Undefined ? JsonSerializer.SerializeToElement(new JsonObject()) : input.Clone())
                    { ProviderMetadata = Signature(signature) });
                    _tools = true;
                    continue;
                }
                // Preserve media, opaque signatures, and unknown provider parts without
                // treating them as answer text or inventing a local tool result.
                if (text is null || signature is not null)
                {
                    Start(events);
                    events.Add(new LlmEvent.ProviderState(part.Clone()) { ProviderMetadata = Metadata(providerMetadataKey, part) });
                }
            }
        }
        var finish = String(Get(candidate, "finishReason"));
        if (finish is not null) Reason = new LlmFinish(MapReason(finish), finish);
        return events;
    }

    private LlmFinishReason MapReason(string? reason) => reason switch
    {
        null or "STOP" => _tools ? LlmFinishReason.ToolCalls : reason is null ? LlmFinishReason.Unknown : LlmFinishReason.Stop,
        "MAX_TOKENS" => LlmFinishReason.Length,
        "IMAGE_SAFETY" or "RECITATION" or "SAFETY" or "BLOCKLIST" or "PROHIBITED_CONTENT" or "SPII"
            or "MODEL_ARMOR" or "IMAGE_PROHIBITED_CONTENT" or "IMAGE_RECITATION" or "LANGUAGE" => LlmFinishReason.ContentFilter,
        "MALFORMED_FUNCTION_CALL" or "UNEXPECTED_TOOL_CALL" or "NO_IMAGE" or "TOO_MANY_TOOL_CALLS"
            or "MISSING_THOUGHT_SIGNATURE" or "MALFORMED_RESPONSE" => LlmFinishReason.Error,
        _ => LlmFinishReason.Unknown
    };

    internal override IReadOnlyList<LlmEvent> Complete()
    {
        var block = String(Get(_feedback, "blockReason"));
        if (Reason is null && block is not null) Reason = new LlmFinish(LlmFinishReason.ContentFilter, block);
        if (Reason is null && Usage is null) throw Invalid("Google stream ended without terminal metadata.", true);
        Reason ??= new LlmFinish(MapReason(null));
        var events = new List<LlmEvent>();
        EndReasoning(events, Signature(_reasoningSignature));
        Finish(events, Missing(_feedback) ? ImmutableDictionary<string, JsonElement>.Empty
            : Metadata(providerMetadataKey, JsonSerializer.SerializeToElement(new { promptFeedback = _feedback })), Signature(_textSignature));
        return events;
    }
}

internal sealed class ChatStreamParser(LlmCompatibility compatibility, string providerMetadataKey) : LlmStreamParser
{
    private sealed class Tool
    {
        internal string? Id;
        internal string? Name;
        internal readonly StringBuilder Input = new();
        internal bool Started;
    }
    private readonly SortedDictionary<int, Tool> _tools = new();
    private readonly JsonArray _details = new();
    private bool _detailsObserved;
    private string? _reasoningField = compatibility.ReasoningField;
    private int? _latestIndex;
    private int _nextIndex;

    private ImmutableDictionary<string, JsonElement> ReasoningMetadata(bool complete = false)
    {
        var data = new JsonObject();
        if (_reasoningField is not null) data["reasoningField"] = _reasoningField;
        if (complete && _detailsObserved) data["reasoningDetails"] = _details.DeepClone();
        return Metadata(providerMetadataKey, JsonSerializer.SerializeToElement(data));
    }

    internal override IReadOnlyList<LlmEvent> Step(JsonElement root)
    {
        var events = new List<LlmEvent>();
        var choices = Get(root, "choices");
        if (!Missing(choices) && choices.ValueKind != JsonValueKind.Array) throw Invalid("Invalid chat choices.");
        var choice = Missing(choices) || choices.GetArrayLength() == 0 ? default : choices[0];
        if (!Missing(choice) && choice.ValueKind != JsonValueKind.Object) throw Invalid("Invalid chat choice.");
        var usage = Get(root, "usage");
        if (Missing(usage)) usage = Get(choice, "usage");
        if (!Missing(usage))
        {
            if (usage.ValueKind != JsonValueKind.Object) throw Invalid("Invalid chat usage.");
            var input = Number(Get(usage, "prompt_tokens"));
            var output = Number(Get(usage, "completion_tokens"));
            var cached = Number(Get(Get(usage, "prompt_tokens_details"), "cached_tokens"))
                ?? Number(Get(usage, "prompt_cache_hit_tokens")) ?? Number(Get(usage, "cached_tokens"));
            var write = Number(Get(Get(usage, "prompt_tokens_details"), "cache_write_tokens"));
            Usage = new LlmUsage(input, output, input is null ? null : Math.Max(0, input.Value - (cached ?? 0) - (write ?? 0)),
                cached, write, Number(Get(Get(usage, "completion_tokens_details"), "reasoning_tokens")),
                Total(input, output, Number(Get(usage, "total_tokens")))) { ProviderMetadata = Metadata(providerMetadataKey, usage) };
        }
        var delta = Get(choice, "delta");
        if (!Missing(delta) && delta.ValueKind != JsonValueKind.Object) throw Invalid("Invalid chat delta.");
        var text = String(Get(delta, "content"));
        var refusal = String(Get(delta, "refusal"));
        var details = Get(delta, "reasoning_details");
        var tools = Get(delta, "tool_calls");
        if (!Missing(tools) && tools.ValueKind != JsonValueKind.Array) throw Invalid("Invalid chat tool deltas.");
        string? reasoning = null;
        foreach (var field in new[] { _reasoningField, "reasoning_content", "reasoning", "reasoning_text" }.OfType<string>().Distinct())
        {
            var value = String(Get(delta, field));
            if (string.IsNullOrEmpty(value)) continue;
            _reasoningField ??= field;
            reasoning = value;
            break;
        }
        if (!Missing(Get(delta, "function_call"))) throw Invalid("Legacy function_call streaming is not supported; use tool_calls.");
        if (Reason is not null)
        {
            if (!string.IsNullOrEmpty(text) || !string.IsNullOrEmpty(refusal) || reasoning is not null
                || (!Missing(details) && details.ValueKind == JsonValueKind.Array && details.GetArrayLength() > 0)
                || (!Missing(tools) && tools.GetArrayLength() > 0)) throw Invalid("Chat received content after finish_reason.");
            return events;
        }
        if (!Missing(details))
        {
            if (details.ValueKind != JsonValueKind.Array) throw Invalid("Invalid reasoning details.");
            _detailsObserved = true;
            var visible = new StringBuilder();
            foreach (var detail in details.EnumerateArray())
            {
                if (String(Get(detail, "type")) == "reasoning.text") visible.Append(String(Get(detail, "text")));
                if (String(Get(detail, "type")) == "reasoning.summary") visible.Append(String(Get(detail, "summary")));
                AppendDetail(detail);
            }
            if (visible.Length > 0) reasoning = visible.ToString();
        }
        if (reasoning is not null) Reasoning(events, reasoning, ReasoningMetadata());
        else if (_detailsObserved) Reasoning(events, null, ReasoningMetadata());
        if (!string.IsNullOrEmpty(text)) Text(events, text);
        if (!string.IsNullOrEmpty(refusal)) Text(events, refusal);
        if (!Missing(tools))
        {
            var position = 0;
            foreach (var item in tools.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) throw Invalid("Invalid chat tool delta.");
                var id = String(Get(item, "id"));
                var name = String(Get(Get(item, "function"), "name"));
                var argument = String(Get(Get(item, "function"), "arguments")) ?? "";
                var matched = string.IsNullOrEmpty(id) ? null : _tools.Where(pair => pair.Value.Id == id).Select(pair => (int?)pair.Key).FirstOrDefault();
                var fallback = tools.GetArrayLength() > 1 ? position : _latestIndex ?? position;
                var explicitIndex = Get(item, "index");
                var index = !Missing(explicitIndex) ? explicitIndex.GetInt32() : matched
                    ?? (!string.IsNullOrEmpty(id) && _tools.TryGetValue(fallback, out var prior) && prior.Id is not null && prior.Id != id ? _nextIndex : fallback);
                if (index < 0) throw Invalid("Invalid chat tool index.");
                if (matched is { } owner && owner != index)
                    throw Invalid("Chat tool ID is already assigned to a different index.");
                _tools.TryGetValue(index, out var tool);
                if (tool is not null)
                {
                    if (!string.IsNullOrEmpty(id) && tool.Id is not null && tool.Id != id)
                        throw Invalid("Chat tool ID changed at an existing index.");
                    if (!string.IsNullOrEmpty(name) && tool.Name is not null && tool.Name != name)
                        throw Invalid("Chat tool name changed at an existing index.");
                }
                _latestIndex = index;
                _nextIndex = Math.Max(_nextIndex, index + 1);
                if (tool is null) _tools[index] = tool = new Tool();
                tool.Id ??= string.IsNullOrEmpty(id) ? null : id;
                tool.Name ??= string.IsNullOrEmpty(name) ? null : name;
                tool.Input.Append(argument);
                if (tool.Id is not null && tool.Name is not null)
                {
                    Start(events);
                    var fragment = tool.Started ? argument : tool.Input.ToString();
                    if (!tool.Started) events.Add(new LlmEvent.ToolInputStart(tool.Id, tool.Name));
                    tool.Started = true;
                    if (fragment.Length > 0) events.Add(new LlmEvent.ToolInputDelta(tool.Id, tool.Name, fragment, ParseInput(tool.Input.ToString())));
                }
                position++;
            }
        }
        var finish = String(Get(choice, "finish_reason"));
        if (finish is not null)
        {
            var normalized = finish switch
            {
                "stop" or "end" => LlmFinishReason.Stop,
                "length" => LlmFinishReason.Length,
                "content_filter" => LlmFinishReason.ContentFilter,
                "function_call" or "tool_calls" => LlmFinishReason.ToolCalls,
                "network_error" => throw new LlmException(new LlmFailure.ProviderInternal("Provider reported a network error.")),
                _ => throw new LlmException(new LlmFailure.Provider("Provider reported an error or unknown finish reason."))
            };
            Reason = new LlmFinish(normalized, String(Get(choice, "native_finish_reason")) ?? finish);
            if (normalized is not (LlmFinishReason.Length or LlmFinishReason.ContentFilter)
                && _tools.Values.Any(tool => !tool.Started)) throw Invalid("Tool call is missing its ID or name.");
        }
        return events;
    }

    private void AppendDetail(JsonElement detail)
    {
        var incoming = JsonNode.Parse(detail.GetRawText());
        if (_details.LastOrDefault() is not JsonObject previous || incoming is not JsonObject current
            || previous["type"]?.GetValue<string>() != "reasoning.text" || current["type"]?.GetValue<string>() != "reasoning.text"
            || new[] { "id", "index", "format" }.Any(key => previous[key] is not null && current[key] is not null && !JsonNode.DeepEquals(previous[key], current[key]))
            || (previous["signature"] is JsonValue oldSignature && oldSignature.TryGetValue<string>(out var oldText) && oldText.Length > 0
                && current["signature"] is JsonValue newSignature && newSignature.TryGetValue<string>(out var newText) && newText.Length > 0 && oldText != newText))
        {
            _details.Add(incoming);
            return;
        }
        var joined = (previous["text"]?.GetValue<string>() ?? "") + (current["text"]?.GetValue<string>() ?? "");
        foreach (var (key, value) in current)
        {
            if (key is "signature" or "format" && previous[key] is JsonValue existing
                && existing.TryGetValue<string>(out var text) && text.Length > 0) continue;
            previous[key] = value?.DeepClone();
        }
        previous["text"] = joined;
    }

    private static JsonElement? ParseInput(string input)
    {
        try { using var document = JsonDocument.Parse(input.Length == 0 ? "{}" : input); return document.RootElement.Clone(); }
        catch (JsonException) { return null; }
    }

    internal override IReadOnlyList<LlmEvent> Complete()
    {
        if (Reason is null && compatibility.RequireFinishReason) throw Invalid("Chat stream ended without finish_reason.", true);
        var events = new List<LlmEvent>();
        if (_detailsObserved) Reasoning(events, null, ReasoningMetadata());
        EndReasoning(events, ReasoningMetadata(complete: true));
        var calls = false;
        foreach (var tool in _tools.Values)
        {
            if (!tool.Started)
            {
                if (Reason?.Normalized is not (LlmFinishReason.Length or LlmFinishReason.ContentFilter)) throw Invalid("Tool call is missing its ID or name.");
                events.Add(new LlmEvent.ProviderState(JsonSerializer.SerializeToElement(new { id = tool.Id, name = tool.Name, arguments = tool.Input.ToString() })));
                continue;
            }
            var raw = tool.Input.ToString();
            events.Add(new LlmEvent.ToolInputEnd(tool.Id!, tool.Name!));
            // Complete JSON alone does not authorize execution after truncation/filtering.
            if (Reason?.Normalized is LlmFinishReason.Length or LlmFinishReason.ContentFilter)
            {
                events.Add(new LlmEvent.ToolInputError(tool.Id!, tool.Name!, raw));
                continue;
            }
            if (ParseInput(raw) is not { } input)
            {
                events.Add(new LlmEvent.ToolInputError(tool.Id!, tool.Name!, raw));
                continue;
            }
            events.Add(new LlmEvent.ToolCall(tool.Id!, tool.Name!, input));
            calls = true;
        }
        Reason ??= new LlmFinish(calls ? LlmFinishReason.ToolCalls : LlmFinishReason.Stop);
        if (calls && Reason.Normalized == LlmFinishReason.Stop) Reason = Reason with { Normalized = LlmFinishReason.ToolCalls };
        Finish(events, ReasoningMetadata(complete: true));
        return events;
    }
}
