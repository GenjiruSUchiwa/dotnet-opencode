namespace OpenCode.Protocol.Groups;

using System.Text.Json.Serialization;
using System.Text.Json;
using OpenCode.Schema;

/// <summary>
/// 1:1 port of ServiceStatus.Health from packages/protocol/src/groups/health.ts
/// </summary>
public sealed record ServiceHealthResponse(
    [property: JsonPropertyName("healthy"), JsonRequired] bool Healthy,
    [property: JsonPropertyName("version"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string Version,
    [property: JsonPropertyName("pid"), JsonRequired, JsonConverter(typeof(NonNegativeIntegerJsonConverter<int>))] int Pid
) : IJsonOnSerializing, IJsonOnDeserialized
{
    private void Validate()
    {
        if (!Healthy || Version is null) throw new JsonException("Health requires healthy:true and version.");
    }
    void IJsonOnSerializing.OnSerializing() => Validate();
    void IJsonOnDeserialized.OnDeserialized() => Validate();
}
