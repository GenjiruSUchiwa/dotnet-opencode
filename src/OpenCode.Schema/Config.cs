namespace OpenCode.Schema;

using System.Text.Json.Serialization;

/// <summary>
/// 1:1 port of Config.Info from packages/schema/src/config.ts
/// </summary>
public sealed record OpenCodeConfiguration(
    [property: JsonPropertyName("$schema")] string? Schema = null,
    [property: JsonPropertyName("shell")] string? Shell = null,
    [property: JsonPropertyName("model")] string? Model = null,
    [property: JsonPropertyName("default_agent")] string? DefaultAgent = null,
    [property: JsonPropertyName("autoupdate")] object? Autoupdate = null,
    [property: JsonPropertyName("share")] string? Share = null,
    [property: JsonPropertyName("username")] string? Username = null,
    [property: JsonPropertyName("snapshots")] bool? Snapshots = null,
    [property: JsonPropertyName("permissions")] IReadOnlyList<PermissionRule>? Permissions = null,
    [property: JsonPropertyName("providers")] IReadOnlyDictionary<string, ProviderConfig>? Providers = null,
    [property: JsonPropertyName("provider")] IReadOnlyDictionary<string, ProviderConfig>? Provider = null,
    [property: JsonPropertyName("mcp")] IReadOnlyDictionary<string, McpServerConfig>? Mcp = null,
    [property: JsonPropertyName("instructions")] IReadOnlyList<string>? Instructions = null
);
