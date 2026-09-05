namespace OpenCode.Schema;

using System.Text.Json.Serialization;

// Payloads declared by protocol/groups/integration.ts. Route IDs/location are not duplicated here.
public sealed record IntegrationWellknownAddPayload(string Url)
{
    [JsonPropertyName("url"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string Url { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Url);
}

public sealed record IntegrationKeyConnectPayload(string Key,
    [property: JsonPropertyName("answer"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(FormAnswerJsonConverter))] FormAnswer? Answer = null,
    [property: JsonPropertyName("label"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string? Label = null)
{
    [JsonPropertyName("key"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string Key { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Key);
}

public sealed record IntegrationOAuthConnectPayload(IntegrationMethodId MethodId,
    [property: JsonPropertyName("answer"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(FormAnswerJsonConverter))] FormAnswer? Answer = null,
    [property: JsonPropertyName("label"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string? Label = null)
{
    [JsonPropertyName("methodID"), JsonRequired]
    public IntegrationMethodId MethodId { get; init => field = ModelProviderContract.Id(value, value.IsInitialized()); } = ModelProviderContract.Id(MethodId, MethodId.IsInitialized());
}

public sealed record IntegrationOAuthCompletePayload(
    [property: JsonPropertyName("code"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string? Code = null);

public sealed record IntegrationCommandConnectPayload(IntegrationMethodId MethodId,
    [property: JsonPropertyName("label"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string? Label = null)
{
    [JsonPropertyName("methodID"), JsonRequired]
    public IntegrationMethodId MethodId { get; init => field = ModelProviderContract.Id(value, value.IsInitialized()); } = ModelProviderContract.Id(MethodId, MethodId.IsInitialized());
}
