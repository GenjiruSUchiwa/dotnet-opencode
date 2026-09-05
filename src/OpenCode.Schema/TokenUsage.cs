namespace OpenCode.Schema;

using System.Text.Json.Serialization;

[JsonConverter(typeof(TokenCacheUsageJsonConverter))]
public sealed record TokenCacheUsage(
    double Read = 0,
    double Write = 0
)
{
    [JsonPropertyName("read")]
    public double Read { get; init => field = FiniteNumberJsonConverter.Validate(value); } = FiniteNumberJsonConverter.Validate(Read);
    [JsonPropertyName("write")]
    public double Write { get; init => field = FiniteNumberJsonConverter.Validate(value); } = FiniteNumberJsonConverter.Validate(Write);
}

/// <summary>
/// Finite token counts; wire input requires every field, including cache.
/// </summary>
[JsonConverter(typeof(TokenUsageInfoJsonConverter))]
public sealed record TokenUsageInfo(
    double Input = 0,
    double Output = 0,
    double Reasoning = 0,
    TokenCacheUsage Cache = default!
)
{
    [JsonPropertyName("input")]
    public double Input { get; init => field = FiniteNumberJsonConverter.Validate(value); } = FiniteNumberJsonConverter.Validate(Input);
    [JsonPropertyName("output")]
    public double Output { get; init => field = FiniteNumberJsonConverter.Validate(value); } = FiniteNumberJsonConverter.Validate(Output);
    [JsonPropertyName("reasoning")]
    public double Reasoning { get; init => field = FiniteNumberJsonConverter.Validate(value); } = FiniteNumberJsonConverter.Validate(Reasoning);
    [JsonPropertyName("cache")]
    public TokenCacheUsage Cache { get; init => field = value ?? throw new ArgumentNullException(nameof(value)); } = Cache ?? new TokenCacheUsage();

    public TokenUsageInfo() : this(0, 0, 0, new TokenCacheUsage(0, 0)) { }
}
