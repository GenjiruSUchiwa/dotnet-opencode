namespace OpenCode.Schema;

using System.Text.Json.Serialization;

public sealed record PromptMention(
    [property: JsonPropertyName("start")] int Start,
    [property: JsonPropertyName("end")] int End,
    [property: JsonPropertyName("text")] string Text
);

public sealed record PromptFileAttachment(
    [property: JsonPropertyName("data")] string Data,
    [property: JsonPropertyName("mime")] string Mime,
    [property: JsonPropertyName("name")] string? Name = null,
    [property: JsonPropertyName("description")] string? Description = null,
    [property: JsonPropertyName("mention")] PromptMention? Mention = null
);

public sealed record PromptAgentAttachment(
    [property: JsonPropertyName("agent")] string Agent,
    [property: JsonPropertyName("mention")] PromptMention? Mention = null
);

public sealed record PromptSkillAttachment(
    [property: JsonPropertyName("skill")] string Skill,
    [property: JsonPropertyName("mention")] PromptMention? Mention = null
);

/// <summary>
/// 1:1 port of Prompt from packages/schema/src/prompt.ts
/// </summary>
public sealed record PromptInput(
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("files")] IReadOnlyList<PromptFileAttachment>? Files = null,
    [property: JsonPropertyName("agents")] IReadOnlyList<PromptAgentAttachment>? Agents = null,
    [property: JsonPropertyName("skills")] IReadOnlyList<PromptSkillAttachment>? Skills = null,
    [property: JsonPropertyName("resume")] bool Resume = true
);
