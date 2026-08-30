namespace OpenCode.Protocol.Groups;

using System.Text.Json.Serialization;

/// <summary>
/// 1:1 port of ServiceStatus.Health from packages/protocol/src/groups/health.ts
/// </summary>
public sealed record ServiceHealthResponse(
    [property: JsonPropertyName("healthy")] bool Healthy,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("pid")] int Pid
);
