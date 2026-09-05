namespace OpenCode.Core.Tools.Builtins;

using System.Globalization;
using System.Text.Json;
using OpenCode.Core.Forms;
using OpenCode.Core.Permissions;
using OpenCode.Schema;

public sealed class QuestionCancelledException : Exception
{
    public QuestionCancelledException() : base("The user dismissed this question") { }
}

/// <summary>Location-owned question leaf. The supplied Forms service is the one exposed by HTTP/MCP.</summary>
public sealed class QuestionTool(FormService forms, PermissionService permissions)
{
    public const string Name = "question";
    public ToolInfo Create() => ToolInfo.FromJson(Name,
        "Use this tool when you need to ask the user questions during execution. This allows you to:\n1. Gather user preferences or requirements\n2. Clarify ambiguous instructions\n3. Get decisions on implementation choices as you work\n4. Offer choices to the user about what direction to take.\n\nUsage notes:\n- A \"Type your own answer\" option is added automatically; don't include a separate option for free form answers\n- Set `multiple: true` to allow selecting more than one option\n- If you recommend a specific option, make that the first option in the list and add \"(Recommended)\" at the end of the label",
        JsonSerializer.Deserialize<JsonElement>("""
            {"type":"object","properties":{"questions":{"type":"array","minItems":1,"description":"Questions to ask","items":{"type":"object","properties":{"question":{"type":"string","description":"Complete question"},"header":{"type":"string","description":"Very short label (max 30 chars)"},"options":{"type":"array","items":{"type":"object","properties":{"label":{"type":"string"},"description":{"type":"string"}},"required":["label","description"]}},"multiple":{"type":"boolean"}},"required":["question","header","options"]}}},"required":["questions"]}
            """), ExecuteAsync, JsonSerializer.Deserialize<JsonElement>("""
            {"type":"object","properties":{"answers":{"type":"array","items":{"type":"array","items":{"type":"string"}}}},"required":["answers"]}
            """), new ToolOptions(CodeMode: false));

    private async Task<ToolExecutionResult> ExecuteAsync(JsonElement input, ToolContext context, CancellationToken ct)
    {
        try
        {
            await permissions.AssertAsync(new PermissionAskInput(context.SessionId, Name, ["*"], Agent: context.AgentId,
                Source: new PermissionSource("tool", context.RequireMessageId().Value, context.CallId)), ct).ConfigureAwait(true);
        }
        catch (Exception error) when (error is PermissionBlockedException or PermissionCorrectedException)
        { throw new ToolExecutionException("Permission denied: question", error); }
        var questions = input.GetProperty("questions").EnumerateArray().ToArray();
        var fields = questions.Select((question, index) =>
        {
            var options = question.GetProperty("options").EnumerateArray().Select(option =>
                new FormOption(option.GetProperty("label").GetString()!, option.GetProperty("label").GetString()!, option.GetProperty("description").GetString()!)).ToArray();
            var key = "q" + index.ToString(CultureInfo.InvariantCulture);
            var title = question.GetProperty("header").GetString()!;
            var description = question.GetProperty("question").GetString()!;
            return question.TryGetProperty("multiple", out var multiple) && multiple.GetBoolean()
                ? (FormField)new FormMultiselectField { Key = key, Title = title, Description = description, Options = options, Custom = true }
                : new FormStringField { Key = key, Title = title, Description = description, Options = options, Custom = true };
        }).ToArray();
        var state = await forms.AskAsync(context.SessionId.Value, new FormCreatePayload("Questions", fields, Metadata:
            new Dictionary<string, JsonElement>
            {
                ["kind"] = JsonSerializer.SerializeToElement("question"),
                ["tool"] = JsonSerializer.SerializeToElement(new { messageID = context.RequireMessageId().Value, id = context.CallId })
            }), ct).ConfigureAwait(true);
        ct.ThrowIfCancellationRequested();
        if (state is FormCancelledState) throw new QuestionCancelledException();
        if (state is not FormAnsweredState answered) throw new ToolContractException("Form ask returned an unsettled question.");
        var answers = questions.Select((_, index) => answered.Answer.GetValueOrDefault("q" + index.ToString(CultureInfo.InvariantCulture)) switch
        {
            null => Array.Empty<string>(),
            FormValue.Strings strings => strings.Value.ToArray(),
            FormValue.Text text => [text.Value],
            FormValue.Boolean boolean => [boolean.Value ? "true" : "false"],
            FormValue.Number number => [number.Value.ToString(CultureInfo.InvariantCulture)],
            _ => throw new ToolContractException("Question returned an unsupported answer.")
        }).ToArray();
        var formatted = string.Join(", ", questions.Select((question, index) =>
            $"\"{question.GetProperty("question").GetString()}\"=\"{(answers[index].Length > 0 ? string.Join(", ", answers[index]) : "Unanswered")}\""));
        return new ToolExecutionResult($"User has answered your questions: {formatted}. You can now continue with the user's answers in mind.",
            new { answers }, new Dictionary<string, object> { ["answers"] = answers });
    }
}
