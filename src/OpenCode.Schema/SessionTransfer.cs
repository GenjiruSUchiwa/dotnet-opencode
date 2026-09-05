namespace OpenCode.Schema;

using System.Text.Json.Serialization;

/// <summary>
/// 1:1 port of SessionTransfer.Data from packages/schema/src/session-transfer.ts
/// </summary>
public sealed record SessionTransferData(
    [property: JsonPropertyName("info"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<SessionInfo>))] SessionInfo Info,
    [property: JsonPropertyName("messages"), JsonRequired, JsonConverter(typeof(PromptAttachmentListJsonConverter<SessionMessage>))] IReadOnlyList<SessionMessage> Messages
) : IJsonOnSerializing
{
    void IJsonOnSerializing.OnSerializing() => SourceObjectContract.Required(Info, Messages);
}
