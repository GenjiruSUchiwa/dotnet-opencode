namespace OpenCode.Schema;

using System.Text.Json.Serialization;

/// <summary>
/// Current revert wire shape; legacy persisted decoding is a separate boundary.
/// </summary>
public sealed record SessionRevert(
    [property: JsonPropertyName("messageID"), JsonRequired] MessageId MessageId,
    [property: JsonPropertyName("partID"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string? PartId = null,
    [property: JsonPropertyName("snapshot"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(OptionalValueJsonConverter<SnapshotId>))] SnapshotId? Snapshot = null,
    [property: JsonPropertyName("files"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(PromptAttachmentListJsonConverter<FileDiffInfo>))] IReadOnlyList<FileDiffInfo>? Files = null
);
