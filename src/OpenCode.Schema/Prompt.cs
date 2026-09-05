namespace OpenCode.Schema;

using System.Text.Json;
using System.Text.Json.Serialization;

public sealed record PromptMention(double Start, double End, string Text)
{
    [JsonPropertyName("start"), JsonRequired, JsonConverter(typeof(FiniteNumberJsonConverter))]
    public double Start { get; init => field = FiniteNumberJsonConverter.Validate(value); } = FiniteNumberJsonConverter.Validate(Start);

    [JsonPropertyName("end"), JsonRequired, JsonConverter(typeof(FiniteNumberJsonConverter))]
    public double End { get; init => field = FiniteNumberJsonConverter.Validate(value); } = FiniteNumberJsonConverter.Validate(End);

    [JsonPropertyName("text"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string Text { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Text);
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(PromptInlineFileSource), "inline")]
[JsonDerivedType(typeof(PromptUriFileSource), "uri")]
public abstract record PromptFileSource;

public sealed record PromptInlineFileSource : PromptFileSource;

public sealed record PromptUriFileSource(string Uri) : PromptFileSource
{
    [JsonPropertyName("uri"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string Uri { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Uri);
}

public static class PromptBase64
{
    public static bool IsValid(ReadOnlySpan<char> value)
    {
        if (value.Length % 4 != 0) return false;
        var end = value.Length;
        if (end > 0 && value[end - 1] == '=')
        {
            end--;
            if (value[end - 1] == '=') end--;
        }
        for (var i = 0; i < end; i++)
            if (!char.IsAsciiLetterOrDigit(value[i]) && value[i] is not ('+' or '/')) return false;
        return true;
    }

    public static string Create(string value)
    {
        if (value is null || !IsValid(value.AsSpan())) throw new JsonException("Expected padded standard base64 without whitespace.");
        return value;
    }
}

public sealed record PromptFileAttachment(
    string Data,
    string Mime,
    PromptFileSource Source,
    [property: JsonPropertyName("name"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string? Name = null,
    [property: JsonPropertyName("description"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string? Description = null,
    [property: JsonPropertyName("mention"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<PromptMention>))] PromptMention? Mention = null
)
{
    [JsonPropertyName("data"), JsonRequired, JsonConverter(typeof(PromptBase64JsonConverter))]
    public string Data { get; init => field = PromptBase64.Create(value); } = PromptBase64.Create(Data);

    [JsonPropertyName("mime"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string Mime { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Mime);

    [JsonPropertyName("source"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<PromptFileSource>))]
    public PromptFileSource Source { get; init => field = PromptValidation.Source(value); } = PromptValidation.Source(Source);

    public static PromptFileAttachment Create(PromptFileAttachment input) =>
        new(input.Data, input.Mime, input.Source, input.Name, input.Description, input.Mention);
}

public sealed record PromptAgentAttachment(
    string Name,
    [property: JsonPropertyName("mention"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<PromptMention>))] PromptMention? Mention = null
)
{
    [JsonPropertyName("name"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string Name { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Name);
}

public sealed record PromptSkillAttachment(
    SkillId Id,
    string Name,
    [property: JsonPropertyName("text"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string? Text = null,
    [property: JsonPropertyName("mention"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<PromptMention>))] PromptMention? Mention = null
)
{
    [JsonPropertyName("id"), JsonRequired]
    public SkillId Id { get; init => field = PromptValidation.Skill(value); } = PromptValidation.Skill(Id);

    [JsonPropertyName("name"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string Name { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Name);
}

/// <summary>Prepared model-facing prompt, distinct from URI-based PromptInput and admission options.</summary>
public sealed record Prompt(
    string Text,
    IReadOnlyList<PromptFileAttachment>? Files = null,
    IReadOnlyList<PromptAgentAttachment>? Agents = null,
    IReadOnlyList<PromptSkillAttachment>? Skills = null
)
{
    [JsonPropertyName("text"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string Text { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Text);

    [JsonPropertyName("files"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(PromptAttachmentListJsonConverter<PromptFileAttachment>))]
    public IReadOnlyList<PromptFileAttachment>? Files { get; init => field = PromptValidation.Attachments(value); } = PromptValidation.Attachments(Files);

    [JsonPropertyName("agents"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(PromptAttachmentListJsonConverter<PromptAgentAttachment>))]
    public IReadOnlyList<PromptAgentAttachment>? Agents { get; init => field = PromptValidation.Attachments(value); } = PromptValidation.Attachments(Agents);

    [JsonPropertyName("skills"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(PromptAttachmentListJsonConverter<PromptSkillAttachment>))]
    public IReadOnlyList<PromptSkillAttachment>? Skills { get; init => field = PromptValidation.Attachments(value); } = PromptValidation.Attachments(Skills);

    public static Prompt FromUserMessage(UserMessage input) => new(input.Text, input.Files, input.Agents, input.Skills);

    public static bool Equivalence(Prompt left, Prompt right) => left.Text == right.Text
        && EquivalentAttachments(left.Files, right.Files)
        && EquivalentAttachments(left.Agents, right.Agents)
        && EquivalentAttachments(left.Skills, right.Skills);

    private static bool EquivalentAttachments<T>(IReadOnlyList<T>? left, IReadOnlyList<T>? right) =>
        ReferenceEquals(left, right) || (left is not null && right is not null && left.SequenceEqual(right));
}

internal static class PromptValidation
{
    internal static T Required<T>(T? value) where T : class =>
        value ?? throw new JsonException("Required prompt value cannot be null.");

    internal static SkillId Skill(SkillId value) => value.IsInitialized()
        ? value : throw new JsonException("Prompt skill attachment requires an id string.");

    internal static PromptFileSource Source(PromptFileSource source) => source switch
    {
        PromptInlineFileSource or PromptUriFileSource => source,
        _ => throw new JsonException("Prompt file source must be inline or uri.")
    };

    internal static IReadOnlyList<T>? Attachments<T>(IReadOnlyList<T>? values) where T : class
    {
        if (values is not null)
            foreach (var value in values) Required(value);
        return values;
    }
}
