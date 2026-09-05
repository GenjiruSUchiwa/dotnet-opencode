namespace OpenCode.Schema;

using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

/// <summary>
/// 1:1 port of Instruction and InstructionEntry from packages/schema/src/instruction.ts and instruction-entry.ts
/// </summary>
public sealed record InstructionEntryInfo(
    [property: JsonPropertyName("key"), JsonRequired] string Key,
    [property: JsonPropertyName("value"), JsonRequired] JsonElement Value
) : IJsonOnSerializing, IJsonOnDeserialized
{
    void IJsonOnSerializing.OnSerializing() => InstructionEntryContract.Validate(Key, Value);
    void IJsonOnDeserialized.OnDeserialized() => InstructionEntryContract.Validate(Key, Value);
}

public sealed record InstructionEntrySnapshot(
    [property: JsonPropertyName("key"), JsonRequired] string Key,
    [property: JsonPropertyName("value"), JsonRequired] JsonElement Value,
    [property: JsonPropertyName("removed"), JsonRequired] bool Removed
) : IJsonOnSerializing, IJsonOnDeserialized
{
    void IJsonOnSerializing.OnSerializing() => InstructionEntryContract.Validate(Key, Value);
    void IJsonOnDeserialized.OnDeserialized() => InstructionEntryContract.Validate(Key, Value);
}

internal static partial class InstructionEntryContract
{
    internal static void Validate(string key, JsonElement value)
    {
        if (key is null || !KeyPattern().IsMatch(key)) throw new JsonException("Invalid instruction entry key.");
        // Source Schema.Json includes null. Only absence/undefined is invalid.
        if (value.ValueKind == JsonValueKind.Undefined) throw new JsonException("Instruction entry requires a JSON value.");
    }
    [GeneratedRegex("^[a-z0-9][a-z0-9._-]*$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex KeyPattern();
}
