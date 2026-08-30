namespace OpenCode.Schema;

using System.Text.Json.Serialization;

public sealed record SessionStatsActivity(
    [property: JsonPropertyName("date")] string Date,
    [property: JsonPropertyName("steps")] int Steps
);

public sealed record SessionStatsModelUsage(
    [property: JsonPropertyName("model")] ModelRef Model,
    [property: JsonPropertyName("steps")] int Steps,
    [property: JsonPropertyName("tokens")] TokenUsageInfo Tokens,
    [property: JsonPropertyName("cost")] Money Cost
);

public sealed record SessionStatsToolUsage(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("calls")] int Calls,
    [property: JsonPropertyName("succeeded")] int Succeeded,
    [property: JsonPropertyName("failed")] int Failed,
    [property: JsonPropertyName("unfinished")] int Unfinished,
    [property: JsonPropertyName("durationP50")] double? DurationP50 = null
);

public sealed record SessionStatsToolTotals(
    [property: JsonPropertyName("calls")] int Calls,
    [property: JsonPropertyName("succeeded")] int Succeeded,
    [property: JsonPropertyName("failed")] int Failed,
    [property: JsonPropertyName("unfinished")] int Unfinished
);

public sealed record SessionStatsDateRange(
    [property: JsonPropertyName("from")] DateTimeOffset From,
    [property: JsonPropertyName("to")] DateTimeOffset To
);

/// <summary>
/// 1:1 port of SessionStats.Info from packages/schema/src/session-stats.ts
/// </summary>
public sealed record SessionStatsInfo(
    [property: JsonPropertyName("range")] SessionStatsDateRange Range,
    [property: JsonPropertyName("sessions")] int Sessions,
    [property: JsonPropertyName("subagents")] int Subagents,
    [property: JsonPropertyName("prompts")] int Prompts,
    [property: JsonPropertyName("steps")] int Steps,
    [property: JsonPropertyName("tokens")] TokenUsageInfo Tokens,
    [property: JsonPropertyName("cost")] Money Cost,
    [property: JsonPropertyName("activeDays")] int ActiveDays,
    [property: JsonPropertyName("streak")] int Streak,
    [property: JsonPropertyName("activity")] IReadOnlyList<SessionStatsActivity> Activity,
    [property: JsonPropertyName("models")] IReadOnlyList<SessionStatsModelUsage> Models,
    [property: JsonPropertyName("toolTotals")] SessionStatsToolTotals? ToolTotals = null
);
