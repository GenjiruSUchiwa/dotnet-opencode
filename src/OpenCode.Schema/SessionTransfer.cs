namespace OpenCode.Schema;

using System.Text.Json.Serialization;

/// <summary>
/// 1:1 port of SessionTransfer.Data from packages/schema/src/session-transfer.ts
/// </summary>
public sealed record SessionTransferData(
    [property: JsonPropertyName("info")] SessionInfo Info,
    [property: JsonPropertyName("messages")] IReadOnlyList<SessionMessage> Messages
);
