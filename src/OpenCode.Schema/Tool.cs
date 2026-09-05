namespace OpenCode.Schema;

using System.Text.Json.Serialization;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ToolTextContent), "text")]
[JsonDerivedType(typeof(ToolFileContent), "file")]
public abstract record ToolContent;

public sealed record ToolTextContent(string Text) : ToolContent
{
    [JsonPropertyName("text"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string Text { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Text);
}

public sealed record ToolFileContent(
    string Uri,
    string Mime,
    [property: JsonPropertyName("name"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string? Name = null
) : ToolContent
{
    [JsonPropertyName("uri"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string Uri { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Uri);

    [JsonPropertyName("mime"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string Mime { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Mime);
}

public sealed record ToolContext(
    SessionId SessionId,
    AgentId AgentId,
    MessageId? MessageId,
    string CallId,
    Func<IReadOnlyDictionary<string, object>, Task> ReportProgress
)
{
    // Command interpolation has no model message. Model tools must require their
    // genuine source instead of publishing a default/fabricated ID.
    public MessageId RequireMessageId() => MessageId ?? throw new InvalidOperationException("This operation requires a model tool message source.");
}

public sealed record ToolResult(
    string? Output = null,
    IReadOnlyList<ToolContent>? Content = null,
    IReadOnlyDictionary<string, object>? Metadata = null
);
