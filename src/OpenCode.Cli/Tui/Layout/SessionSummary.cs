namespace OpenCode.Cli.Tui.Layout;

using System.Globalization;
using OpenCode.Schema;

public sealed record SessionSummary(double? ContextTokens, double? ContextPercent, double? Cost)
{
    public static SessionSummary Create(IReadOnlyList<SessionMessage> messages, IReadOnlyList<ModelInfo> models, SessionInfo? session,
        IReadOnlyList<SessionMessage>? baseline = null)
    {
        var boundary = session?.Revert?.MessageId;
        var end = boundary is null ? messages.Count : messages.ToList().FindIndex(message => message.Id == boundary);
        if (end < 0) return new(null, null, session?.Cost.Amount);
        var history = messages.Take(end).ToArray();
        var compaction = Array.FindLastIndex(history, message => message is CompactionMessage { Status: "completed" });
        var last = history.Skip(compaction + 1).OfType<AssistantMessage>().LastOrDefault(message => message.Tokens is not null);
        var tokens = last?.Tokens is { } usage ? usage.Input + usage.Output + usage.Reasoning + usage.Cache.Read + usage.Cache.Write : (double?)null;
        var limit = last?.Model is { } selected ? models.FirstOrDefault(model => model.ProviderId.Value == selected.ProviderId && model.Id.Value == selected.Id)?.Limit.Context : null;
        // Hydrated Session cost includes facts outside the visible message window. Never add it to those messages again.
        var visibleCost = messages.OfType<AssistantMessage>().Sum(message => message.Cost?.Amount ?? 0);
        var baselineCost = (baseline ?? []).OfType<AssistantMessage>().Sum(message => message.Cost?.Amount ?? 0);
        var cost = session is null ? visibleCost : session.Cost.Amount + Math.Max(0, visibleCost - baselineCost);
        return new(tokens > 0 ? tokens : null, tokens > 0 && limit > 0 ? Math.Floor(tokens.Value / limit.Value * 100 + 0.5) : null, cost > 0 ? cost : null);
    }

    public string? ContextLabel => ContextTokens is not { } count ? null
        : (count >= 1000 ? (count / 1000).ToString("0.#", CultureInfo.InvariantCulture) + "k" : count.ToString("0", CultureInfo.InvariantCulture))
            + (ContextPercent is { } percent ? $" ({percent:0}%)" : "");
    public string? CostLabel => Cost is { } cost ? "$" + cost.ToString("0.00", CultureInfo.InvariantCulture) : null;
}
