namespace OpenCode.Core.Llm;

using System.Text.Json;
using System.Text.Json.Nodes;

internal static class ResponsesRequestLowering
{
    internal static readonly string[] OptionNames = ["store", "metadata", "safetyIdentifier", "streamOptions", "topLogprobs",
        "reasoningEffort", "reasoningSummary", "include", "textVerbosity", "serviceTier", "truncation", "allowedTools", "maxToolCalls", "parallelToolCalls"];

    internal static JsonObject Defaults(string modelId)
    {
        var result = new JsonObject { ["store"] = false, ["include"] = new JsonArray(JsonValue.Create("reasoning.encrypted_content")) };
        // Public model-facade defaults from providers/openai-options.ts; this never changes the model ID or protocol.
        var id = modelId.ToLowerInvariant();
        if (!id.Contains("gpt-5", StringComparison.Ordinal) || id.Contains("gpt-5-chat", StringComparison.Ordinal) || id.Contains("gpt-5-pro", StringComparison.Ordinal)) return result;
        result["reasoningEffort"] = "medium";
        result["reasoningSummary"] = "auto";
        if (id.Contains("gpt-5.", StringComparison.Ordinal) && !id.Contains("codex", StringComparison.Ordinal) && !id.Contains("-chat", StringComparison.Ordinal)) result["textVerbosity"] = "low";
        return result;
    }

    internal static JsonObject Body(LlmRequest request, JsonObject options)
    {
        if (string.IsNullOrEmpty(request.ModelId) || request.Messages.IsDefault || request.System.IsDefault || request.Tools.IsDefault
            || request.Messages.Any(message => message.Content.IsDefault)) throw Invalid("Responses requires an exact model ID and initialized request arrays.");
        if (request.System.Any(part => part.Cache is not null) || request.Tools.Any(tool => tool.Cache is not null)
            || request.Messages.Any(message => message.Content.Any(part => part.Cache is not null)))
            throw Unsupported("Responses explicit cache markers are not implemented; use promptCacheKey.");
        if (request.Generation is { } generation && (generation.TopK is not null || generation.Seed is not null || !generation.Stop.IsDefaultOrEmpty))
            throw Unsupported("Responses does not lower topK, seed, or stop sequences.");
        LlmHttp.RequireFields(options, OptionNames);
        foreach (var field in new[] { "store", "parallelToolCalls" }) LlmHttp.RequireType(options, field, JsonValueKind.True, JsonValueKind.False);
        var body = new JsonObject { ["model"] = request.ModelId, ["stream"] = true, ["input"] = Messages(request) };
        var instructions = string.Join('\n', request.System.Select(part => part.Text));
        if (instructions.Length > 0) body["instructions"] = instructions;
        var names = new HashSet<string>(StringComparer.Ordinal);
        var tools = new JsonArray();
        foreach (var tool in request.Tools)
        {
            if (string.IsNullOrEmpty(tool.Name) || !names.Add(tool.Name) || tool.InputSchema.ValueKind != JsonValueKind.Object)
                throw Invalid("Responses tools require unique names and object input schemas.");
            tools.Add(new JsonObject { ["type"] = "function", ["name"] = tool.Name, ["description"] = tool.Description,
                ["parameters"] = JsonNode.Parse(tool.InputSchema.GetRawText()), ["strict"] = false });
        }
        if (tools.Count > 0) body["tools"] = tools;
        if (request.ToolChoice is { } choice)
            body["tool_choice"] = choice switch
            {
                LlmToolChoice.Auto => JsonValue.Create("auto"), LlmToolChoice.None => JsonValue.Create("none"),
                LlmToolChoice.Required => JsonValue.Create("required"),
                LlmToolChoice.Named named when names.Contains(named.Name) => new JsonObject { ["type"] = "function", ["name"] = named.Name },
                _ => throw Invalid("Named tool choice must refer to a declared function.")
            };
        if (request.Generation is { } settings)
            foreach (var (name, value) in new (string, double?)[]
            {
                ("max_output_tokens", settings.MaxTokens), ("temperature", settings.Temperature), ("top_p", settings.TopP),
                ("presence_penalty", settings.PresencePenalty), ("frequency_penalty", settings.FrequencyPenalty)
            }) if (value is not null) body[name] = value;
        foreach (var (name, wire) in new[] { ("store", "store"), ("parallelToolCalls", "parallel_tool_calls") })
            if (options[name] is { } value) body[wire] = value.GetValue<bool>();
        if (!options.ContainsKey("parallelToolCalls") && request.ToolChoice?.DisableParallelToolUse is { } disabled) body["parallel_tool_calls"] = !disabled;
        foreach (var (name, wire) in new[] { ("safetyIdentifier", "safety_identifier"), ("serviceTier", "service_tier"), ("truncation", "truncation") })
            if (options[name] is { } value && value.GetValue<string>() is { Length: > 0 } text) body[wire] = text;
        if (body["truncation"] is { } truncation && truncation.GetValue<string>() is not ("auto" or "disabled")) throw Invalid("Unknown Responses truncation mode.");
        if (LlmCachePolicy.PromptKey(request.PromptCacheKey) is { Length: > 0 } cacheKey) body["prompt_cache_key"] = cacheKey;
        if (options["metadata"] is { } metadata)
        {
            if (metadata is not JsonObject values || values.Any(pair => pair.Value is not JsonValue value || !value.TryGetValue<string>(out _)))
                throw Invalid("Responses metadata values must be strings.");
            body["metadata"] = metadata.DeepClone();
        }
        if (options["streamOptions"] is { } stream)
        {
            if (stream is not JsonObject fields) throw Invalid("streamOptions must be an object.");
            LlmHttp.RequireFields(fields, "includeObfuscation");
            if (fields["includeObfuscation"] is { } obfuscation) body["stream_options"] = new JsonObject { ["include_obfuscation"] = obfuscation.GetValue<bool>() };
        }
        foreach (var (name, wire) in new[] { ("topLogprobs", "top_logprobs"), ("maxToolCalls", "max_tool_calls") })
            if (options[name] is { } value)
            {
                var number = CatalogNumbers.Integer(value);
                if (name == "topLogprobs" && (number < 0 || number > 20)) throw Invalid("topLogprobs must be between zero and twenty.");
                body[wire] = number;
            }
        if (options["include"] is { } include)
        {
            if (include is not JsonArray values || values.Any(value => value is not JsonValue scalar || !scalar.TryGetValue<string>(out _)))
                throw Invalid("Responses include must be an array of strings.");
            if (values.Count > 0) body["include"] = values.DeepClone();
        }
        var reasoning = new JsonObject();
        if (options["reasoningEffort"] is { } effort && effort.GetValue<string>().Length > 0) reasoning["effort"] = effort.DeepClone();
        if (options["reasoningSummary"] is { } summary)
        {
            if (summary.GetValue<string>() is not ("auto" or "concise" or "detailed")) throw Invalid("Unknown reasoning summary mode.");
            reasoning["summary"] = summary.DeepClone();
        }
        if (reasoning.Count > 0) body["reasoning"] = reasoning;
        if (options["textVerbosity"] is { } verbosity && verbosity.GetValue<string>().Length > 0) body["text"] = new JsonObject { ["verbosity"] = verbosity.DeepClone() };
        if (options["allowedTools"] is { } allowed)
        {
            if (allowed is not JsonObject fields || fields["toolNames"] is not JsonArray toolNames) throw Invalid("allowedTools requires toolNames.");
            LlmHttp.RequireFields(fields, "toolNames", "mode");
            var mode = fields["mode"]?.GetValue<string>() ?? "auto";
            if (mode is not ("auto" or "none" or "required")) throw Invalid("Unknown allowed-tools mode.");
            var selected = new JsonArray();
            foreach (var tool in toolNames)
            {
                var name = tool?.GetValue<string>() ?? throw Invalid("Allowed tool names must be strings.");
                if (!names.Contains(name)) throw Invalid("Allowed tools must refer to declared functions.");
                selected.Add(new JsonObject { ["type"] = "function", ["name"] = name });
            }
            if (selected.Count > 0) body["tool_choice"] = new JsonObject { ["type"] = "allowed_tools", ["mode"] = mode, ["tools"] = selected };
        }
        return body;
    }

    private static JsonArray Messages(LlmRequest request)
    {
        var result = new JsonArray();
        foreach (var message in request.Messages)
        {
            if (message.Role == LlmRole.System)
            {
                if (message.Content.Any(part => part is not LlmContent.Text)) throw Invalid("Chronological system updates require text.");
                result.Add(new JsonObject { ["role"] = "developer", ["content"] = string.Join('\n', message.Content.Cast<LlmContent.Text>().Select(part => part.Value)) });
                continue;
            }
            if (message.Role == LlmRole.User)
            {
                result.Add(new JsonObject { ["role"] = "user", ["content"] = new JsonArray(message.Content.Select(part => InputPart(part, false)).ToArray()) });
                continue;
            }
            if (message.Role == LlmRole.Tool)
            {
                foreach (var part in message.Content)
                {
                    if (part is not LlmContent.ToolResult tool || tool.ProviderExecuted) throw Unsupported("Responses tool messages support local function results only.");
                    result.Add(new JsonObject { ["type"] = "function_call_output", ["call_id"] = tool.Id, ["output"] = ToolOutput(tool.Result) });
                }
                continue;
            }
            if (message.Role != LlmRole.Assistant) throw Invalid("Unknown message role.");
            JsonObject? textGroup = null;
            var reasoningGroups = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
            foreach (var part in message.Content)
            {
                if (part is LlmContent.Text text)
                {
                    var id = ItemId(part);
                    var phase = Field(part, "phase");
                    if (phase is { } value && value.ValueKind != JsonValueKind.Null && value.GetString() is not ("commentary" or "final_answer")) phase = null;
                    if (textGroup is null || textGroup["id"]?.GetValue<string>() != id
                        || !JsonNode.DeepEquals(textGroup["phase"], phase is { } raw ? JsonNode.Parse(raw.GetRawText()) : null)
                        || textGroup.ContainsKey("phase") != phase.HasValue)
                    {
                        textGroup = new JsonObject { ["type"] = "message", ["role"] = "assistant", ["content"] = new JsonArray() };
                        if (id is not null) textGroup["id"] = id;
                        if (phase is { } supplied) textGroup["phase"] = JsonNode.Parse(supplied.GetRawText());
                        result.Add(textGroup);
                    }
                    textGroup["content"]!.AsArray().Add(new JsonObject { ["type"] = "output_text", ["text"] = text.Value });
                    continue;
                }
                textGroup = null;
                if (part is LlmContent.Reasoning reasoning)
                {
                    if (!part.ProviderMetadata.TryGetValue("openai", out var metadata) || metadata.ValueKind != JsonValueKind.Object)
                        throw Unsupported("Responses reasoning replay requires OpenAI provider metadata.");
                    var encrypted = Field(part, "reasoningEncryptedContent");
                    if (reasoning.Encrypted is not null && encrypted is null) throw Unsupported("Use reasoningEncryptedContent metadata for Responses encrypted state.");
                    if (encrypted is { } value && value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)) throw Invalid("Invalid encrypted reasoning state.");
                    var id = ItemId(part);
                    var existing = id is not null ? reasoningGroups.GetValueOrDefault(id) : null;
                    var item = existing ?? new JsonObject { ["type"] = "reasoning", ["summary"] = new JsonArray() };
                    if (existing is null)
                    {
                        if (id is not null) { item["id"] = id; reasoningGroups[id] = item; }
                        if (encrypted is { } initialEncrypted) item["encrypted_content"] = JsonNode.Parse(initialEncrypted.GetRawText());
                        result.Add(item);
                    }
                    else if (encrypted is { ValueKind: JsonValueKind.String } updatedEncrypted) item["encrypted_content"] = updatedEncrypted.GetString();
                    if (reasoning.Value.Length > 0) item["summary"]!.AsArray().Add(new JsonObject { ["type"] = "summary_text", ["text"] = reasoning.Value });
                    continue;
                }
                if (part is LlmContent.ToolCall { ProviderExecuted: false } call)
                {
                    var item = new JsonObject { ["type"] = "function_call", ["call_id"] = call.Id,
                        ["name"] = call.Name, ["arguments"] = call.Input.GetRawText() };
                    if (ItemId(part) is { } id) item["id"] = id;
                    result.Add(item);
                    continue;
                }
                throw Unsupported("Hosted tools and this assistant content type are not implemented for Responses replay.");
            }
        }
        return result;
    }

    private static JsonNode InputPart(LlmContent part, bool toolResult)
    {
        if (part.Cache is not null) throw Unsupported("Responses input does not lower cache markers.");
        if (part is LlmContent.Text text) return new JsonObject { ["type"] = "input_text", ["text"] = text.Value };
        if (part is not LlmContent.Media media) throw Unsupported("Responses input supports text and base64 media only.");
        var url = $"data:{media.MediaType};base64,{media.Base64}";
        if (media.MediaType.StartsWith("image/", StringComparison.Ordinal)) return new JsonObject { ["type"] = "input_image", ["image_url"] = url };
        if (toolResult && media.MediaType.StartsWith("video/", StringComparison.Ordinal)) return new JsonObject { ["type"] = "input_video", ["video_url"] = url };
        return new JsonObject { ["type"] = "input_file", ["filename"] = media.Filename ?? (media.MediaType == "application/pdf" ? "document.pdf" : "file"), ["file_data"] = url };
    }

    private static JsonNode? ToolOutput(LlmToolResult result) => result switch
    {
        LlmToolResult.Text text => JsonValue.Create(text.Value),
        LlmToolResult.Json json => JsonValue.Create(json.Value.ValueKind == JsonValueKind.String ? json.Value.GetString() : json.Value.GetRawText()),
        LlmToolResult.Error error => JsonValue.Create(error.Value.ValueKind == JsonValueKind.String ? error.Value.GetString() : error.Value.GetRawText()),
        LlmToolResult.Content content => new JsonArray(content.Value.Select(part => InputPart(part, true)).ToArray()),
        _ => throw Invalid("Unknown function output type.")
    };

    private static JsonElement? Field(LlmContent part, string name) => part.ProviderMetadata.TryGetValue("openai", out var metadata)
        && metadata.ValueKind == JsonValueKind.Object && metadata.TryGetProperty(name, out var value) ? value : null;
    private static string? ItemId(LlmContent part)
    {
        var value = Field(part, "itemId");
        if (value is not { ValueKind: JsonValueKind.String }) return null;
        var id = value.Value.GetString()!;
        var separator = id.IndexOf('_');
        return separator > 0 && separator < id.Length - 1 ? id : null;
    }

    private static LlmException Invalid(string message) => new(new LlmFailure.InvalidRequest(message));
    private static LlmException Unsupported(string message) => new(new LlmFailure.Unsupported(message));
}
