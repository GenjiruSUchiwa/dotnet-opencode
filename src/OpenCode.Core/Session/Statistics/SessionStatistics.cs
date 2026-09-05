namespace OpenCode.Core.Session.Statistics;

using System.Globalization;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenCode.Core.Persistence;
using OpenCode.Core.Database;
using OpenCode.Schema;

public sealed record SessionStatisticsInput(double? From = null, double? To = null, ProjectId? ProjectId = null,
    string? Timezone = null, SessionStatsToolMode Tools = SessionStatsToolMode.Summary);

/// <summary>Read model port of core/session/stats.ts. No persistence writes or
/// inferred activity; source query failures propagate instead of returning empty data.</summary>
public sealed class SessionStatistics(IDatabase database)
{
    private const double Window = 31d * 24 * 60 * 60 * 1000;

    [SuppressMessage("Design", "MA0015", Justification = "Statistics validation messages and existing inferred field names are exposed by the API and must not gain new parameter suffixes during the persistence migration.")]
    public async Task<SessionStatsInfo> GetAsync(SessionStatisticsInput? input = null, CancellationToken ct = default)
    {
        input ??= new();
        if (input.ProjectId is { } projectId) ArgumentException.ThrowIfNullOrEmpty(projectId.Value);
        var zone = Zone(input.Timezone ?? "UTC");
        var to = input.To ?? database.Clock.GetUtcNow().ToUnixTimeMilliseconds();
        if (!double.IsFinite(to) || input.From is { } number && !double.IsFinite(number))
            throw new ArgumentException("Stats bounds must be finite numbers.");
        if (input.From is { } start && start >= to) throw new ArgumentException("Stats range must end after it starts");
        if (!Enum.IsDefined(input.Tools)) throw new ArgumentException("Unknown stats tools mode.");
        var endTime = Time(to);
        var connection = database.CreateConnection();
        await using var connectionLifetime = connection.ConfigureAwait(true);
        var db = new PersistenceContext(connection);
        await using var dbLifetime = db.ConfigureAwait(true);
        var project = input.ProjectId?.Value;
        var messages = db.Messages.Join(db.Sessions, message => message.session_id, session => session.id,
            (message, session) => new { Message = message, Session = session })
            .Where(row => (row.Message.type == "user" || row.Message.type == "assistant")
                && (row.Session.fork_session_id == null || row.Message.time_created >= row.Session.time_created)
                && (project == null || row.Session.project_id == project));
        var earliest = input.From is null ? await messages.Where(row => SqliteFunctions.Before(row.Message.time_created, to))
            .MinAsync(row => (double?)row.Message.time_created, ct).ConfigureAwait(false) : null;
        var from = input.From ?? earliest ?? to;
        var startTime = Time(from);
        var sessions = new HashSet<string>(StringComparer.Ordinal);
        var subagents = new HashSet<string>(StringComparer.Ordinal);
        var activity = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var models = new Dictionary<string, ModelAggregate>(StringComparer.Ordinal);
        var tools = new Dictionary<string, ToolAggregate>(StringComparer.Ordinal);
        var totals = new Usage();
        var toolTotals = new ToolAggregate("");
        var prompts = 0;
        var steps = 0;
        for (var lower = from; lower < to; lower += Window)
        {
            ct.ThrowIfCancellationRequested();
            var upper = Math.Min(to, lower + Window);
            var rows = messages.Where(row => SqliteFunctions.AtOrAfter(row.Message.time_created, lower) && SqliteFunctions.Before(row.Message.time_created, upper))
                .Select(row => new { row.Message.session_id, row.Session.parent_id, row.Message.type, Created = (double)row.Message.time_created,
                    Provider = SqliteFunctions.JsonText(row.Message.data, "$.model.providerID"), Model = SqliteFunctions.JsonText(row.Message.data, "$.model.id"),
                    Variant = SqliteFunctions.JsonText(row.Message.data, "$.model.variant"), Input = SqliteFunctions.JsonNumber(row.Message.data, "$.tokens.input"),
                    Output = SqliteFunctions.JsonNumber(row.Message.data, "$.tokens.output"), Reasoning = SqliteFunctions.JsonNumber(row.Message.data, "$.tokens.reasoning"),
                    Read = SqliteFunctions.JsonNumber(row.Message.data, "$.tokens.cache.read"), Write = SqliteFunctions.JsonNumber(row.Message.data, "$.tokens.cache.write"),
                    Cost = SqliteFunctions.JsonNumber(row.Message.data, "$.cost") });
            await foreach (var row in rows.ReadAsync(-1, ct).ConfigureAwait(false))
            {
                (row.parent_id is null ? sessions : subagents).Add(row.session_id);
                if (row.type == "user")
                {
                    if (row.parent_id is null) prompts = checked(prompts + 1);
                    continue;
                }
                steps = checked(steps + 1);
                var tokens = new TokenUsageInfo(row.Input ?? 0, row.Output ?? 0, row.Reasoning ?? 0, new(row.Read ?? 0, row.Write ?? 0));
                var cost = row.Cost ?? 0;
                totals.Add(tokens, cost);
                var day = TimeZoneInfo.ConvertTime(Time(row.Created), zone).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                activity[day] = checked(activity.GetValueOrDefault(day) + 1);
                var provider = row.Provider;
                var modelId = row.Model;
                var variant = row.Variant;
                if (string.IsNullOrEmpty(provider) || string.IsNullOrEmpty(modelId)) continue;
                var key = $"{provider}/{modelId}#{variant ?? ""}";
                if (!models.TryGetValue(key, out var model))
                    models.Add(key, model = new(new(provider, modelId, string.IsNullOrEmpty(variant) ? null : variant)));
                model.Steps = checked(model.Steps + 1);
                model.Usage.Add(tokens, cost);
            }
        }

        if (input.Tools != SessionStatsToolMode.None)
            for (var lower = from; lower < to; lower += Window)
            {
                if (input.Tools == SessionStatsToolMode.Summary)
                {
                    foreach (var row in await StatisticsSql.SummaryAsync(db, project, lower, Math.Min(to, lower + Window), ct).ConfigureAwait(false))
                    {
                        toolTotals.Calls = checked(toolTotals.Calls + row.Calls);
                        toolTotals.Succeeded = checked(toolTotals.Succeeded + row.Succeeded);
                        toolTotals.Failed = checked(toolTotals.Failed + row.Failed);
                        toolTotals.Unfinished = checked(toolTotals.Unfinished + row.Unfinished);
                    }
                    continue;
                }
                foreach (var row in await StatisticsSql.DetailAsync(db, project, lower, Math.Min(to, lower + Window), ct).ConfigureAwait(false))
                {
                    var status = row.Status;
                    toolTotals.Add(status);
                    var name = row.Name;
                    if (string.IsNullOrEmpty(name)) continue;
                    if (!tools.TryGetValue(name, out var tool)) tools.Add(name, tool = new(name));
                    tool.Add(status);
                    if (row.Duration is { } duration) tool.Durations.Add(duration);
                }
            }

        // Match source: all sessions in the selected project, not just the sets
        // counted above. Compaction overhead changes totals, not models/activity.
        var ids = await db.Sessions.Where(row => project == null || row.project_id == project).Select(row => row.id).ToListAsync(ct).ConfigureAwait(false);
        var eventType = SessionEventDefinitions.UsageRecorded.Type;
        foreach (var batch in ids.Chunk(500))
        {
            // Intentional source compatibility: stats.ts queries the unversioned
            // definition.Type, although the writer stores version-suffixed types.
            var rows = db.Events.Where(row => batch.Contains(row.aggregate_id) && row.type == eventType
                && SqliteFunctions.JsonText(row.data, "$.source") == "compaction" && row.created >= from && row.created < to)
                .Select(row => row.data);
            await foreach (var row in rows.ReadAsync(-1, ct).ConfigureAwait(false))
            {
                var usage = DecodeUsage(row);
                if (usage is not null) totals.Add(usage.Tokens, usage.Cost.Amount);
            }
        }
        var streak = 0;
        var current = 0;
        DateOnly? previous = null;
        foreach (var day in activity.Keys)
        {
            var date = DateOnly.ParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            current = previous is { } before && date.DayNumber - before.DayNumber == 1 ? current + 1 : 1;
            streak = Math.Max(streak, current);
            previous = date;
        }
        SessionStatsTools toolResult = input.Tools switch
        {
            SessionStatsToolMode.None => new SessionStatsToolsNone(),
            SessionStatsToolMode.Summary => new SessionStatsToolsSummary(toolTotals.Totals()),
            _ => new SessionStatsToolsDetail(toolTotals.Totals(), tools.Values.OrderByDescending(tool => tool.Calls).Select(tool => tool.Result()).ToArray())
        };
        return new(new(startTime, endTime), sessions.Count, subagents.Count, prompts, steps, totals.Tokens, Money.FromExisting(totals.Cost),
            activity.Count, streak, activity.Select(day => new SessionStatsActivity(day.Key, day.Value)).ToArray(),
            models.Values.OrderByDescending(model => model.Usage.TotalTokens).Select(model => new SessionStatsModelUsage(model.Model,
                model.Steps, model.Usage.Tokens, Money.FromExisting(model.Usage.Cost))).ToArray(), toolResult);
    }

    [SuppressMessage("Design", "MA0015", Justification = "Preserve the existing source-compatible calendar-range error text.")]
    private static DateTimeOffset Time(double value)
    {
        if (!double.IsFinite(value) || value < -62135596800000d || value >= 253402300800000d)
            throw new ArgumentException("Stats timestamps exceed the supported .NET calendar range (years 1–9999).");
        return DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Truncate(value));
    }
    [SuppressMessage("Design", "MA0015", Justification = "Preserve timezone validation messages and exception causes rather than appending a new parameter suffix.")]
    private static TimeZoneInfo Zone(string id)
    {
        if (id != "UTC" && !TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out _)) throw new ArgumentException($"Invalid time zone: {id}");
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (TimeZoneNotFoundException error) { throw new ArgumentException($"Invalid time zone: {id}", error); }
        catch (InvalidTimeZoneException error) { throw new ArgumentException($"Invalid time zone: {id}", error); }
    }
    private static SessionUsageRecordedEventData? DecodeUsage(string json)
    {
        try { return JsonSerializer.Deserialize(json, OpenCodeJsonContext.Default.SessionUsageRecordedEventData); }
        catch (JsonException) { return null; }
        catch (ArgumentException) { return null; }
    }
    private sealed class Usage
    {
        public TokenUsageInfo Tokens { get; private set; } = new();
        public double Cost { get; private set; }
        public double TotalTokens => Tokens.Input + Tokens.Output + Tokens.Reasoning + Tokens.Cache.Read + Tokens.Cache.Write;
        public void Add(TokenUsageInfo value, double cost)
        {
            Tokens = new(Tokens.Input + value.Input, Tokens.Output + value.Output, Tokens.Reasoning + value.Reasoning,
                new(Tokens.Cache.Read + value.Cache.Read, Tokens.Cache.Write + value.Cache.Write));
            Cost += cost;
        }
    }
    private sealed class ModelAggregate(ModelRef model)
    {
        public ModelRef Model { get; } = model;
        public int Steps { get; set; }
        public Usage Usage { get; } = new();
    }
    private sealed class ToolAggregate(string name)
    {
        public int Calls, Succeeded, Failed, Unfinished;
        public List<double> Durations { get; } = [];
        public void Add(string? status)
        {
            Calls = checked(Calls + 1);
            if (status == "completed") Succeeded = checked(Succeeded + 1);
            else if (status == "error") Failed = checked(Failed + 1);
            else Unfinished = checked(Unfinished + 1);
        }
        public SessionStatsToolTotals Totals() => new(Calls, Succeeded, Failed, Unfinished);
        public SessionStatsToolUsage Result()
        {
            Durations.Sort();
            var middle = Durations.Count / 2;
            double? median = Durations.Count == 0 ? null : Durations.Count % 2 == 0
                ? (Durations[middle - 1] + Durations[middle]) / 2 : Durations[middle];
            return new(name, Calls, Succeeded, Failed, Unfinished, median);
        }
    }

}
