namespace OpenCode.Schema;

using System.Text.Json.Serialization;

public sealed record QuestionOption(
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("description")] string Description
);

/// <summary>
/// 1:1 port of Question.Prompt from packages/schema/src/question.ts
/// </summary>
public sealed record QuestionPrompt(
    [property: JsonPropertyName("question")] string Question,
    [property: JsonPropertyName("header")] string Header,
    [property: JsonPropertyName("options")] IReadOnlyList<QuestionOption> Options,
    [property: JsonPropertyName("multiple")] bool? Multiple = null
);
