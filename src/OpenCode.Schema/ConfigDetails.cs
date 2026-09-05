namespace OpenCode.Schema;

using System.Text.Json;
using System.Text.Json.Serialization;

public sealed record ConfigAgentInfo
{
    [JsonPropertyName("model"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public ConfigModelSelection? Model { get; init; }
    [JsonPropertyName("request"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<ConfigProviderRequest>))] public ConfigProviderRequest? Request { get; init; }
    [JsonPropertyName("system"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<string>))] public string? System { get; init; }
    [JsonPropertyName("description"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<string>))] public string? Description { get; init; }
    [JsonPropertyName("mode"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(OptionalValueJsonConverter<AgentMode>))] public AgentMode? Mode { get; init; }
    [JsonPropertyName("hidden"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(OptionalValueJsonConverter<bool>))] public bool? Hidden { get; init; }
    [JsonPropertyName("color"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(ConfigColorJsonConverter))]
    public string? Color { get; init => field = value is null ? null : ConfigColorJsonConverter.Validate(value); }
    [JsonPropertyName("steps"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(OptionalPositiveIntegerJsonConverter))]
    public double? Steps { get; init => field = value is null ? null : MessageContract.Integer(value.Value, 1); }
    [JsonPropertyName("disabled"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(OptionalValueJsonConverter<bool>))] public bool? Disabled { get; init; }
    [JsonPropertyName("permissions"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(PromptAttachmentListJsonConverter<PermissionRule>))] public IReadOnlyList<PermissionRule>? Permissions { get; init; }
}

public sealed record ConfigCommandInfo(string Template)
{
    [JsonPropertyName("template"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string Template { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Template);
    [JsonPropertyName("description"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<string>))] public string? Description { get; init; }
    [JsonPropertyName("agent"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<string>))] public string? Agent { get; init; }
    [JsonPropertyName("model"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public ConfigModelSelection? Model { get; init; }
    [JsonPropertyName("subtask"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(OptionalValueJsonConverter<bool>))] public bool? Subtask { get; init; }
}

public sealed record ConfigWatcherInfo(
    [property: JsonPropertyName("ignore"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(PromptAttachmentListJsonConverter<string>))] IReadOnlyList<string>? Ignore = null);

public sealed record ConfigCompactionKeep
{
    [JsonPropertyName("tokens"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(OptionalNonNegativeIntegerJsonConverter))]
    public double? Tokens { get; init => field = value is null ? null : MessageContract.Integer(value.Value, 0); }
}

public sealed record ConfigCompactionInfo
{
    [JsonPropertyName("auto"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(OptionalValueJsonConverter<bool>))] public bool? Auto { get; init; }
    [JsonPropertyName("keep"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<ConfigCompactionKeep>))] public ConfigCompactionKeep? Keep { get; init; }
    [JsonPropertyName("buffer"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(OptionalNonNegativeIntegerJsonConverter))]
    public double? Buffer { get; init => field = value is null ? null : MessageContract.Integer(value.Value, 0); }
}

public sealed record ConfigMediaImage
{
    [JsonPropertyName("auto_resize"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(OptionalValueJsonConverter<bool>))] public bool? AutoResize { get; init; }
    [JsonPropertyName("max_width"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(OptionalPositiveIntegerJsonConverter))]
    public double? MaxWidth { get; init => field = value is null ? null : MessageContract.Integer(value.Value, 1); }
    [JsonPropertyName("max_height"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(OptionalPositiveIntegerJsonConverter))]
    public double? MaxHeight { get; init => field = value is null ? null : MessageContract.Integer(value.Value, 1); }
    [JsonPropertyName("max_base64_bytes"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(OptionalPositiveIntegerJsonConverter))]
    public double? MaxBase64Bytes { get; init => field = value is null ? null : MessageContract.Integer(value.Value, 1); }
}

public sealed record ConfigMediaInfo(
    [property: JsonPropertyName("image"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<ConfigMediaImage>))] ConfigMediaImage? Image = null);

public sealed record ConfigPolicyInfo(string Action, string Resource, string Effect)
{
    [JsonPropertyName("action"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string Action { get; init => field = ValidateAction(value); } = ValidateAction(Action);
    [JsonPropertyName("resource"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string Resource { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Resource);
    [JsonPropertyName("effect"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string Effect { get; init => field = ValidateEffect(value); } = ValidateEffect(Effect);
    private static string ValidateAction(string value) => value == "provider.use" ? value : throw new JsonException("Config policy action must be provider.use.");
    private static string ValidateEffect(string value) => value is "allow" or "deny" ? value : throw new JsonException("Config policy effect must be allow or deny.");
}

public sealed record ConfigExperimentalInfo
{
    [JsonPropertyName("portable_shell_scanner"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(OptionalValueJsonConverter<bool>))] public bool? PortableShellScanner { get; init; }
    [JsonPropertyName("subagent_depth"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(OptionalNonNegativeIntegerJsonConverter))]
    public double? SubagentDepth { get; init => field = value is null ? null : MessageContract.Integer(value.Value, 0); }
    [JsonPropertyName("policies"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(PromptAttachmentListJsonConverter<ConfigPolicyInfo>))] public IReadOnlyList<ConfigPolicyInfo>? Policies { get; init; }
}

public sealed record ConfigToolOutputInfo
{
    [JsonPropertyName("max_lines"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(OptionalPositiveIntegerJsonConverter))]
    public double? MaxLines { get; init => field = value is null ? null : MessageContract.Integer(value.Value, 1); }
    [JsonPropertyName("max_bytes"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(OptionalPositiveIntegerJsonConverter))]
    public double? MaxBytes { get; init => field = value is null ? null : MessageContract.Integer(value.Value, 1); }
}
