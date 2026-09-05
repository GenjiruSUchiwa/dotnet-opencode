namespace OpenCode.Schema;

using System.Text.Json;
using System.Text.Json.Serialization;

// Canonical config/provider.ts contracts, separate from the provider-owned loader's ConfigModels.cs.
public sealed record ConfigProviderRequest
{
    [JsonPropertyName("headers"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(ProviderHeadersJsonConverter))] public IReadOnlyDictionary<string, string>? Headers { get; init; }
    [JsonPropertyName("body"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<IReadOnlyDictionary<string, JsonElement>>))] public IReadOnlyDictionary<string, JsonElement>? Body { get; init; }
}

public abstract record ConfigProviderOverlays
{
    [JsonPropertyName("settings"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<IReadOnlyDictionary<string, JsonElement>>))] public IReadOnlyDictionary<string, JsonElement>? Settings { get; init; }
    [JsonPropertyName("headers"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(ProviderHeadersJsonConverter))] public IReadOnlyDictionary<string, string>? Headers { get; init; }
    [JsonPropertyName("body"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<IReadOnlyDictionary<string, JsonElement>>))] public IReadOnlyDictionary<string, JsonElement>? Body { get; init; }
}

public sealed record ConfigModelCacheCost
{
    [JsonPropertyName("read"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(OptionalValueJsonConverter<MoneyPerMillionTokens>))] public MoneyPerMillionTokens? Read { get; init; }
    [JsonPropertyName("write"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(OptionalValueJsonConverter<MoneyPerMillionTokens>))] public MoneyPerMillionTokens? Write { get; init; }
}

public sealed record ConfigModelCost(
    [property: JsonPropertyName("input"), JsonRequired] MoneyPerMillionTokens Input,
    [property: JsonPropertyName("output"), JsonRequired] MoneyPerMillionTokens Output)
{
    [JsonPropertyName("tier"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<ModelCostTier>))] public ModelCostTier? Tier { get; init; }
    [JsonPropertyName("cache"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<ConfigModelCacheCost>))] public ConfigModelCacheCost? Cache { get; init; }
}

[JsonConverter(typeof(ConfigModelCostsJsonConverter))]
public abstract record ConfigModelCosts
{
    private protected ConfigModelCosts() { }
    public sealed record Single(ConfigModelCost Value) : ConfigModelCosts
    {
        public ConfigModelCost Value { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Value);
    }
    public sealed record Multiple(IReadOnlyList<ConfigModelCost> Values) : ConfigModelCosts
    {
        public IReadOnlyList<ConfigModelCost> Values { get; init => field = PromptValidation.Required(PromptValidation.Attachments(value)); } = PromptValidation.Required(PromptValidation.Attachments(Values));
    }
}

public sealed record ConfigModelLimit
{
    [JsonPropertyName("context"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(OptionalIntegerNumberJsonConverter))]
    public double? Context { get; init => field = value is null ? null : ModelProviderContract.Integer(value.Value); }
    [JsonPropertyName("input"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(OptionalIntegerNumberJsonConverter))]
    public double? Input { get; init => field = value is null ? null : ModelProviderContract.Integer(value.Value); }
    [JsonPropertyName("output"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(OptionalIntegerNumberJsonConverter))]
    public double? Output { get; init => field = value is null ? null : ModelProviderContract.Integer(value.Value); }
}

public sealed record ConfigModelVariant(VariantId Id) : ConfigProviderOverlays
{
    [JsonPropertyName("id"), JsonRequired]
    public VariantId Id { get; init => field = ModelProviderContract.Id(value, value.IsInitialized()); } = ModelProviderContract.Id(Id, Id.IsInitialized());
}

public sealed record ConfigModelInfo : ConfigProviderOverlays
{
    [JsonPropertyName("modelID"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(OptionalValueJsonConverter<ModelId>))] public ModelId? ModelId { get; init; }
    [JsonPropertyName("family"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(OptionalValueJsonConverter<ModelFamily>))] public ModelFamily? Family { get; init; }
    [JsonPropertyName("name"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<string>))] public string? Name { get; init; }
    [JsonPropertyName("compatibility"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<ModelCompatibility>))] public ModelCompatibility? Compatibility { get; init; }
    [JsonPropertyName("package"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<string>))] public string? Package { get; init; }
    [JsonPropertyName("capabilities"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<ModelCapabilities>))] public ModelCapabilities? Capabilities { get; init; }
    [JsonPropertyName("variants"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(PromptAttachmentListJsonConverter<ConfigModelVariant>))] public IReadOnlyList<ConfigModelVariant>? Variants { get; init; }
    [JsonPropertyName("cost"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public ConfigModelCosts? Cost { get; init; }
    [JsonPropertyName("disabled"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(OptionalValueJsonConverter<bool>))] public bool? Disabled { get; init; }
    [JsonPropertyName("limit"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<ConfigModelLimit>))] public ConfigModelLimit? Limit { get; init; }
}

public sealed record ConfigProviderInfo : ConfigProviderOverlays
{
    [JsonPropertyName("name"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<string>))] public string? Name { get; init; }
    [JsonPropertyName("env"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(PromptAttachmentListJsonConverter<string>))] public IReadOnlyList<string>? Env { get; init; }
    [JsonPropertyName("package"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<string>))] public string? Package { get; init; }
    [JsonPropertyName("models"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(ConfigRecordJsonConverter<ConfigModelInfo>))] public IReadOnlyDictionary<string, ConfigModelInfo>? Models { get; init; }
}
