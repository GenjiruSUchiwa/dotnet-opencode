namespace OpenCode.Schema;

using System.Text.Json.Serialization;

public sealed record McpTimeoutConfig(
    [property: JsonPropertyName("startup")] int? Startup = null,
    [property: JsonPropertyName("catalog")] int? Catalog = null,
    [property: JsonPropertyName("execution")] int? Execution = null
);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(McpLocalConfig), "local")]
[JsonDerivedType(typeof(McpRemoteConfig), "remote")]
public abstract record McpServerConfig;

public sealed record McpLocalConfig(
    [property: JsonPropertyName("command")] IReadOnlyList<string> Command,
    [property: JsonPropertyName("cwd")] string? Cwd = null,
    [property: JsonPropertyName("environment")] IReadOnlyDictionary<string, string>? Environment = null,
    [property: JsonPropertyName("disabled")] bool? Disabled = null,
    [property: JsonPropertyName("codemode")] bool? CodeMode = null,
    [property: JsonPropertyName("timeout")] McpTimeoutConfig? Timeout = null
) : McpServerConfig;

public sealed record McpRemoteConfig(
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("headers")] IReadOnlyDictionary<string, string>? Headers = null,
    [property: JsonPropertyName("disabled")] bool? Disabled = null,
    [property: JsonPropertyName("codemode")] bool? CodeMode = null,
    [property: JsonPropertyName("timeout")] McpTimeoutConfig? Timeout = null
) : McpServerConfig;
