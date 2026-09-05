namespace OpenCode.Protocol.Groups;

using System.Text.Json.Serialization;
using OpenCode.Schema;

public sealed record SessionStatsQuery(double? From = null, double? To = null, ProjectId? Project = null,
    string? Timezone = null, SessionStatsToolMode? Tools = null)
{
    public void Validate()
    {
        if ((From is { } from && !double.IsFinite(from)) || (To is { } to && !double.IsFinite(to)))
            throw new ArgumentException("Stats bounds must be finite numbers.");
        if (From is { } start && To is { } end && start >= end)
            throw new ArgumentException("Stats range must end after it starts");
        if (Tools is { } tools && !Enum.IsDefined(tools)) throw new ArgumentException("Unknown stats tools mode.");
        if (Project is { } project) ArgumentException.ThrowIfNullOrEmpty(project.Value);
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true, AllowOutOfOrderMetadataProperties = true)]
[JsonSerializable(typeof(ApiResult<SessionStatsInfo>), TypeInfoPropertyName = "StatsResult")]
public partial class SessionStatsProtocolJsonContext : JsonSerializerContext;
