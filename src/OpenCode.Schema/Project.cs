namespace OpenCode.Schema;

using System.Text.Json.Serialization;

public sealed record ProjectTime(
    [property: JsonPropertyName("created")] long Created,
    [property: JsonPropertyName("updated")] long Updated,
    [property: JsonPropertyName("initialized")] long? Initialized = null
);

public sealed record ProjectIcon(
    [property: JsonPropertyName("url")] string? Url = null,
    [property: JsonPropertyName("override")] string? Override = null,
    [property: JsonPropertyName("color")] string? Color = null
);

public sealed record ProjectCommands(
    [property: JsonPropertyName("start")] string? Start = null
);

/// <summary>
/// 1:1 port of Project.Info from packages/schema/src/project.ts
/// </summary>
public sealed record ProjectInfo(
    [property: JsonPropertyName("id")] ProjectId Id,
    [property: JsonPropertyName("canonical")] string Canonical,
    [property: JsonPropertyName("time")] ProjectTime Time,
    [property: JsonPropertyName("sandboxes")] IReadOnlyList<string> Sandboxes,
    [property: JsonPropertyName("vcs")] string? Vcs = null,
    [property: JsonPropertyName("name")] string? Name = null,
    [property: JsonPropertyName("icon")] ProjectIcon? Icon = null,
    [property: JsonPropertyName("commands")] ProjectCommands? Commands = null
);
