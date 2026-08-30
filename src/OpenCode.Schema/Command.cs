namespace OpenCode.Schema;

using System.Text.Json.Serialization;

/// <summary>
/// 1:1 port of Command.Info from packages/schema/src/command.ts
/// </summary>
public sealed record CommandInfo(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string? Description = null
);
