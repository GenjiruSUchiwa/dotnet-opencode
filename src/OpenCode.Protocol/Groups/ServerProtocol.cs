namespace OpenCode.Protocol.Groups;

using System.Text.Json.Serialization;
using System.Text.Json;
using OpenCode.Schema;

/// <summary>
/// 1:1 port of ServerGroup response from packages/protocol/src/groups/server.ts
/// </summary>
public sealed record ServerInfoResponse(
    [property: JsonPropertyName("urls"), JsonRequired, JsonConverter(typeof(PromptAttachmentListJsonConverter<string>))] IReadOnlyList<string> Urls
) : IJsonOnSerializing
{
    void IJsonOnSerializing.OnSerializing()
    {
        if (Urls is null) throw new JsonException("Server information requires urls.");
    }
}
