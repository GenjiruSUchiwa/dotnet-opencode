namespace OpenCode.Schema;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// 1:1 port of Instruction and InstructionEntry from packages/schema/src/instruction.ts and instruction-entry.ts
/// </summary>
public sealed record InstructionEntryInfo(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("value")] JsonElement Value
);

public sealed record InstructionEntrySnapshot(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("value")] JsonElement Value,
    [property: JsonPropertyName("removed")] bool Removed
);
