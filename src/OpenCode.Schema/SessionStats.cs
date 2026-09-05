namespace OpenCode.Schema;

using System.Text.Json.Serialization;

public sealed record SessionStatsActivity(
    [property: JsonPropertyName("date"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string Date,
    [property: JsonPropertyName("steps"), JsonRequired, JsonConverter(typeof(NonNegativeIntegerJsonConverter<int>))] int Steps
) : IJsonOnSerializing
{
    void IJsonOnSerializing.OnSerializing() => SourceObjectContract.Required(Date);
}

public sealed record SessionStatsModelUsage(
    [property: JsonPropertyName("model"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<ModelRef>))] ModelRef Model,
    [property: JsonPropertyName("steps"), JsonRequired, JsonConverter(typeof(NonNegativeIntegerJsonConverter<int>))] int Steps,
    [property: JsonPropertyName("tokens"), JsonRequired] TokenUsageInfo Tokens,
    [property: JsonPropertyName("cost"), JsonRequired] Money Cost
) : IJsonOnSerializing
{
    void IJsonOnSerializing.OnSerializing() => SourceObjectContract.Required(Model, Tokens);
}

public sealed record SessionStatsToolUsage(
    [property: JsonPropertyName("name"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string Name,
    [property: JsonPropertyName("calls"), JsonRequired, JsonConverter(typeof(NonNegativeIntegerJsonConverter<int>))] int Calls,
    [property: JsonPropertyName("succeeded"), JsonRequired, JsonConverter(typeof(NonNegativeIntegerJsonConverter<int>))] int Succeeded,
    [property: JsonPropertyName("failed"), JsonRequired, JsonConverter(typeof(NonNegativeIntegerJsonConverter<int>))] int Failed,
    [property: JsonPropertyName("unfinished"), JsonRequired, JsonConverter(typeof(NonNegativeIntegerJsonConverter<int>))] int Unfinished,
    [property: JsonPropertyName("durationP50"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(OptionalFiniteNumberJsonConverter))] double? DurationP50 = null
) : IJsonOnSerializing
{
    void IJsonOnSerializing.OnSerializing() => SourceObjectContract.Required(Name);
}

public sealed record SessionStatsToolTotals(
    [property: JsonPropertyName("calls"), JsonRequired, JsonConverter(typeof(NonNegativeIntegerJsonConverter<int>))] int Calls,
    [property: JsonPropertyName("succeeded"), JsonRequired, JsonConverter(typeof(NonNegativeIntegerJsonConverter<int>))] int Succeeded,
    [property: JsonPropertyName("failed"), JsonRequired, JsonConverter(typeof(NonNegativeIntegerJsonConverter<int>))] int Failed,
    [property: JsonPropertyName("unfinished"), JsonRequired, JsonConverter(typeof(NonNegativeIntegerJsonConverter<int>))] int Unfinished
);

public sealed record SessionStatsDateRange(
    [property: JsonPropertyName("from"), JsonRequired, JsonConverter(typeof(EpochMillisecondsJsonConverter))] DateTimeOffset From,
    [property: JsonPropertyName("to"), JsonRequired, JsonConverter(typeof(EpochMillisecondsJsonConverter))] DateTimeOffset To
);

[JsonConverter(typeof(SourceStringEnumJsonConverter<SessionStatsToolMode>))]
public enum SessionStatsToolMode
{
    [JsonStringEnumMemberName("none")] None,
    [JsonStringEnumMemberName("summary")] Summary,
    [JsonStringEnumMemberName("detail")] Detail
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "mode")]
[JsonDerivedType(typeof(SessionStatsToolsNone), "none")]
[JsonDerivedType(typeof(SessionStatsToolsSummary), "summary")]
[JsonDerivedType(typeof(SessionStatsToolsDetail), "detail")]
public abstract record SessionStatsTools;
public sealed record SessionStatsToolsNone : SessionStatsTools;
public sealed record SessionStatsToolsSummary(
    [property: JsonPropertyName("totals"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<SessionStatsToolTotals>))] SessionStatsToolTotals Totals) : SessionStatsTools, IJsonOnSerializing
{
    void IJsonOnSerializing.OnSerializing() => SourceObjectContract.Required(Totals);
}
public sealed record SessionStatsToolsDetail(
    [property: JsonPropertyName("totals"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<SessionStatsToolTotals>))] SessionStatsToolTotals Totals,
    [property: JsonPropertyName("usage"), JsonRequired, JsonConverter(typeof(PromptAttachmentListJsonConverter<SessionStatsToolUsage>))] IReadOnlyList<SessionStatsToolUsage> Usage) : SessionStatsTools, IJsonOnSerializing
{
    void IJsonOnSerializing.OnSerializing() => SourceObjectContract.Required(Totals, Usage);
}

/// <summary>
/// 1:1 port of SessionStats.Info from packages/schema/src/session-stats.ts
/// </summary>
public sealed record SessionStatsInfo(
    [property: JsonPropertyName("range"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<SessionStatsDateRange>))] SessionStatsDateRange Range,
    [property: JsonPropertyName("sessions"), JsonRequired, JsonConverter(typeof(NonNegativeIntegerJsonConverter<int>))] int Sessions,
    [property: JsonPropertyName("subagents"), JsonRequired, JsonConverter(typeof(NonNegativeIntegerJsonConverter<int>))] int Subagents,
    [property: JsonPropertyName("prompts"), JsonRequired, JsonConverter(typeof(NonNegativeIntegerJsonConverter<int>))] int Prompts,
    [property: JsonPropertyName("steps"), JsonRequired, JsonConverter(typeof(NonNegativeIntegerJsonConverter<int>))] int Steps,
    [property: JsonPropertyName("tokens"), JsonRequired] TokenUsageInfo Tokens,
    [property: JsonPropertyName("cost"), JsonRequired] Money Cost,
    [property: JsonPropertyName("activeDays"), JsonRequired, JsonConverter(typeof(NonNegativeIntegerJsonConverter<int>))] int ActiveDays,
    [property: JsonPropertyName("streak"), JsonRequired, JsonConverter(typeof(NonNegativeIntegerJsonConverter<int>))] int Streak,
    [property: JsonPropertyName("activity"), JsonRequired, JsonConverter(typeof(PromptAttachmentListJsonConverter<SessionStatsActivity>))] IReadOnlyList<SessionStatsActivity> Activity,
    [property: JsonPropertyName("models"), JsonRequired, JsonConverter(typeof(PromptAttachmentListJsonConverter<SessionStatsModelUsage>))] IReadOnlyList<SessionStatsModelUsage> Models,
    [property: JsonPropertyName("tools"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<SessionStatsTools>))] SessionStatsTools Tools
) : IJsonOnSerializing
{
    void IJsonOnSerializing.OnSerializing() => SourceObjectContract.Required(Range, Tokens, Activity, Models, Tools);
}
