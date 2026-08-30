namespace OpenCode.Schema;

using System.Text.Json.Serialization;

public sealed record TokenCacheUsage(
    [property: JsonPropertyName("read")] long Read = 0,
    [property: JsonPropertyName("write")] long Write = 0
);

/// <summary>
/// 1:1 port of TokenUsage.Info from packages/schema/src/token-usage.ts
/// </summary>
public sealed record TokenUsageInfo(
    [property: JsonPropertyName("input")] long Input = 0,
    [property: JsonPropertyName("output")] long Output = 0,
    [property: JsonPropertyName("reasoning")] long Reasoning = 0,
    [property: JsonPropertyName("cache")] TokenCacheUsage Cache = default!
)
{
    public TokenUsageInfo() : this(0, 0, 0, new TokenCacheUsage(0, 0)) { }
}
