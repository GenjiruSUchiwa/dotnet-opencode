namespace OpenCode.Schema;

using System.Text.Json;
using System.Text.Json.Serialization;

public sealed record McpTimeoutConfig(double? Startup = null, double? Catalog = null, double? Execution = null)
{
    [JsonPropertyName("startup"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(OptionalPositiveIntegerJsonConverter))]
    public double? Startup { get; init => field = value is null ? null : MessageContract.Integer(value.Value, 1); } = Startup is null ? null : MessageContract.Integer(Startup.Value, 1);
    [JsonPropertyName("catalog"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(OptionalPositiveIntegerJsonConverter))]
    public double? Catalog { get; init => field = value is null ? null : MessageContract.Integer(value.Value, 1); } = Catalog is null ? null : MessageContract.Integer(Catalog.Value, 1);
    [JsonPropertyName("execution"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(OptionalPositiveIntegerJsonConverter))]
    public double? Execution { get; init => field = value is null ? null : MessageContract.Integer(value.Value, 1); } = Execution is null ? null : MessageContract.Integer(Execution.Value, 1);
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(McpLocalConfig), "local")]
[JsonDerivedType(typeof(McpRemoteConfig), "remote")]
public abstract record McpServerConfig;

public sealed record McpLocalConfig(
    IReadOnlyList<string> Command,
    [property: JsonPropertyName("cwd"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string? Cwd = null,
    [property: JsonPropertyName("environment"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(ProviderHeadersJsonConverter))] IReadOnlyDictionary<string, string>? Environment = null,
    [property: JsonPropertyName("disabled"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(OptionalValueJsonConverter<bool>))] bool? Disabled = null,
    [property: JsonPropertyName("codemode"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(OptionalValueJsonConverter<bool>))] bool? CodeMode = null,
    [property: JsonPropertyName("timeout"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<McpTimeoutConfig>))] McpTimeoutConfig? Timeout = null
) : McpServerConfig
{
    [JsonPropertyName("command"), JsonRequired, JsonConverter(typeof(PromptAttachmentListJsonConverter<string>))]
    public IReadOnlyList<string> Command { get; init => field = PromptValidation.Required(PromptValidation.Attachments(value)); } = PromptValidation.Required(PromptValidation.Attachments(Command));
}

[JsonConverter(typeof(McpOAuthSettingJsonConverter))]
public abstract record McpOAuthSetting
{
    private protected McpOAuthSetting() { }
}

public sealed record McpOAuthDisabled : McpOAuthSetting
{
    public static McpOAuthDisabled Instance { get; } = new();
}

public sealed record McpOAuthConfig(
    [property: JsonPropertyName("client_id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string? ClientId = null,
    [property: JsonPropertyName("client_secret"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string? ClientSecret = null,
    [property: JsonPropertyName("scope"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string? Scope = null,
    double? CallbackPort = null,
    [property: JsonPropertyName("redirect_uri"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string? RedirectUri = null
) : McpOAuthSetting
{
    [JsonPropertyName("callback_port"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(McpCallbackPortJsonConverter))]
    public double? CallbackPort { get; init => field = value is null ? null : McpCallbackPortJsonConverter.Validate(value.Value); } = CallbackPort is null ? null : McpCallbackPortJsonConverter.Validate(CallbackPort.Value);
}

public sealed record McpRemoteConfig(
    string Url,
    [property: JsonPropertyName("headers"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(ProviderHeadersJsonConverter))] IReadOnlyDictionary<string, string>? Headers = null,
    [property: JsonPropertyName("disabled"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(OptionalValueJsonConverter<bool>))] bool? Disabled = null,
    [property: JsonPropertyName("codemode"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(OptionalValueJsonConverter<bool>))] bool? CodeMode = null,
    [property: JsonPropertyName("timeout"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<McpTimeoutConfig>))] McpTimeoutConfig? Timeout = null,
    [property: JsonPropertyName("oauth"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(McpOAuthSettingJsonConverter))] McpOAuthSetting? OAuth = null
) : McpServerConfig
{
    [JsonPropertyName("url"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string Url { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Url);
}

/// <summary>Config.MCP.Info reuses canonical MCP config types rather than duplicating them.</summary>
public sealed record McpConfiguration(
    [property: JsonPropertyName("timeout"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<McpTimeoutConfig>))] McpTimeoutConfig? Timeout = null,
    [property: JsonPropertyName("servers"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(McpServersJsonConverter))] IReadOnlyDictionary<string, McpServerConfig>? Servers = null
);

/// <summary>Protocol mcp.add wraps its union in a required config property.</summary>
public sealed record McpAddPayload(McpServerConfig Config)
{
    [JsonPropertyName("config"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<McpServerConfig>))]
    public McpServerConfig Config { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Config);
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "status")]
[JsonDerivedType(typeof(McpConnectedStatus), "connected")]
[JsonDerivedType(typeof(McpPendingStatus), "pending")]
[JsonDerivedType(typeof(McpDisabledStatus), "disabled")]
[JsonDerivedType(typeof(McpFailedStatus), "failed")]
[JsonDerivedType(typeof(McpNeedsAuthStatus), "needs_auth")]
public abstract record McpStatus;
public sealed record McpConnectedStatus : McpStatus;
public sealed record McpPendingStatus : McpStatus;
public sealed record McpDisabledStatus : McpStatus;
public sealed record McpNeedsAuthStatus : McpStatus;
public sealed record McpFailedStatus(string Error) : McpStatus
{
    [JsonPropertyName("error"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string Error { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Error);
}

public sealed record McpServer(string Name, McpStatus Status,
    [property: JsonPropertyName("integrationID"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(OptionalValueJsonConverter<IntegrationId>))] IntegrationId? IntegrationId = null)
{
    [JsonPropertyName("name"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string Name { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Name);
    [JsonPropertyName("status"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<McpStatus>))]
    public McpStatus Status { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Status);
}

public sealed record McpResource(
    string Server, string Name, string Uri,
    [property: JsonPropertyName("description"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string? Description = null,
    [property: JsonPropertyName("mimeType"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string? MimeType = null)
{
    [JsonPropertyName("server"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string Server { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Server);
    [JsonPropertyName("name"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string Name { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Name);
    [JsonPropertyName("uri"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string Uri { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Uri);
}

public sealed record McpResourceTemplate(
    string Server, string Name, string UriTemplate,
    [property: JsonPropertyName("description"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string? Description = null,
    [property: JsonPropertyName("mimeType"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string? MimeType = null)
{
    [JsonPropertyName("server"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string Server { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Server);
    [JsonPropertyName("name"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string Name { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Name);
    [JsonPropertyName("uriTemplate"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string UriTemplate { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(UriTemplate);
}

public sealed record McpResourceCatalog(IReadOnlyList<McpResource> Resources, IReadOnlyList<McpResourceTemplate> Templates)
{
    [JsonPropertyName("resources"), JsonRequired, JsonConverter(typeof(PromptAttachmentListJsonConverter<McpResource>))]
    public IReadOnlyList<McpResource> Resources { get; init => field = PromptValidation.Required(PromptValidation.Attachments(value)); } = PromptValidation.Required(PromptValidation.Attachments(Resources));
    [JsonPropertyName("templates"), JsonRequired, JsonConverter(typeof(PromptAttachmentListJsonConverter<McpResourceTemplate>))]
    public IReadOnlyList<McpResourceTemplate> Templates { get; init => field = PromptValidation.Required(PromptValidation.Attachments(value)); } = PromptValidation.Required(PromptValidation.Attachments(Templates));
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(McpResourceTextPart), "text")]
[JsonDerivedType(typeof(McpResourceBlobPart), "blob")]
public abstract record McpResourceContentPart(string Uri,
    [property: JsonPropertyName("mimeType"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string? MimeType = null)
{
    [JsonPropertyName("uri"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string Uri { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Uri);
}

public sealed record McpResourceTextPart(string Uri, string Text, string? MimeType = null) : McpResourceContentPart(Uri, MimeType)
{
    [JsonPropertyName("text"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string Text { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Text);
}

public sealed record McpResourceBlobPart(string Uri, string Blob, string? MimeType = null) : McpResourceContentPart(Uri, MimeType)
{
    [JsonPropertyName("blob"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string Blob { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Blob);
}

public sealed record McpResourceContent(string Server, string Uri, IReadOnlyList<McpResourceContentPart> Contents)
{
    [JsonPropertyName("server"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string Server { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Server);
    [JsonPropertyName("uri"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string Uri { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Uri);
    [JsonPropertyName("contents"), JsonRequired, JsonConverter(typeof(PromptAttachmentListJsonConverter<McpResourceContentPart>))]
    public IReadOnlyList<McpResourceContentPart> Contents { get; init => field = PromptValidation.Required(PromptValidation.Attachments(value)); } = PromptValidation.Required(PromptValidation.Attachments(Contents));
}
