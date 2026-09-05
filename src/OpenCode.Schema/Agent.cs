namespace OpenCode.Schema;

using System.Text.Json;
using System.Text.Json.Serialization;

[JsonConverter(typeof(AgentIdJsonConverter))]
[Vogen.ValueObject<string>(comparison: Vogen.ComparisonGeneration.Omit)]
public readonly partial struct AgentId
{
    public static AgentId FromExisting(string value) => From(PromptValidation.Required(value));
    public void Deconstruct(out string value) => value = Value;
    public override string ToString() => Value;
    public static implicit operator string(AgentId id) => id.Value;
    public static explicit operator AgentId(string value) => FromExisting(value);
}

public sealed class AgentIdJsonConverter() : ScalarJsonConverter<AgentId, string>(AgentId.FromExisting, static value => value.Value)
{
    public override AgentId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String) throw new JsonException("Expected agent ID string.");
        return AgentId.FromExisting(reader.GetString()!);
    }

    public override void Write(Utf8JsonWriter writer, AgentId value, JsonSerializerOptions options) =>
        writer.WriteStringValue(PromptValidation.Required(value.Value));
}

[JsonConverter(typeof(AgentModeJsonConverter))]
public enum AgentMode
{
    [JsonStringEnumMemberName("subagent")]
    Subagent,
    [JsonStringEnumMemberName("primary")]
    Primary,
    [JsonStringEnumMemberName("all")]
    All
}

/// <summary>
/// Canonical Agent.Info. Runtime request and permissions are required; configuration overrides are separate.
/// </summary>
public sealed record AgentInfo(
    AgentId Id,
    string Name,
    AgentMode Mode,
    [property: JsonPropertyName("hidden"), JsonRequired] bool Hidden,
    IReadOnlyList<PermissionRule> Permissions,
    ProviderRequest Request,
    [property: JsonPropertyName("model"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<ModelRef>))] ModelRef? Model = null,
    [property: JsonPropertyName("system"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string? System = null,
    [property: JsonPropertyName("description"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string? Description = null,
    [property: JsonPropertyName("color"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string? Color = null,
    double? Steps = null
)
{
    [JsonPropertyName("id"), JsonRequired]
    public AgentId Id { get; init => field = ModelProviderContract.Id(value, value.IsInitialized()); } = ModelProviderContract.Id(Id, Id.IsInitialized());
    [JsonPropertyName("name"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string Name { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Name);
    [JsonPropertyName("mode"), JsonRequired]
    public AgentMode Mode { get; init => field = AgentModeJsonConverter.Validate(value); } = AgentModeJsonConverter.Validate(Mode);
    [JsonPropertyName("permissions"), JsonRequired, JsonConverter(typeof(PromptAttachmentListJsonConverter<PermissionRule>))]
    public IReadOnlyList<PermissionRule> Permissions { get; init => field = PromptValidation.Required(PromptValidation.Attachments(value)); } = PromptValidation.Required(PromptValidation.Attachments(Permissions));
    [JsonPropertyName("request"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<ProviderRequest>))]
    public ProviderRequest Request { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Request);
    [JsonPropertyName("steps"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(OptionalPositiveIntegerJsonConverter))]
    public double? Steps { get; init => field = value is null ? null : MessageContract.Integer(value.Value, 1); } = Steps is null ? null : MessageContract.Integer(Steps.Value, 1);

    public static AgentInfo CreateDefault(AgentId id) => new(
        Id: id,
        Name: id.Value,
        Mode: AgentMode.Primary,
        Hidden: false,
        Permissions: [
            new PermissionRule("*", "*", PermissionEffect.Allow),
            new PermissionRule("external_directory", "*", PermissionEffect.Ask),
            new PermissionRule("read", "*.env", PermissionEffect.Ask),
            new PermissionRule("read", "*.env.*", PermissionEffect.Ask),
            new PermissionRule("read", "*.env.example", PermissionEffect.Allow)
        ],
        Request: new ProviderRequest(Headers: new Dictionary<string, string>(), Body: new Dictionary<string, JsonElement>())
    );
}
