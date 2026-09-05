namespace OpenCode.Schema;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>Canonical current Config.Info; loading, legacy migration, and runtime defaults are separate.</summary>
public sealed record OpenCodeConfiguration
{
    [JsonPropertyName("$schema"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<string>))] public string? Schema { get; init; }
    [JsonPropertyName("shell"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<string>))] public string? Shell { get; init; }
    [JsonPropertyName("model"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public ConfigModelSelection? Model { get; init; }
    [JsonPropertyName("default_agent"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<string>))] public string? DefaultAgent { get; init; }
    [JsonPropertyName("autoupdate"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public ConfigAutoUpdate? Autoupdate { get; init; }
    [JsonPropertyName("share"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string? Share { get; init => field = value is null or "manual" or "auto" or "disabled" ? value : throw new JsonException("Unknown share mode."); }
    [JsonPropertyName("enterprise"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<ConfigEnterprise>))] public ConfigEnterprise? Enterprise { get; init; }
    [JsonPropertyName("username"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<string>))] public string? Username { get; init; }
    [JsonPropertyName("permissions"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(PromptAttachmentListJsonConverter<PermissionRule>))] public IReadOnlyList<PermissionRule>? Permissions { get; init; }
    [JsonPropertyName("agents"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(ConfigRecordJsonConverter<ConfigAgentInfo>))] public IReadOnlyDictionary<string, ConfigAgentInfo>? Agents { get; init; }
    [JsonPropertyName("snapshots"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(OptionalValueJsonConverter<bool>))] public bool? Snapshots { get; init; }
    [JsonPropertyName("watcher"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<ConfigWatcherInfo>))] public ConfigWatcherInfo? Watcher { get; init; }
    [JsonPropertyName("formatter"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public ConfigFormatterInfo? Formatter { get; init; }
    [JsonPropertyName("lsp"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public ConfigLspInfo? Lsp { get; init; }
    [JsonPropertyName("media"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<ConfigMediaInfo>))] public ConfigMediaInfo? Media { get; init; }
    [JsonPropertyName("tool_output"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<ConfigToolOutputInfo>))] public ConfigToolOutputInfo? ToolOutput { get; init; }
    [JsonPropertyName("mcp"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<McpConfiguration>))] public McpConfiguration? Mcp { get; init; }
    [JsonPropertyName("compaction"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<ConfigCompactionInfo>))] public ConfigCompactionInfo? Compaction { get; init; }
    [JsonPropertyName("skills"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(PromptAttachmentListJsonConverter<string>))] public IReadOnlyList<string>? Skills { get; init; }
    [JsonPropertyName("commands"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(ConfigRecordJsonConverter<ConfigCommandInfo>))] public IReadOnlyDictionary<string, ConfigCommandInfo>? Commands { get; init; }
    [JsonPropertyName("instructions"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(PromptAttachmentListJsonConverter<string>))] public IReadOnlyList<string>? Instructions { get; init; }
    [JsonPropertyName("references"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(ConfigRecordJsonConverter<ConfigReferenceEntry>))] public IReadOnlyDictionary<string, ConfigReferenceEntry>? References { get; init; }
    [JsonPropertyName("websearch"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public ConfigWebSearchSelection? WebSearch { get; init; }
    [JsonPropertyName("plugins"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(PromptAttachmentListJsonConverter<ConfigPlugin>))] public IReadOnlyList<ConfigPlugin>? Plugins { get; init; }
    [JsonPropertyName("warming"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public ConfigWarming? Warming { get; init; }
    [JsonPropertyName("providers"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(ConfigRecordJsonConverter<ConfigProviderInfo>))] public IReadOnlyDictionary<string, ConfigProviderInfo>? Providers { get; init; }
    [JsonPropertyName("experimental"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<ConfigExperimentalInfo>))] public ConfigExperimentalInfo? Experimental { get; init; }
}

public sealed record ConfigEnterprise(
    [property: JsonPropertyName("url"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string? Url = null);

[JsonConverter(typeof(ConfigAutoUpdateJsonConverter))]
public abstract record ConfigAutoUpdate
{
    private protected ConfigAutoUpdate() { }
    public sealed record Toggle(bool Value) : ConfigAutoUpdate;
    public sealed record Notify : ConfigAutoUpdate;
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ConfigDocument), "document")]
[JsonDerivedType(typeof(ConfigDirectory), "directory")]
[JsonDerivedType(typeof(ConfigAgentsDirectory), "agents")]
[JsonDerivedType(typeof(ConfigClaudeDirectory), "claude")]
public abstract record ConfigEntry;

public sealed record ConfigDocument(OpenCodeConfiguration Info,
    [property: JsonPropertyName("path"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string? Path = null) : ConfigEntry
{
    [JsonPropertyName("info"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<OpenCodeConfiguration>))]
    public OpenCodeConfiguration Info { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Info);
}
public abstract record ConfigPathEntry(string Path) : ConfigEntry
{
    [JsonPropertyName("path"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string Path { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Path);
}
public sealed record ConfigDirectory(string Path) : ConfigPathEntry(Path);
public sealed record ConfigAgentsDirectory(string Path) : ConfigPathEntry(Path);
public sealed record ConfigClaudeDirectory(string Path) : ConfigPathEntry(Path);
