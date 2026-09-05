namespace OpenCode.Schema;

using System.Text.Json.Serialization;

public sealed record QuestionOption(
    [property: JsonPropertyName("label"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string Label,
    [property: JsonPropertyName("description"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string Description
) : IJsonOnSerializing
{
    void IJsonOnSerializing.OnSerializing() => SourceObjectContract.Required(Label, Description);
}

/// <summary>
/// 1:1 port of Question.Prompt from packages/schema/src/question.ts
/// </summary>
public sealed record QuestionPrompt(
    [property: JsonPropertyName("question"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string Question,
    [property: JsonPropertyName("header"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string Header,
    [property: JsonPropertyName("options"), JsonRequired, JsonConverter(typeof(PromptAttachmentListJsonConverter<QuestionOption>))] IReadOnlyList<QuestionOption> Options,
    [property: JsonPropertyName("multiple"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(OptionalValueJsonConverter<bool>))] bool? Multiple = null
) : IJsonOnSerializing
{
    void IJsonOnSerializing.OnSerializing() => SourceObjectContract.Required(Question, Header, Options);
}
