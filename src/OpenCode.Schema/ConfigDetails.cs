namespace OpenCode.Schema;

using System.Text.Json.Serialization;

public sealed record ConfigAgentInfo(
    [property: JsonPropertyName("model")] string? Model = null,
    [property: JsonPropertyName("system")] string? System = null,
    [property: JsonPropertyName("description")] string? Description = null,
    [property: JsonPropertyName("mode")] AgentMode? Mode = null,
    [property: JsonPropertyName("hidden")] bool? Hidden = null,
    [property: JsonPropertyName("color")] string? Color = null,
    [property: JsonPropertyName("steps")] int? Steps = null,
    [property: JsonPropertyName("disabled")] bool? Disabled = null,
    [property: JsonPropertyName("permissions")] IReadOnlyList<PermissionRule>? Permissions = null
);

public sealed record ConfigWatcherInfo(
    [property: JsonPropertyName("ignore")] IReadOnlyList<string>? Ignore = null
);

public sealed record ConfigFormatterEntry(
    [property: JsonPropertyName("disabled")] bool? Disabled = null,
    [property: JsonPropertyName("command")] IReadOnlyList<string>? Command = null,
    [property: JsonPropertyName("environment")] IReadOnlyDictionary<string, string>? Environment = null,
    [property: JsonPropertyName("extensions")] IReadOnlyList<string>? Extensions = null
);

public sealed record ConfigLspServer(
    [property: JsonPropertyName("command")] IReadOnlyList<string> Command,
    [property: JsonPropertyName("extensions")] IReadOnlyList<string>? Extensions = null,
    [property: JsonPropertyName("disabled")] bool? Disabled = null,
    [property: JsonPropertyName("env")] IReadOnlyDictionary<string, string>? Env = null
);

public sealed record ConfigCompactionKeep(
    [property: JsonPropertyName("tokens")] int? Tokens = null
);

public sealed record ConfigCompactionInfo(
    [property: JsonPropertyName("auto")] bool? Auto = null,
    [property: JsonPropertyName("keep")] ConfigCompactionKeep? Keep = null,
    [property: JsonPropertyName("buffer")] int? Buffer = null
);

public sealed record ConfigPolicyInfo(
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("resource")] string Resource,
    [property: JsonPropertyName("effect")] string Effect
);

public sealed record ConfigExperimentalInfo(
    [property: JsonPropertyName("portable_shell_scanner")] bool? PortableShellScanner = null,
    [property: JsonPropertyName("subagent_depth")] int? SubagentDepth = null,
    [property: JsonPropertyName("policies")] IReadOnlyList<ConfigPolicyInfo>? Policies = null
);

public sealed record ConfigToolOutputInfo(
    [property: JsonPropertyName("max_lines")] int? MaxLines = null,
    [property: JsonPropertyName("max_bytes")] int? MaxBytes = null
);
