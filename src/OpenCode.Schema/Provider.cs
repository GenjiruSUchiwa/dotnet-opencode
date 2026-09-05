namespace OpenCode.Schema;

using System.Text.Json;
using System.Text.Json.Serialization;

[JsonConverter(typeof(ProviderIdJsonConverter))]
[Vogen.ValueObject<string>(comparison: Vogen.ComparisonGeneration.Omit)]
public readonly partial struct ProviderId
{
    public static ProviderId FromExisting(string value) => From(PromptValidation.Required(value));
    public void Deconstruct(out string value) => value = Value;
    public override string ToString() => Value;
    public static implicit operator string(ProviderId id) => id.Value;
    public static explicit operator ProviderId(string value) => FromExisting(value);

    public static readonly ProviderId OpenCode = new("opencode");
    public static readonly ProviderId Anthropic = new("anthropic");
    public static readonly ProviderId OpenAI = new("openai");
    public static readonly ProviderId Google = new("google");
    public static readonly ProviderId GoogleVertex = new("google-vertex");
    public static readonly ProviderId GitHubCopilot = new("github-copilot");
    public static readonly ProviderId AmazonBedrock = new("amazon-bedrock");
    public static readonly ProviderId Azure = new("azure");
    public static readonly ProviderId OpenRouter = new("openrouter");
    public static readonly ProviderId Mistral = new("mistral");
    public static readonly ProviderId GitLab = new("gitlab");
}

public sealed class ProviderIdJsonConverter() : ScalarJsonConverter<ProviderId, string>(ProviderId.FromExisting, static value => value.Value)
{
    public override ProviderId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String) throw new JsonException("Expected provider ID string.");
        return ProviderId.FromExisting(reader.GetString()!);
    }
    public override void Write(Utf8JsonWriter writer, ProviderId value, JsonSerializerOptions options) =>
        writer.WriteStringValue(PromptValidation.Required(value.Value));
}

[JsonConverter(typeof(ProviderActivationJsonConverter))]
public enum ProviderActivation
{
    [JsonStringEnumMemberName("auto")] Auto,
    [JsonStringEnumMemberName("enabled")] Enabled,
    [JsonStringEnumMemberName("disabled")] Disabled
}

/// <summary>Provider.Request: settings has a construction default; all three maps are required JSON fields.</summary>
[method: JsonConstructor]
public sealed record ProviderRequest(
    IReadOnlyDictionary<string, JsonElement> Settings,
    IReadOnlyDictionary<string, string> Headers,
    IReadOnlyDictionary<string, JsonElement> Body
)
{
    [JsonPropertyName("settings"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<IReadOnlyDictionary<string, JsonElement>>))]
    public IReadOnlyDictionary<string, JsonElement> Settings { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Settings);
    [JsonPropertyName("headers"), JsonRequired, JsonConverter(typeof(ProviderHeadersJsonConverter))]
    public IReadOnlyDictionary<string, string> Headers { get; init => field = ProviderHeadersJsonConverter.Validate(value); } = ProviderHeadersJsonConverter.Validate(Headers);
    [JsonPropertyName("body"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<IReadOnlyDictionary<string, JsonElement>>))]
    public IReadOnlyDictionary<string, JsonElement> Body { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Body);

    public ProviderRequest(IReadOnlyDictionary<string, string> Headers, IReadOnlyDictionary<string, JsonElement> Body)
        : this(new Dictionary<string, JsonElement>(), Headers, Body) { }
}

public sealed record ProviderInfo(
    ProviderId Id,
    string Name,
    ProviderActivation Activation,
    string Package,
    [property: JsonPropertyName("integrationID"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(OptionalValueJsonConverter<IntegrationId>))] IntegrationId? IntegrationId = null,
    [property: JsonPropertyName("settings"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<IReadOnlyDictionary<string, JsonElement>>))] IReadOnlyDictionary<string, JsonElement>? Settings = null,
    [property: JsonPropertyName("headers"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(ProviderHeadersJsonConverter))] IReadOnlyDictionary<string, string>? Headers = null,
    [property: JsonPropertyName("body"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<IReadOnlyDictionary<string, JsonElement>>))] IReadOnlyDictionary<string, JsonElement>? Body = null
)
{
    [JsonPropertyName("id"), JsonRequired]
    public ProviderId Id { get; init => field = ModelProviderContract.Id(value, value.IsInitialized()); } = ModelProviderContract.Id(Id, Id.IsInitialized());
    [JsonPropertyName("name"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string Name { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Name);
    [JsonPropertyName("activation"), JsonRequired]
    public ProviderActivation Activation { get; init => field = ProviderActivationJsonConverter.Validate(value); } = ProviderActivationJsonConverter.Validate(Activation);
    [JsonPropertyName("package"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string Package { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Package);

    public static ProviderInfo Empty(ProviderId id) => new(id, PromptValidation.Required(id.Value), ProviderActivation.Auto, "");
}
