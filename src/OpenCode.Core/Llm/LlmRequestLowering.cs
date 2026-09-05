namespace OpenCode.Core.Llm;

using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class LlmRequestLowering
{
    internal static JsonObject Body(LlmRequest request, bool google, JsonObject providerOptions, string providerMetadataKey)
    {
        if (request.Messages.IsDefault || request.System.IsDefault || request.Tools.IsDefault
            || request.Messages.Any(message => message.Content.IsDefault)) throw Invalid("Request arrays must be initialized.");
        if (request.System.Any(part => part.Cache is not null) || request.Tools.Any(tool => tool.Cache is not null)
            || request.Messages.Any(message => message.Content.Any(part => part.Cache is not null)))
            throw Unsupported("Explicit cache hints are currently implemented only for Anthropic Messages.");
        if (request.ToolChoice?.DisableParallelToolUse is not null)
            throw Unsupported("Parallel-tool control is not implemented by this protocol lowering.");
        if (string.IsNullOrEmpty(request.ModelId)) throw Invalid("An exact API model ID is required.");
        if (request.Compatibility.ReasoningField is "role" or "content" or "refusal" or "tool_calls")
            throw Invalid("The reasoning field conflicts with a reserved message field.");
        if (request.Compatibility.MaxTokensField is not ("max_tokens" or "max_completion_tokens"))
            throw Invalid("Unsupported maximum-token field.");
        var messages = Messages(request, google, providerMetadataKey);
        var body = google ? new JsonObject { ["contents"] = messages }
            : new JsonObject { ["model"] = request.ModelId, ["messages"] = messages, ["stream"] = true };
        if (google && request.System.Length > 0)
            body["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = string.Join('\n', request.System.Select(part => part.Text)) }) };
        if (!google && request.Compatibility.SupportsUsageInStreaming)
            body["stream_options"] = new JsonObject { ["include_usage"] = true };
        if (request.Tools.Length > 0)
        {
            if (request.Tools.Select(tool => tool.Name).Distinct(StringComparer.Ordinal).Count() != request.Tools.Length)
                throw Invalid("Tool names must be unique.");
            var tools = new JsonArray();
            foreach (var tool in request.Tools)
            {
                if (string.IsNullOrEmpty(tool.Name) || tool.InputSchema.ValueKind != JsonValueKind.Object)
                    throw Invalid("Tool definitions require a name and object input schema.");
                var function = new JsonObject { ["name"] = tool.Name, ["description"] = tool.Description };
                var schema = JsonNode.Parse(tool.InputSchema.GetRawText())!.AsObject();
                var parameters = google ? GoogleSchema(schema) : schema;
                if (parameters is not null) function["parameters"] = parameters;
                if (google) tools.Add(function);
                else
                {
                    if (request.Compatibility.SupportsStrictMode) function["strict"] = false;
                    tools.Add(new JsonObject { ["type"] = "function", ["function"] = function });
                }
            }
            body["tools"] = google ? new JsonArray(new JsonObject { ["functionDeclarations"] = tools }) : tools;
        }
        else if (!google && request.Messages.Any(message => message.Role == LlmRole.Tool || message.Content.Any(part => part is LlmContent.ToolCall)))
            body["tools"] = new JsonArray();
        if (request.ToolChoice is { } choice && (!google || request.Tools.Length > 0))
        {
            var mode = choice switch
            {
                LlmToolChoice.Auto => "auto", LlmToolChoice.None => "none", LlmToolChoice.Required => "required",
                LlmToolChoice.Named named when request.Tools.Any(tool => tool.Name == named.Name) => "tool",
                _ => throw Invalid("Named tool choice must refer to a declared tool.")
            };
            if (google)
            {
                var config = new JsonObject { ["mode"] = mode == "auto" ? "AUTO" : mode == "none" ? "NONE" : "ANY" };
                if (choice is LlmToolChoice.Named named) config["allowedFunctionNames"] = new JsonArray(JsonValue.Create(named.Name));
                body["toolConfig"] = new JsonObject { ["functionCallingConfig"] = config };
            }
            else body["tool_choice"] = choice is LlmToolChoice.Named named
                ? new JsonObject { ["type"] = "function", ["function"] = new JsonObject { ["name"] = named.Name } }
                : JsonValue.Create(mode);
        }
        var generation = google ? new JsonObject() : body;
        if (request.Generation is { } options)
        {
            if (!google && options.TopK is not null) throw Unsupported("Chat does not lower topK; use an explicit provider body override if supported.");
            foreach (var (name, value) in new (string, double?)[]
            {
                (google ? "maxOutputTokens" : request.Compatibility.MaxTokensField, options.MaxTokens),
                ("temperature", options.Temperature), (google ? "topP" : "top_p", options.TopP), ("topK", options.TopK),
                (google ? "frequencyPenalty" : "frequency_penalty", options.FrequencyPenalty),
                (google ? "presencePenalty" : "presence_penalty", options.PresencePenalty), ("seed", options.Seed)
            })
                if (value is not null) generation[name] = value;
            if (!options.Stop.IsDefaultOrEmpty)
                generation[google ? "stopSequences" : "stop"] = new JsonArray(options.Stop.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
        }
        if (google)
        {
            LlmHttp.RequireFields(providerOptions, "thinkingConfig", "cachedContent", "safetySettings", "serviceTier");
            if (providerOptions["thinkingConfig"] is { } thinkingOption)
            {
                var thinking = thinkingOption.DeepClone() as JsonObject ?? throw Invalid("thinkingConfig must be an object.");
                LlmHttp.RequireFields(thinking, "thinkingBudget", "includeThoughts", "thinkingLevel");
                LlmHttp.RequireType(thinking, "thinkingBudget", JsonValueKind.Number);
                LlmHttp.RequireType(thinking, "includeThoughts", JsonValueKind.True, JsonValueKind.False);
                LlmHttp.RequireType(thinking, "thinkingLevel", JsonValueKind.String);
                thinking["includeThoughts"] ??= true;
                generation["thinkingConfig"] = thinking;
            }
            foreach (var name in new[] { "cachedContent", "serviceTier" })
            {
                LlmHttp.RequireType(providerOptions, name, JsonValueKind.String);
                if (providerOptions[name] is { } value) body[name] = value.DeepClone();
            }
            if (providerOptions["safetySettings"] is { } safety)
            {
                if (safety is not JsonArray settings) throw Invalid("safetySettings must be an array.");
                foreach (var setting in settings)
                {
                    if (setting is not JsonObject entry || entry["category"] is null || entry["threshold"] is null)
                        throw Invalid("Safety settings require category and threshold.");
                    LlmHttp.RequireFields(entry, "category", "threshold");
                    LlmHttp.RequireType(entry, "category", JsonValueKind.String);
                    LlmHttp.RequireType(entry, "threshold", JsonValueKind.String);
                }
                body["safetySettings"] = safety.DeepClone();
            }
            if (generation.Count > 0) body["generationConfig"] = generation;
            if (request.PromptCacheKey is not null) throw Unsupported("Google promptCacheKey lowering is not implemented; use cachedContent.");
        }
        else
        {
            LlmHttp.RequireFields(providerOptions, "reasoningEffort", "store");
            LlmHttp.RequireType(providerOptions, "reasoningEffort", JsonValueKind.String);
            LlmHttp.RequireType(providerOptions, "store", JsonValueKind.True, JsonValueKind.False);
            if (providerOptions["reasoningEffort"] is { } effort && effort.GetValue<string>().Length > 0) body["reasoning_effort"] = effort.DeepClone();
            if (providerOptions["store"] is { } store)
            {
                if (request.Compatibility.SupportsStore == false) throw Unsupported("Store is disabled by model compatibility.");
                body["store"] = store.DeepClone();
            }
            else if (request.Compatibility.SupportsStore == true) body["store"] = false;
            if (!string.IsNullOrEmpty(request.PromptCacheKey)) body["prompt_cache_key"] = request.PromptCacheKey;
        }
        return body;
    }

    private static JsonArray Messages(LlmRequest request, bool google, string providerMetadataKey)
    {
        var result = new JsonArray();
        if (!google && request.System.Length > 0)
            result.Add(new JsonObject { ["role"] = "system", ["content"] = string.Join('\n', request.System.Select(part => part.Text)) });
        foreach (var message in request.Messages)
        {
            if (message.Content.IsDefault) throw Invalid("Message content must be initialized.");
            if (message.Role == LlmRole.System)
            {
                if (message.Content.Any(part => part is not LlmContent.Text)) throw Invalid("Chronological system updates accept text only.");
                var text = "<system-update>\n" + string.Join('\n', message.Content.Cast<LlmContent.Text>().Select(part => part.Value))
                    .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;") + "\n</system-update>";
                if (result.LastOrDefault() is JsonObject previous && previous["role"]?.GetValue<string>() == "user"
                    && (!google || !previous["parts"]!.AsArray().Any(part => part is JsonObject obj && obj.ContainsKey("functionResponse"))))
                {
                    if (google) previous["parts"]!.AsArray().Add(new JsonObject { ["text"] = text });
                    else if (previous["content"] is JsonArray array) array.Add(new JsonObject { ["type"] = "text", ["text"] = text });
                    else previous["content"] = previous["content"]!.GetValue<string>() + "\n" + text;
                }
                else result.Add(google ? new JsonObject { ["role"] = "user", ["parts"] = new JsonArray(new JsonObject { ["text"] = text }) }
                    : new JsonObject { ["role"] = "user", ["content"] = text });
                continue;
            }
            if (google)
            {
                var parts = new JsonArray();
                foreach (var part in message.Content)
                {
                    JsonObject lowered = (message.Role, part) switch
                    {
                        (LlmRole.User or LlmRole.Assistant, LlmContent.Text text) => new() { ["text"] = text.Value },
                        (LlmRole.Assistant, LlmContent.Reasoning reasoning) when reasoning.Encrypted is null => new() { ["text"] = reasoning.Value, ["thought"] = true },
                        (LlmRole.User, LlmContent.Media media) => GoogleMedia(media),
                        (LlmRole.Assistant, LlmContent.ToolCall call) when !call.ProviderExecuted => new()
                        { ["functionCall"] = new JsonObject { ["id"] = call.Id, ["name"] = call.Name, ["args"] = JsonNode.Parse(call.Input.GetRawText()) } },
                        (LlmRole.Tool, LlmContent.ToolResult tool) when !tool.ProviderExecuted => new()
                        { ["functionResponse"] = new JsonObject { ["id"] = tool.Id, ["name"] = tool.Name,
                            ["response"] = new JsonObject { ["name"] = tool.Name, ["content"] = ToolResultText(tool.Result) } } },
                        _ => throw Unsupported("Unsupported Google message content or provider-executed tool history.")
                    };
                    if (message.Role == LlmRole.Assistant && MetadataValue(part, providerMetadataKey, "thoughtSignature") is { } signature)
                        lowered["thoughtSignature"] = signature.GetString();
                    parts.Add(lowered);
                }
                if (message.Role == LlmRole.Tool && result.LastOrDefault() is JsonObject previous
                    && previous["role"]?.GetValue<string>() == "user"
                    && previous["parts"]!.AsArray().Any(part => part is JsonObject obj && obj.ContainsKey("functionResponse")))
                {
                    foreach (var part in parts) previous["parts"]!.AsArray().Add(part?.DeepClone());
                }
                else result.Add(new JsonObject { ["role"] = message.Role == LlmRole.Assistant ? "model" : "user", ["parts"] = parts });
                continue;
            }
            if (message.Role != LlmRole.Tool && request.Compatibility.RequireAssistantAfterTool
                && result.LastOrDefault()?["role"]?.GetValue<string>() == "tool")
                result.Add(new JsonObject { ["role"] = "assistant", ["content"] = "Done." });
            if (message.Role == LlmRole.Tool)
            {
                foreach (var part in message.Content)
                {
                    if (part is not LlmContent.ToolResult tool || tool.ProviderExecuted) throw Unsupported("Unsupported chat tool result.");
                    result.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = tool.Id, ["content"] = ToolResultText(tool.Result) });
                }
                continue;
            }
            if (message.Role == LlmRole.User)
            {
                if (message.Content.All(part => part is LlmContent.Text))
                    result.Add(new JsonObject { ["role"] = "user", ["content"] = string.Concat(message.Content.Cast<LlmContent.Text>().Select(part => part.Value)) });
                else
                {
                    var content = new JsonArray();
                    foreach (var part in message.Content)
                        content.Add(part switch
                        {
                            LlmContent.Text text => new JsonObject { ["type"] = "text", ["text"] = text.Value },
                            LlmContent.Media media when media.MediaType.StartsWith("image/", StringComparison.Ordinal) =>
                                new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = $"data:{media.MediaType};base64,{media.Base64}" } },
                            _ => throw Unsupported("Chat user content supports text and base64 images only.")
                        });
                    result.Add(new JsonObject { ["role"] = "user", ["content"] = content });
                }
                continue;
            }
            if (message.Role != LlmRole.Assistant) throw Invalid("Invalid message role.");
            if (message.Content.All(part => part is LlmContent.Text text && string.IsNullOrWhiteSpace(text.Value))) continue;
            if (message.Content.Any(part => part is not (LlmContent.Text or LlmContent.Reasoning or LlmContent.ToolCall)))
                throw Unsupported("Unsupported chat assistant content.");
            var calls = new JsonArray();
            foreach (var call in message.Content.OfType<LlmContent.ToolCall>())
            {
                if (call.ProviderExecuted) throw Unsupported("Provider-executed tool replay is not supported by chat.");
                calls.Add(new JsonObject { ["id"] = call.Id, ["type"] = "function", ["function"] = new JsonObject
                { ["name"] = call.Name, ["arguments"] = call.Input.GetRawText() } });
            }
            var texts = message.Content.OfType<LlmContent.Text>().ToArray();
            var assistant = new JsonObject { ["role"] = "assistant", ["content"] = texts.Length > 0
                ? string.Concat(texts.Select(part => part.Value)) : calls.Count > 0 ? null : "" };
            if (calls.Count > 0) assistant["tool_calls"] = calls;
            var reasoningParts = message.Content.OfType<LlmContent.Reasoning>().ToArray();
            if (reasoningParts.Any(part => part.Encrypted is not null)) throw Unsupported("Encrypted reasoning must be replayed through provider metadata, not a generic encrypted field.");
            var details = new JsonArray();
            var detailsObserved = false;
            foreach (var part in reasoningParts)
                if (MetadataValue(part, providerMetadataKey, "reasoningDetails") is { } value)
                {
                    if (value.ValueKind != JsonValueKind.Array) throw Invalid("Reasoning details must be an array.");
                    detailsObserved = true;
                    foreach (var detail in value.EnumerateArray()) details.Add(JsonNode.Parse(detail.GetRawText()));
                }
            if (detailsObserved) assistant["reasoning_details"] = details;
            var field = request.Compatibility.ReasoningField
                ?? reasoningParts.Select(part => MetadataValue(part, providerMetadataKey, "reasoningField")?.GetString()).FirstOrDefault(value => value is not null)
                ?? (request.Compatibility.RequireReasoning || reasoningParts.Any(part => MetadataValue(part, providerMetadataKey, "reasoningDetails") is null) ? "reasoning_content" : null);
            if (field is "role" or "content" or "refusal" or "tool_calls") throw Invalid("Reasoning metadata names a reserved field.");
            if (field is not null && (reasoningParts.Length > 0 || request.Compatibility.RequireReasoning))
                assistant[field] = string.Concat(reasoningParts.Select(part => part.Value));
            result.Add(assistant);
        }
        return result;
    }

    private static JsonObject GoogleMedia(LlmContent.Media media) => new()
    { ["inlineData"] = new JsonObject { ["mimeType"] = media.MediaType, ["data"] = media.Base64 } };

    private static JsonElement? MetadataValue(LlmContent part, string provider, string key) =>
        part.ProviderMetadata.TryGetValue(provider, out var metadata) && metadata.ValueKind == JsonValueKind.Object
            && metadata.TryGetProperty(key, out var value) && value.ValueKind != JsonValueKind.Null ? value : null;

    private static string ToolResultText(LlmToolResult result) => result switch
    {
        LlmToolResult.Text text => text.Value,
        LlmToolResult.Json json => json.Value.ValueKind == JsonValueKind.String ? json.Value.GetString()! : json.Value.GetRawText(),
        LlmToolResult.Error error => error.Value.ValueKind == JsonValueKind.String ? error.Value.GetString()! : error.Value.GetRawText(),
        LlmToolResult.Content content when content.Value.All(part => part is LlmContent.Text) => string.Join('\n', content.Value.Cast<LlmContent.Text>().Select(part => part.Value)),
        _ => throw Unsupported("Media-bearing tool results are not implemented by this lowering boundary.")
    };

    private static JsonObject? GoogleSchema(JsonObject schema, bool nested = false)
    {
        // Protocol projection follows gemini-tool-schema.ts, not provider/model heuristics.
        var source = schema.DeepClone().AsObject();
        var combiner = new[] { "anyOf", "oneOf", "allOf" }.Any(key => source[key] is JsonArray);
        if (source["enum"] is JsonArray values)
        {
            source["enum"] = new JsonArray(values.Select(value => (JsonNode?)JsonValue.Create(value is JsonValue scalar
                && scalar.TryGetValue<string>(out var text) ? text : value?.ToJsonString() ?? "null")).ToArray());
            if (source["type"] is JsonValue type && type.TryGetValue<string>(out var name) && name is "integer" or "number") source["type"] = "string";
        }
        var kind = source["type"] is JsonValue typeValue && typeValue.TryGetValue<string>(out var typeName) ? typeName : null;
        if (kind == "object" && source["properties"] is JsonObject properties && source["required"] is JsonArray required)
            source["required"] = new JsonArray(required.Where(item => item is JsonValue value && value.TryGetValue<string>(out var key) && properties.ContainsKey(key)).Select(item => item!.DeepClone()).ToArray());
        if (kind == "array" && !combiner)
        {
            source["items"] ??= new JsonObject();
            if (source["items"] is JsonObject items && !items.Any(pair => new[] { "type", "properties", "items", "prefixItems", "enum", "const", "$ref", "additionalProperties", "patternProperties", "required", "not", "if", "then", "else", "anyOf", "oneOf", "allOf" }.Contains(pair.Key)))
                items["type"] = "string";
        }
        if (kind is not null && kind != "object" && !combiner) { source.Remove("properties"); source.Remove("required"); }
        if (!nested && kind == "object" && (source["properties"] is not JsonObject props || props.Count == 0)
            && (source["additionalProperties"] is null || source["additionalProperties"] is JsonValue flag && flag.TryGetValue<bool>(out var enabled) && !enabled)) return null;
        var result = new JsonObject();
        foreach (var key in new[] { "description", "required", "format", "minLength" })
            if (source[key] is { } value) result[key] = value.DeepClone();
        if (source["type"] is JsonArray types)
        {
            var nonNull = types.Where(value => value?.GetValue<string>() != "null").ToArray();
            if (nonNull.Length == 0) result["type"] = "null";
            else
            {
                result["anyOf"] = new JsonArray(nonNull.Select(value => (JsonNode)new JsonObject { ["type"] = value?.DeepClone() }).ToArray());
                if (nonNull.Length != types.Count) result["nullable"] = true;
            }
        }
        else if (source["type"] is { } value) result["type"] = value.DeepClone();
        if (source.ContainsKey("const")) result["enum"] = new JsonArray(source["const"]?.DeepClone());
        else if (source["enum"] is { } enumeration) result["enum"] = enumeration.DeepClone();
        if (source["properties"] is JsonObject children)
        {
            var projected = new JsonObject();
            foreach (var (key, value) in children) projected[key] = value is JsonObject child ? GoogleSchema(child, true) : null;
            result["properties"] = projected;
        }
        if (source["items"] is JsonObject item) result["items"] = GoogleSchema(item, true);
        else if (source["items"] is JsonArray tuple) result["items"] = new JsonArray(tuple.Select(value => value is JsonObject itemSchema ? GoogleSchema(itemSchema, true) : null).ToArray());
        foreach (var key in new[] { "allOf", "anyOf", "oneOf" })
        {
            if (source[key] is not JsonArray array) continue;
            var nullable = key == "anyOf" && array.Any(value => value is JsonObject obj && obj["type"] is JsonValue scalar && scalar.TryGetValue<string>(out var type) && type == "null");
            var filtered = array.Where(value => !nullable || value is not JsonObject obj || obj["type"] is not JsonValue scalar || !scalar.TryGetValue<string>(out var type) || type != "null").ToArray();
            if (nullable) result["nullable"] = true;
            if (nullable && filtered.Length == 1 && filtered[0] is JsonObject single)
            {
                foreach (var (name, value) in GoogleSchema(single, true)!) result[name] = value?.DeepClone();
            }
            else result[key] = new JsonArray(filtered.Select(value => value is JsonObject child ? GoogleSchema(child, true) : null).ToArray());
        }
        return result;
    }

    private static LlmException Invalid(string message) => new(new LlmFailure.InvalidRequest(message));
    private static LlmException Unsupported(string message) => new(new LlmFailure.Unsupported(message));
}
