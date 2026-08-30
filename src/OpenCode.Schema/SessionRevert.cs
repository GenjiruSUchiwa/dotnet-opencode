namespace OpenCode.Schema;

using System.Text.Json.Serialization;

/// <summary>
/// 1:1 port of Session.Revert from packages/schema/src/session-revert.ts
/// </summary>
public sealed record SessionRevert(
    [property: JsonPropertyName("messageID")] MessageId MessageId,
    [property: JsonPropertyName("partID")] string? PartId = null,
    [property: JsonPropertyName("snapshot")] SnapshotId? Snapshot = null,
    [property: JsonPropertyName("files")] IReadOnlyList<FileDiffInfo>? Files = null
);
