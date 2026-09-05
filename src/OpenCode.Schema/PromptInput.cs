namespace OpenCode.Schema;

using System.Text.Json.Serialization;

public sealed record PromptInputFileAttachment(
    string Uri,
    [property: JsonPropertyName("name"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string? Name = null,
    [property: JsonPropertyName("description"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string? Description = null,
    [property: JsonPropertyName("mention"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<PromptMention>))] PromptMention? Mention = null
)
{
    [JsonPropertyName("uri"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string Uri { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Uri);

    public static PromptInputFileAttachment Create(PromptInputFileAttachment input) =>
        new(input.Uri, input.Name, input.Description, input.Mention);
}

public sealed record PromptInputSkillAttachment(
    SkillId Id,
    [property: JsonPropertyName("mention"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<PromptMention>))] PromptMention? Mention = null
)
{
    [JsonPropertyName("id"), JsonRequired]
    public SkillId Id { get; init => field = PromptValidation.Skill(value); } = PromptValidation.Skill(Id);
}

/// <summary>Unprepared prompt: URI files and skill IDs; no resume, delivery, or model defaults.</summary>
public sealed record PromptInput(
    string Text,
    IReadOnlyList<PromptInputFileAttachment>? Files = null,
    IReadOnlyList<PromptAgentAttachment>? Agents = null,
    IReadOnlyList<PromptInputSkillAttachment>? Skills = null
)
{
    [JsonPropertyName("text"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string Text { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Text);

    [JsonPropertyName("files"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(PromptAttachmentListJsonConverter<PromptInputFileAttachment>))]
    public IReadOnlyList<PromptInputFileAttachment>? Files { get; init => field = PromptValidation.Attachments(value); } = PromptValidation.Attachments(Files);

    [JsonPropertyName("agents"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(PromptAttachmentListJsonConverter<PromptAgentAttachment>))]
    public IReadOnlyList<PromptAgentAttachment>? Agents { get; init => field = PromptValidation.Attachments(value); } = PromptValidation.Attachments(Agents);

    [JsonPropertyName("skills"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(PromptAttachmentListJsonConverter<PromptInputSkillAttachment>))]
    public IReadOnlyList<PromptInputSkillAttachment>? Skills { get; init => field = PromptValidation.Attachments(value); } = PromptValidation.Attachments(Skills);
}
