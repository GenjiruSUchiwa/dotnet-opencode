namespace OpenCode.Schema;

using System.Text.Json.Serialization;

public sealed record TerminalSize(
    [property: JsonPropertyName("cols")] int Cols,
    [property: JsonPropertyName("rows")] int Rows
);

public sealed record TerminalOutputOffsets(
    [property: JsonPropertyName("head")] int Head,
    [property: JsonPropertyName("tail")] int Tail
);

/// <summary>
/// 1:1 port of PersistentPty.Info from packages/schema/src/persistent-pty.ts
/// </summary>
public sealed record PersistentPtyInfo(
    [property: JsonPropertyName("id")] PtyId Id,
    [property: JsonPropertyName("sessionID")] SessionId SessionId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("command")] string Command,
    [property: JsonPropertyName("args")] IReadOnlyList<string> Args,
    [property: JsonPropertyName("cwd")] string Cwd,
    [property: JsonPropertyName("status")] PtyStatus Status,
    [property: JsonPropertyName("pid")] int Pid,
    [property: JsonPropertyName("size")] TerminalSize Size,
    [property: JsonPropertyName("output")] TerminalOutputOffsets Output,
    [property: JsonPropertyName("foregroundProcess")] string? ForegroundProcess = null,
    [property: JsonPropertyName("exitCode")] int? ExitCode = null
);

public sealed record PersistentPtyHandoff(
    [property: JsonPropertyName("directory")] string Directory,
    [property: JsonPropertyName("instanceID")] string InstanceId,
    [property: JsonPropertyName("ticket")] string Ticket,
    [property: JsonPropertyName("expiresAt")] double ExpiresAt
);
