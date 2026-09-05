namespace OpenCode.Core.Session;

using OpenCode.Core.Llm;
using OpenCode.Schema;

internal static class SessionUsage
{
    internal static TokenUsageInfo Tokens(LlmUsage? usage) => new(Safe(usage?.NonCachedInputTokens), Safe(usage?.VisibleOutputTokens),
        Safe(usage?.ReasoningTokens), new TokenCacheUsage(Safe(usage?.CacheReadInputTokens), Safe(usage?.CacheWriteInputTokens)));

    internal static Money Cost(IReadOnlyList<CatalogCost> prices, TokenUsageInfo tokens)
    {
        var context = tokens.Input + tokens.Cache.Read + tokens.Cache.Write;
        var price = prices.Where(price => price.Tier?.Type == "context" && context > price.Tier.Size)
            .OrderByDescending(price => price.Tier!.Size).FirstOrDefault() ?? prices.FirstOrDefault(price => price.Tier is null);
        return price is null ? Money.Zero : Money.FromExisting((tokens.Input * Finite(price.Input) +
            (tokens.Output + tokens.Reasoning) * Finite(price.Output) + tokens.Cache.Read * Finite(price.Cache.Read) + tokens.Cache.Write * Finite(price.Cache.Write)) / 1_000_000);
    }

    internal static TokenUsageInfo Add(TokenUsageInfo left, TokenUsageInfo right) => new(left.Input + right.Input, left.Output + right.Output,
        left.Reasoning + right.Reasoning, new TokenCacheUsage(left.Cache.Read + right.Cache.Read, left.Cache.Write + right.Cache.Write));
    private static double Finite(double number) => double.IsFinite(number) ? number : 0;
    private static double Safe(double? number) => Math.Max(0, Finite(number ?? 0));
}
