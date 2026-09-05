namespace OpenCode.Core.Session;

using System.Text.Json;
using System.Text.Json.Nodes;
using OpenCode.Core.Config;
using OpenCode.Core.Instructions;
using OpenCode.Core.Llm;

internal sealed record CompactionSettings(bool Auto, double Buffer, double KeepTokens)
{
    internal static CompactionSettings Read(string directory, JsonObject merged)
    {
        var home = Path.GetFullPath(Environment.GetEnvironmentVariable("OPENCODE_TEST_HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        var sources = ProducerConfiguration.Read(directory, home, Path.GetFullPath(ConfigLoader.GetDefaultConfigDirectory()), merged);
        sources.RequireNoPluginSources();
        var result = new CompactionSettings(true, 20_000, 15_000);
        foreach (var document in sources.Documents)
        {
            if (document.Info["compaction"] is null) continue;
            if (document.Info["compaction"] is not JsonObject value || value.Any(pair => pair.Key is not ("auto" or "buffer" or "keep")))
                throw new JsonException("Invalid compaction configuration.");
            if (value.ContainsKey("auto")) result = result with
            {
                Auto = value["auto"] is JsonValue auto && auto.TryGetValue<bool>(out var enabled) ? enabled : throw new JsonException("Compaction auto must be boolean.")
            };
            if (value.ContainsKey("buffer")) result = result with { Buffer = Number(value["buffer"]) };
            if (value.ContainsKey("keep"))
            {
                if (value["keep"] is not JsonObject keep || keep.Any(pair => pair.Key != "tokens")) throw new JsonException("Invalid compaction keep configuration.");
                if (keep.ContainsKey("tokens")) result = result with { KeepTokens = Number(keep["tokens"]) };
            }
        }
        return result;
    }

    internal bool Required(IReadOnlyList<JsonElement> messages, CatalogModelInfo model)
    {
        if (!Auto) return false;
        var last = messages.LastOrDefault(message => message.GetProperty("type").GetString() == "assistant" && message.TryGetProperty("tokens", out _));
        if (last.ValueKind == JsonValueKind.Undefined) return false;
        var tokens = last.GetProperty("tokens");
        var used = tokens.GetProperty("input").GetDouble() + tokens.GetProperty("output").GetDouble() + tokens.GetProperty("reasoning").GetDouble() +
            tokens.GetProperty("cache").GetProperty("read").GetDouble() + tokens.GetProperty("cache").GetProperty("write").GetDouble();
        if (used <= 0) return false;
        var context = model.Limit?.Context ?? throw new CatalogMetadataUnavailableException("Automatic compaction requires the selected model's context limit.");
        if (context <= 0) return false;
        var output = Math.Min(model.Limit!.Output ?? throw new CatalogMetadataUnavailableException("Automatic compaction requires the selected model's output limit."), 32_000);
        return used >= Math.Min(model.Limit.Input is { } input ? input - Buffer : double.PositiveInfinity, context - Math.Max(output, Buffer));
    }

    private static double Number(JsonNode? value) => value is JsonValue number && number.TryGetValue<double>(out var result) &&
        double.IsFinite(result) && result >= 0 && result == Math.Truncate(result) ? result : throw new JsonException("Compaction budgets must be nonnegative integers.");
}

internal sealed record CompactionPlan(string Prompt, string Recent)
{
    // packages/core/src/session/compaction.ts SUMMARY_TEMPLATE, not a substitute system prompt.
    private const string Template = """
        Output exactly the Markdown structure shown inside <template> and keep the section order unchanged. Do not include the <template> tags in your response.
        <template>
        ## Objective
        - [one or two brief sentences describing what the user is trying to accomplish]

        ## Important Details
        - [constraints/preferences, decisions and why, important facts/assumptions, exact context needed to continue, or "(none)"]

        ## Work State
        ### Completed
        - [finished work, verified facts, or changes made; otherwise "(none)"]

        ### Active
        - [current work, partial changes, or investigation state; otherwise "(none)"]

        ### Blocked
        - [blockers, failing commands, or unknowns; otherwise "(none)"]

        ## Next Move
        1. [immediate concrete action, or "(none)"]
        2. [next action if known, or "(none)"]

        ## Relevant Files
        - [file or directory path: why it matters, or "(none)"]
        </template>

        Rules:
        - Keep every section, even when empty.
        - Use terse bullets, not prose paragraphs.
        - Preserve exact file paths, symbols, commands, error strings, URLs, and identifiers when known.
        - Do not mention the summary process or that context was compacted.
        """;

    internal static CompactionPlan? Create(IReadOnlyList<JsonElement> messages, double keepTokens)
    {
        var conversation = messages.Where(message => message.GetProperty("type").GetString() is not ("compaction" or "system"))
            .Select(message => (Message: message, Text: Serialize(message))).Where(item => item.Text.Length > 0).ToArray();
        if (conversation.Length == 0) return null;
        double total = 0;
        var split = conversation.Length;
        for (var index = conversation.Length - 1; index >= 0; index--)
        {
            var next = total + Math.Floor(conversation[index].Text.Length / 4d + 0.5);
            if (split < conversation.Length && next > keepTokens) break;
            total = next;
            split = index;
        }
        while (split > 0 && conversation[split].Message.GetProperty("type").GetString() != "user") split--;
        if (split == 0)
        {
            var latestUser = Array.FindLastIndex(conversation, item => item.Message.GetProperty("type").GetString() == "user");
            if (latestUser > 0) split = latestUser;
        }
        var head = string.Join("\n\n", conversation.Take(split).Select(item => item.Text));
        var recent = string.Join("\n\n", conversation.Skip(split).Select(item => item.Text));
        var checkpoint = messages.LastOrDefault(message => message.GetProperty("type").GetString() == "compaction" && message.GetProperty("status").GetString() == "completed");
        var previousSummary = checkpoint.ValueKind == JsonValueKind.Undefined ? "" : checkpoint.GetProperty("summary").GetString()!;
        var previousRecent = checkpoint.ValueKind == JsonValueKind.Undefined ? "" : checkpoint.GetProperty("recent").GetString()!;
        var summarizeRecent = previousRecent.Length == 0 && head.Length == 0;
        var context = summarizeRecent ? new[] { recent } : new[] { previousRecent, head }.Where(value => value.Length > 0);
        var intro = previousSummary.Length > 0
            ? $"Update the anchored summary below using the conversation history below.\nPreserve still-true details, remove stale details, and merge in the new facts.\n<previous-summary>\n{previousSummary}\n</previous-summary>"
            : "Create a new anchored summary from the conversation history.";
        return new CompactionPlan(string.Join("\n\n", new[] { intro, Template, "The following is the conversation history:" }.Concat(context)), summarizeRecent ? "" : recent);
    }

    private static string Serialize(JsonElement message)
    {
        switch (message.GetProperty("type").GetString())
        {
            case "user":
                var skills = message.TryGetProperty("skills", out var activated)
                    ? activated.EnumerateArray().Where(skill => skill.TryGetProperty("text", out _)).Select(skill => $"[Skill activated: {skill.GetProperty("name").GetString()}]\n{skill.GetProperty("text").GetString()}") : [];
                var files = message.TryGetProperty("files", out var attached) ? attached.EnumerateArray().Select(file =>
                    $"[Attached {file.GetProperty("mime").GetString()}: {(file.TryGetProperty("name", out var name) ? name.GetString() : file.GetProperty("source").GetProperty("type").GetString() == "uri" ? file.GetProperty("source").GetProperty("uri").GetString() : "inline attachment") }]") : [];
                return string.Join("\n", skills.Append("[User]: " + message.GetProperty("text").GetString()).Concat(files));
            case "location-switched": return "[User]: The working directory has been changed to " + message.GetProperty("location").GetProperty("directory").GetString() + ".";
            case "assistant":
                return string.Join("\n", message.GetProperty("content").EnumerateArray().SelectMany(part =>
                {
                    var type = part.GetProperty("type").GetString();
                    if (type == "text") return new[] { "[Assistant]: " + part.GetProperty("text").GetString() };
                    if (type == "reasoning") return part.GetProperty("text").GetString() is { Length: > 0 } text ? ["[Assistant reasoning]: " + text] : [];
                    var state = part.GetProperty("state");
                    var input = state.GetProperty("input");
                    var call = $"[Assistant tool call]: {part.GetProperty("name").GetString()}({(input.ValueKind == JsonValueKind.String ? input.GetString() : InstructionJson.Stringify(input))})";
                    return state.GetProperty("status").GetString() switch
                    {
                        "completed" => [call, "[Tool result]: " + Truncate(string.Join("\n", state.GetProperty("content").EnumerateArray().Select(item =>
                            item.GetProperty("type").GetString() == "text" ? item.GetProperty("text").GetString() :
                                $"[Attached {item.GetProperty("mime").GetString()}{(item.TryGetProperty("name", out var name) ? ": " + name.GetString() : "")}]")))],
                        "error" => [call, "[Tool error]: " + state.GetProperty("error").GetProperty("message").GetString()],
                        _ => new[] { call }
                    };
                }));
            case "system": return "[System update]: " + message.GetProperty("text").GetString();
            case "synthetic": return "[Synthetic context]: " + message.GetProperty("text").GetString();
            case "skill": return $"[Skill activated: {message.GetProperty("name").GetString()}]\n{message.GetProperty("text").GetString()}";
            case "shell":
                if (message.TryGetProperty("metadata", out var metadata) && metadata.TryGetProperty("background", out var background) && background.ValueKind == JsonValueKind.True) return "";
                return $"[Shell]: {message.GetProperty("command").GetString()}\n{Truncate(message.TryGetProperty("output", out var output) ? output.GetProperty("output").GetString() ?? "" : "")}";
            default: return "";
        }
    }

    private static string Truncate(string value)
    {
        if (value.Length <= 2000) return value;
        var end = 0;
        for (var count = 0; count < 2000 && end < value.Length; count++)
            end += char.IsHighSurrogate(value[end]) && end + 1 < value.Length && char.IsLowSurrogate(value[end + 1]) ? 2 : 1;
        return end == value.Length ? value : value[..end] + "\n[truncated]";
    }
}
