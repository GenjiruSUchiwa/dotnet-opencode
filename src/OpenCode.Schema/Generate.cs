namespace OpenCode.Schema;

using System.Text.Json.Serialization;

/// <summary>One stateless generation request. Model resolution belongs to Core Generate.</summary>
public sealed record GenerateTextInput(
    string Prompt,
    [property: JsonPropertyName("model"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<ModelRef>))] ModelRef? Model = null
)
{
    [JsonPropertyName("prompt"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string Prompt { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Prompt);
}

public sealed record GenerateTextResult(string Text)
{
    [JsonPropertyName("text"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string Text { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Text);
}
