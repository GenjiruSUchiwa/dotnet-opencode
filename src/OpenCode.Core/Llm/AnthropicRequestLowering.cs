namespace OpenCode.Core.Llm;

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

internal static class AnthropicRequestLowering
{
    internal static readonly string[] OptionNames = ["thinking", "effort", "service_tier", "serviceTier", "metadata",
        "container", "inference_geo", "inferenceGeo", "cache_control", "cacheControl", "output_config", "outputConfig"];

    internal static JsonObject Body(LlmRequest request, JsonObject options)
    {
        if (string.IsNullOrEmpty(request.ModelId) || request.Messages.IsDefault || request.System.IsDefault || request.Tools.IsDefault)
            throw Invalid("Anthropic requires an exact API model ID and initialized request arrays.");
        if (request.PromptCacheKey is not null) throw Unsupported("Anthropic promptCacheKey placement is not implemented; use explicit cache hints.");
        if (request.Generation is { } generation && (generation.FrequencyPenalty is not null || generation.PresencePenalty is not null || generation.Seed is not null))
            throw Unsupported("Anthropic does not lower frequencyPenalty, presencePenalty, or seed.");
        LlmHttp.RequireFields(options, OptionNames);
        var remaining = 4;
        var dropped = 0;
        JsonObject Mark(JsonObject block, LlmCacheHint? hint)
        {
            if (hint is null) return block;
            if (hint.Kind is not (LlmCacheKind.Ephemeral or LlmCacheKind.Persistent)
                || hint.TtlSeconds is { } seconds && !double.IsFinite(seconds)) throw Invalid("Invalid cache hint.");
            if (remaining == 0) { dropped++; return block; }
            remaining--;
            var control = new JsonObject { ["type"] = "ephemeral" };
            if (hint.TtlSeconds >= 3600) control["ttl"] = "1h";
            block["cache_control"] = control;
            return block;
        }

        var body = new JsonObject { ["model"] = request.ModelId, ["stream"] = true,
            ["max_tokens"] = request.Generation?.MaxTokens ?? 32_000 };
        var tools = new JsonArray();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tool in request.Tools)
        {
            if (string.IsNullOrEmpty(tool.Name) || !names.Add(tool.Name) || tool.InputSchema.ValueKind != JsonValueKind.Object)
                throw Invalid("Tools require unique names and object input schemas.");
            tools.Add(Mark(new JsonObject { ["name"] = tool.Name, ["description"] = tool.Description,
                ["input_schema"] = JsonNode.Parse(tool.InputSchema.GetRawText()) }, tool.Cache));
        }
        if (tools.Count > 0)
        {
            body["tools"] = tools;
            if (request.ToolChoice is { } choice)
            {
                var selected = new JsonObject { ["type"] = choice switch
                {
                    LlmToolChoice.Auto => "auto", LlmToolChoice.None => "none", LlmToolChoice.Required => "any",
                    LlmToolChoice.Named namedTool when names.Contains(namedTool.Name) => "tool",
                    _ => throw Invalid("Named tool choice must refer to a declared tool.")
                } };
                if (choice is LlmToolChoice.Named named) selected["name"] = named.Name;
                if (choice is not LlmToolChoice.None && choice.DisableParallelToolUse is { } parallel) selected["disable_parallel_tool_use"] = parallel;
                body["tool_choice"] = selected;
            }
        }
        if (request.System.Length > 0)
            body["system"] = new JsonArray(request.System.Select(part => (JsonNode)Mark(new JsonObject
                { ["type"] = "text", ["text"] = part.Text }, part.Cache)).ToArray());

        var messages = new JsonArray();
        var pendingTools = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < request.Messages.Length; index++)
        {
            var message = request.Messages[index];
            if (message.Content.IsDefault) throw Invalid("Message content must be initialized.");
            var content = new JsonArray();
            if (message.Role == LlmRole.System)
            {
                if (pendingTools.Count > 0) throw Invalid("System updates cannot split local tool calls from their results.");
                if (message.Content.Any(part => part is not LlmContent.Text)) throw Invalid("System updates accept text only.");
                var previous = index == 0 ? null : request.Messages[index - 1];
                var next = index + 1 < request.Messages.Length ? request.Messages[index + 1] : null;
                var native = NativeSystemUpdates(request.ModelId) && previous is not null
                    && (previous.Role is LlmRole.User or LlmRole.Tool || previous.Role == LlmRole.Assistant
                        && previous.Content.LastOrDefault() is LlmContent.ToolCall { ProviderExecuted: true })
                    && (next is null || next.Role == LlmRole.Assistant);
                if (native)
                {
                    foreach (var part in message.Content.Cast<LlmContent.Text>())
                        content.Add(Mark(new JsonObject { ["type"] = "text", ["text"] = part.Value }, part.Cache));
                    messages.Add(new JsonObject { ["role"] = "system", ["content"] = content });
                }
                else
                {
                    var text = "<system-update>\n" + string.Join('\n', message.Content.Cast<LlmContent.Text>().Select(part => part.Value))
                        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;") + "\n</system-update>";
                    var block = Mark(new JsonObject { ["type"] = "text", ["text"] = text }, message.Content.LastOrDefault()?.Cache);
                    if (messages.LastOrDefault() is JsonObject prior && prior["role"]?.GetValue<string>() == "user") prior["content"]!.AsArray().Add(block);
                    else messages.Add(new JsonObject { ["role"] = "user", ["content"] = new JsonArray(block) });
                }
                continue;
            }
            foreach (var part in message.Content)
            {
                if (message.Role is LlmRole.User or LlmRole.Assistant && part is LlmContent.Text text)
                {
                    content.Add(Mark(new JsonObject { ["type"] = "text", ["text"] = text.Value }, part.Cache));
                    continue;
                }
                if (message.Role == LlmRole.User && part is LlmContent.Media media)
                {
                    content.Add(Mark(Media(media), part.Cache));
                    continue;
                }
                if (message.Role == LlmRole.Assistant && part is LlmContent.Reasoning reasoning)
                {
                    var signature = reasoning.Encrypted ?? Metadata(reasoning, "signature")?.GetString();
                    var redacted = Metadata(reasoning, "redactedData")?.GetString();
                    if (signature is null && redacted is not null) content.Add(new JsonObject { ["type"] = "redacted_thinking", ["data"] = redacted });
                    else if (!string.IsNullOrWhiteSpace(signature)) content.Add(new JsonObject { ["type"] = "thinking", ["thinking"] = reasoning.Value, ["signature"] = signature });
                    else if (!string.IsNullOrWhiteSpace(reasoning.Value))
                        content.Add(request.Compatibility.RequireSignature == false
                            ? new JsonObject { ["type"] = "thinking", ["thinking"] = reasoning.Value, ["signature"] = "" }
                            : Mark(new JsonObject { ["type"] = "text", ["text"] = reasoning.Value }, part.Cache));
                    continue;
                }
                if (message.Role == LlmRole.Assistant && part is LlmContent.ToolCall call)
                {
                    if (!call.ProviderExecuted) pendingTools.Add(call.Id);
                    content.Add(new JsonObject { ["type"] = call.ProviderExecuted ? "server_tool_use" : "tool_use",
                        ["id"] = ToolId(call.Id), ["name"] = call.Name, ["input"] = JsonNode.Parse(call.Input.GetRawText()) });
                    continue;
                }
                if (message.Role == LlmRole.Assistant && part is LlmContent.ToolResult { ProviderExecuted: true } hosted)
                {
                    var type = hosted.Name switch { "web_search" => "web_search_tool_result", "code_execution" => "code_execution_tool_result",
                        "web_fetch" => "web_fetch_tool_result", _ => throw Unsupported("This hosted tool result cannot be replayed by Anthropic Messages.") };
                    var payload = Metadata(hosted, "result") ?? (hosted.Result switch
                    { LlmToolResult.Json json => json.Value, LlmToolResult.Error error => error.Value,
                        _ => throw Invalid("Hosted results require their provider replay JSON.") });
                    content.Add(new JsonObject { ["type"] = type, ["tool_use_id"] = ToolId(hosted.Id), ["content"] = JsonNode.Parse(payload.GetRawText()) });
                    continue;
                }
                if (message.Role == LlmRole.Tool && part is LlmContent.ToolResult result)
                {
                    if (result.ProviderExecuted) throw Invalid("Provider-executed results belong in assistant history.");
                    pendingTools.Remove(result.Id);
                    var block = new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = ToolId(result.Id), ["content"] = ToolResult(result.Result) };
                    if (result.Result is LlmToolResult.Error) block["is_error"] = true;
                    content.Add(Mark(block, part.Cache));
                    continue;
                }
                throw Unsupported("Unsupported Anthropic message content for this role.");
            }
            if (message.Role is not (LlmRole.User or LlmRole.Assistant or LlmRole.Tool)) throw Invalid("Unknown message role.");
            if (message.Role == LlmRole.Tool && messages.LastOrDefault() is JsonObject previousMessage
                && previousMessage["role"]?.GetValue<string>() == "user"
                && previousMessage["content"]!.AsArray().All(block => block?["type"]?.GetValue<string>() == "tool_result"))
                foreach (var block in content) previousMessage["content"]!.AsArray().Add(block?.DeepClone());
            else messages.Add(new JsonObject { ["role"] = message.Role == LlmRole.Assistant ? "assistant" : "user", ["content"] = content });
        }
        body["messages"] = messages;
        if (request.Generation is { } settings)
        {
            if (settings.Temperature is { } temperature) body["temperature"] = temperature;
            if (settings.TopP is { } topP) body["top_p"] = topP;
            if (settings.TopK is { } topK) body["top_k"] = topK;
            if (!settings.Stop.IsDefaultOrEmpty) body["stop_sequences"] = JsonSerializer.SerializeToNode(settings.Stop);
        }
        ApplyOptions(body, options);
        if (dropped > 0) System.Diagnostics.Trace.TraceWarning("Anthropic Messages: omitted {0} cache breakpoints beyond the four-marker limit.", dropped);
        return body;
    }

    private static void ApplyOptions(JsonObject body, JsonObject options)
    {
        if (options["thinking"] is { } thinkingNode)
        {
            var input = thinkingNode as JsonObject ?? throw Invalid("thinking must be an object.");
            LlmHttp.RequireFields(input, "type", "display", "budgetTokens", "budget_tokens");
            var type = input["type"]?.GetValue<string>();
            if (type is not ("adaptive" or "enabled" or "disabled")) throw Invalid("Unknown thinking type.");
            var thinking = new JsonObject { ["type"] = type };
            if (type == "enabled")
            {
                var budget = input["budgetTokens"] ?? input["budget_tokens"] ?? throw Invalid("Enabled thinking requires budgetTokens.");
                try { thinking["budget_tokens"] = CatalogNumbers.Finite(budget); }
                catch (JsonException) { throw Invalid("Thinking budget must be a finite JSON number."); }
            }
            if (type != "disabled" && input["display"] is { } display)
            {
                if (display.GetValue<string>() is not ("summarized" or "omitted")) throw Invalid("Unknown thinking display mode.");
                thinking["display"] = display.DeepClone();
            }
            body["thinking"] = thinking;
        }
        if ((options["service_tier"] ?? options["serviceTier"]) is { } service)
        {
            if (service.GetValue<string>() is not ("auto" or "standard_only")) throw Invalid("Unknown Anthropic service tier.");
            body["service_tier"] = service.DeepClone();
        }
        if (options["metadata"] is { } metadata)
        {
            var value = metadata as JsonObject ?? throw Invalid("metadata must be an object.");
            LlmHttp.RequireFields(value, "user_id");
            if (value["user_id"] is { } user) _ = user.GetValue<string>();
            body["metadata"] = value.DeepClone();
        }
        if ((options["inference_geo"] ?? options["inferenceGeo"]) is { } geo) { _ = geo.GetValue<string>(); body["inference_geo"] = geo.DeepClone(); }
        if (options["container"] is { } container)
        {
            if (container is JsonObject value)
            {
                LlmHttp.RequireFields(value, "id", "skills");
                if (value["id"] is { } id) _ = id.GetValue<string>();
                if (value["skills"] is { } skills && (skills is not JsonArray entries || entries.Any(item => item is not JsonObject))) throw Invalid("Container skills must be objects.");
            }
            else _ = container.GetValue<string>();
            body["container"] = container.DeepClone();
        }
        if ((options["cache_control"] ?? options["cacheControl"]) is { } cache)
        {
            var value = cache as JsonObject ?? throw Invalid("cache_control must be an object.");
            LlmHttp.RequireFields(value, "type", "ttl");
            if (value["type"]?.GetValue<string>() != "ephemeral" || value["ttl"] is { } ttl && ttl.GetValue<string>() is not ("5m" or "1h")) throw Invalid("Invalid cache control.");
            body["cache_control"] = value.DeepClone();
        }
        var output = options["output_config"] ?? options["outputConfig"];
        if (output is not null && output is not JsonObject) throw Invalid("output_config must be an object.");
        if (output is JsonObject fields) LlmHttp.RequireFields(fields, "effort", "format");
        var config = new JsonObject();
        if ((options["effort"] ?? output?["effort"]) is { } effort) { _ = effort.GetValue<string>(); config["effort"] = effort.DeepClone(); }
        if (output?["format"] is { } format)
        {
            if (format is not JsonObject value || value["type"]?.GetValue<string>() != "json_schema" || value["schema"] is not JsonObject)
                throw Invalid("Output format requires a JSON schema.");
            LlmHttp.RequireFields(value, "type", "schema");
            config["format"] = value.DeepClone();
        }
        if (config.Count > 0) body["output_config"] = config;
    }

    private static JsonObject Media(LlmContent.Media media)
    {
        var type = media.MediaType.ToLowerInvariant();
        var metadata = media.Metadata;
        var anthropic = metadata?.GetValueOrDefault("anthropic") ?? default;
        JsonElement Nested(string key) => anthropic.ValueKind == JsonValueKind.Object && anthropic.TryGetProperty(key, out var value) ? value : default;
        JsonElement General(string key) => metadata?.GetValueOrDefault(key) ?? default;
        string? Text(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        JsonElement Prefer(JsonElement primary, JsonElement fallback) => primary.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? fallback : primary;

        var fileId = Text(Nested("file_id")) ?? Text(Nested("fileId")) ?? Text(General("file_id")) ?? Text(General("fileId"));
        var source = string.IsNullOrEmpty(fileId)
            ? new JsonObject { ["type"] = "base64", ["media_type"] = type, ["data"] = media.Base64 }
            : new JsonObject { ["type"] = "file", ["file_id"] = fileId };
        if (type.StartsWith("image/", StringComparison.Ordinal))
        {
            var image = new JsonObject { ["type"] = "image", ["source"] = source };
            var transformations = Prefer(Nested("transformations"), General("transformations"));
            var oversized = transformations.ValueKind == JsonValueKind.Object && transformations.TryGetProperty("oversized_image", out var value)
                ? Text(value) : null;
            if (oversized is not ("downsize" or "error")) oversized = Text(Nested("oversized_image"));
            if (oversized is "downsize" or "error") image["transformations"] = new JsonObject { ["oversized_image"] = oversized };
            return image;
        }
        if (string.IsNullOrEmpty(fileId) && type == "text/plain")
        {
            source["type"] = "text";
            try { source["data"] = Encoding.UTF8.GetString(Convert.FromBase64String(media.Base64)); }
            catch (FormatException) { throw Invalid("Text document data must be base64."); }
        }
        else if (string.IsNullOrEmpty(fileId) && type != "application/pdf")
            throw Unsupported("Anthropic media lowering supports base64 images, PDFs, text documents, and file references only.");
        var document = new JsonObject { ["type"] = "document", ["source"] = source };
        var title = Text(Nested("title")) ?? Text(General("title")) ?? (string.IsNullOrEmpty(media.Filename) ? null : media.Filename);
        if (title is not null) document["title"] = title;
        var context = Text(Nested("context")) ?? Text(General("context"));
        if (context is not null) document["context"] = context;
        var citations = Prefer(Nested("citations"), General("citations"));
        if (citations.ValueKind == JsonValueKind.Object && citations.TryGetProperty("enabled", out var enabled) &&
            enabled.ValueKind is JsonValueKind.True or JsonValueKind.False)
            document["citations"] = new JsonObject { ["enabled"] = enabled.GetBoolean() };
        return document;
    }

    private static JsonNode? ToolResult(LlmToolResult result) => result switch
    {
        LlmToolResult.Text text => JsonValue.Create(text.Value),
        LlmToolResult.Json json => JsonValue.Create(json.Value.ValueKind == JsonValueKind.String ? json.Value.GetString() : json.Value.GetRawText()),
        LlmToolResult.Error error => JsonValue.Create(error.Value.ValueKind == JsonValueKind.String ? error.Value.GetString() : error.Value.GetRawText()),
        LlmToolResult.Content content => new JsonArray(content.Value.Select(part => part.Cache is not null
            ? throw Unsupported("Cache hints on nested tool-result content are not supported.") : part switch
            {
                LlmContent.Text text => (JsonNode)new JsonObject { ["type"] = "text", ["text"] = text.Value },
                LlmContent.Media media => Media(media),
                _ => throw Unsupported("Tool result content supports text and media only.")
            }).ToArray()),
        _ => throw Invalid("Unknown tool result type.")
    };

    private static bool NativeSystemUpdates(string modelId)
    {
        var match = Regex.Match(modelId.ToLowerInvariant(), @"(?:^|[./])claude-(?<family>fable|haiku|mythos|opus|sonnet)-(?<major>\d+)(?:[.-](?<minor>\d+))?", RegexOptions.NonBacktracking);
        if (!match.Success) return false;
        var major = double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        if (match.Groups[1].Value != "opus" || major != 4) return major >= 5;
        return match.Groups[3].Success && match.Groups[3].Length <= 2 && double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture) >= 8;
    }

    private static string ToolId(string id) => Regex.Replace(id, "[^a-zA-Z0-9_-]", "_", RegexOptions.NonBacktracking);
    private static JsonElement? Metadata(LlmContent part, string name) => part.ProviderMetadata.TryGetValue("anthropic", out var value)
        && value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var field) && field.ValueKind != JsonValueKind.Null ? field : null;
    private static LlmException Invalid(string message) => new(new LlmFailure.InvalidRequest(message));
    private static LlmException Unsupported(string message) => new(new LlmFailure.Unsupported(message));
}
