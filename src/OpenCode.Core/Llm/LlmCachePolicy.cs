namespace OpenCode.Core.Llm;

/// <summary>The source's default cache policy, independent of Session cache lineage.</summary>
internal static class LlmCachePolicy
{
    internal static LlmRequest AnthropicDefault(LlmRequest request)
    {
        // cache-policy.ts reserves manual placements first. Auto fills up to four
        // slots in order: last tool, first/last system, final message boundary.
        var remaining = Math.Max(0, 4 - request.Tools.Count(tool => tool.Cache is not null)
            - request.System.Count(part => part.Cache is not null)
            - request.Messages.Sum(message => message.Content.Count(part => part.Cache is not null)));
        if (remaining == 0) return request;
        var hint = new LlmCacheHint(LlmCacheKind.Ephemeral);
        var tools = request.Tools;
        if (tools.Length > 0 && tools[^1].Cache is null)
        {
            tools = tools.SetItem(tools.Length - 1, tools[^1] with { Cache = hint });
            remaining--;
        }
        var system = request.System;
        foreach (var index in new[] { 0, system.Length - 1 }.Distinct())
        {
            if (index < 0 || index >= system.Length || system[index].Cache is not null || remaining == 0) continue;
            system = system.SetItem(index, system[index] with { Cache = hint });
            remaining--;
        }
        var messages = request.Messages;
        if (remaining > 0 && messages.Length > 0 && messages[^1].Content.Length > 0)
        {
            var content = messages[^1].Content;
            var index = Enumerable.Range(0, content.Length).LastOrDefault(index => content[index] is LlmContent.Text, -1);
            if (index < 0) index = content.Length - 1;
            if (content[index].Cache is null)
                messages = messages.SetItem(messages.Length - 1, messages[^1] with
                { Content = content.SetItem(index, content[index] with { Cache = hint }) });
        }
        return request with { Tools = tools, System = system, Messages = messages };
    }

    internal static string? PromptKey(string? key)
    {
        if (key is null) return null;
        // Array.from(key).slice(0, 64): count surrogate pairs once, but preserve
        // unpaired UTF-16 code units rather than replacing them with U+FFFD.
        var offset = 0;
        for (var count = 0; count < 64 && offset < key.Length; count++, offset++)
            if (char.IsHighSurrogate(key[offset]) && offset + 1 < key.Length && char.IsLowSurrogate(key[offset + 1])) offset++;
        return offset == key.Length ? key : key[..offset];
    }
}
