namespace OpenCode.Protocol.Groups;

using System.Text.Json.Serialization;
using OpenCode.Schema;

/// <summary>
/// 1:1 port of PtyGroup from packages/protocol/src/groups/pty.ts
/// </summary>
public sealed record PtyCreateRequest(
    [property: JsonPropertyName("command")] string? Command = null,
    [property: JsonPropertyName("args")] IReadOnlyList<string>? Args = null,
    [property: JsonPropertyName("cwd")] string? Cwd = null,
    [property: JsonPropertyName("title")] string? Title = null,
    [property: JsonPropertyName("env")] IReadOnlyDictionary<string, string>? Env = null
);
