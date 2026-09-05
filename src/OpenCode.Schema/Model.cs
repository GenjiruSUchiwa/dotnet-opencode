namespace OpenCode.Schema;

using System.Text.Json;
using System.Text.Json.Serialization;

[JsonConverter(typeof(ModelIdJsonConverter))]
[Vogen.ValueObject<string>(comparison: Vogen.ComparisonGeneration.Omit)]
public readonly partial struct ModelId
{
    public static ModelId FromExisting(string value) => From(PromptValidation.Required(value));
    public void Deconstruct(out string value) => value = Value;
    public override string ToString() => Value;
    public static implicit operator string(ModelId id) => id.Value;
    public static explicit operator ModelId(string value) => FromExisting(value);
}

public sealed class ModelIdJsonConverter() : ScalarJsonConverter<ModelId, string>(ModelId.FromExisting, static value => value.Value)
{
    public override ModelId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String) throw new JsonException("Expected model ID string.");
        return ModelId.FromExisting(reader.GetString()!);
    }
    public override void Write(Utf8JsonWriter writer, ModelId value, JsonSerializerOptions options) =>
        writer.WriteStringValue(PromptValidation.Required(value.Value));
}

[JsonConverter(typeof(VariantIdJsonConverter))]
[Vogen.ValueObject<string>(comparison: Vogen.ComparisonGeneration.Omit)]
public readonly partial struct VariantId
{
    public static VariantId FromExisting(string value) => From(PromptValidation.Required(value));
    public void Deconstruct(out string value) => value = Value;
    public override string ToString() => Value;
    public static implicit operator string(VariantId id) => id.Value;
    public static explicit operator VariantId(string value) => FromExisting(value);
}

public sealed class VariantIdJsonConverter() : ScalarJsonConverter<VariantId, string>(VariantId.FromExisting, static value => value.Value)
{
    public override VariantId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String) throw new JsonException("Expected variant ID string.");
        return VariantId.FromExisting(reader.GetString()!);
    }
    public override void Write(Utf8JsonWriter writer, VariantId value, JsonSerializerOptions options) =>
        writer.WriteStringValue(PromptValidation.Required(value.Value));
}

[JsonConverter(typeof(ModelFamilyJsonConverter))]
[Vogen.ValueObject<string>(comparison: Vogen.ComparisonGeneration.Omit)]
public readonly partial struct ModelFamily
{
    public static ModelFamily FromExisting(string value) => From(PromptValidation.Required(value));
    public void Deconstruct(out string value) => value = Value;
    public override string ToString() => Value;
    public static implicit operator string(ModelFamily family) => family.Value;
    public static explicit operator ModelFamily(string value) => FromExisting(value);
}

public sealed class ModelFamilyJsonConverter() : ScalarJsonConverter<ModelFamily, string>(ModelFamily.FromExisting, static value => value.Value)
{
    public override ModelFamily Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String) throw new JsonException("Expected model family string.");
        return ModelFamily.FromExisting(reader.GetString()!);
    }
    public override void Write(Utf8JsonWriter writer, ModelFamily value, JsonSerializerOptions options) =>
        writer.WriteStringValue(PromptValidation.Required(value.Value));
}

// String properties retain the existing runner/store reference API. The wire brands are unrestricted strings.
public sealed record ModelRef(
    string ProviderId,
    string Id,
    [property: JsonPropertyName("variant"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string? Variant = null
)
{
    [JsonPropertyName("providerID"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string ProviderId { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(ProviderId);
    [JsonPropertyName("id"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string Id { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Id);

    public static ModelRef Parse(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var providerEnd = input.IndexOf('/');
        if (providerEnd <= 0) throw new ArgumentException("Invalid model reference.", nameof(input));
        var providerId = input[..providerEnd];
        var variantStart = input.IndexOf('#', providerEnd + 1);
        var id = input[(providerEnd + 1)..(variantStart < 0 ? input.Length : variantStart)];
        var variant = variantStart < 0 ? null : input[(variantStart + 1)..];
        if (id.Length == 0 || providerId.Contains('#') || (variant is not null && (variant.Length == 0 || variant.Contains('#'))))
            throw new ArgumentException("Invalid model reference.", nameof(input));
        return new ModelRef(providerId, id, variant);
    }

    public override string ToString() => Variant is not null ? $"{ProviderId}/{Id}#{Variant}" : $"{ProviderId}/{Id}";
}

public sealed record ModelCompatibility(
    [property: JsonPropertyName("reasoningField"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string? ReasoningField = null,
    [property: JsonPropertyName("requireReasoning"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(OptionalValueJsonConverter<bool>))] bool? RequireReasoning = null,
    string? MaxTokensField = null,
    [property: JsonPropertyName("requireFinishReason"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(OptionalValueJsonConverter<bool>))] bool? RequireFinishReason = null,
    [property: JsonPropertyName("requireAssistantAfterTool"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(OptionalValueJsonConverter<bool>))] bool? RequireAssistantAfterTool = null
)
{
    [JsonPropertyName("maxTokensField"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string? MaxTokensField { get; init => field = Validate(value); } = Validate(MaxTokensField);
    private static string? Validate(string? value) => value is null or "max_completion_tokens" or "max_tokens"
        ? value : throw new JsonException("Unknown maximum-token field.");
}

public sealed record ModelCapabilities(
    [property: JsonPropertyName("tools"), JsonRequired] bool Tools,
    IReadOnlyList<string> Input,
    IReadOnlyList<string> Output,
    [property: JsonPropertyName("responsesWebsockets"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(OptionalValueJsonConverter<bool>))] bool? ResponsesWebsockets = null
)
{
    [JsonPropertyName("input"), JsonRequired, JsonConverter(typeof(PromptAttachmentListJsonConverter<string>))]
    public IReadOnlyList<string> Input { get; init => field = PromptValidation.Required(PromptValidation.Attachments(value)); } = PromptValidation.Required(PromptValidation.Attachments(Input));
    [JsonPropertyName("output"), JsonRequired, JsonConverter(typeof(PromptAttachmentListJsonConverter<string>))]
    public IReadOnlyList<string> Output { get; init => field = PromptValidation.Required(PromptValidation.Attachments(value)); } = PromptValidation.Required(PromptValidation.Attachments(Output));
    public static ModelCapabilities CreateDefault() => new(true, ["text", "image"], ["text"]);
}

public sealed record ModelCostTier(double Size)
{
    [JsonPropertyName("type"), JsonRequired]
    public string Type
    {
        get => "context";
        init { if (value != "context") throw new JsonException("Model cost tier must have type context."); }
    }
    [JsonPropertyName("size"), JsonRequired, JsonConverter(typeof(IntegerNumberJsonConverter))]
    public double Size { get; init => field = ModelProviderContract.Integer(value); } = ModelProviderContract.Integer(Size);
}

public sealed record ModelCacheCost(
    [property: JsonPropertyName("read"), JsonRequired] MoneyPerMillionTokens Read,
    [property: JsonPropertyName("write"), JsonRequired] MoneyPerMillionTokens Write
);

public sealed record ModelCost(
    [property: JsonPropertyName("input"), JsonRequired] MoneyPerMillionTokens Input,
    [property: JsonPropertyName("output"), JsonRequired] MoneyPerMillionTokens Output,
    ModelCacheCost Cache,
    [property: JsonPropertyName("tier"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<ModelCostTier>))] ModelCostTier? Tier = null
)
{
    [JsonPropertyName("cache"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<ModelCacheCost>))]
    public ModelCacheCost Cache { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Cache);
}

public sealed record ModelLimit(double Context, double Output, double? Input = null)
{
    [JsonPropertyName("context"), JsonRequired, JsonConverter(typeof(IntegerNumberJsonConverter))]
    public double Context { get; init => field = ModelProviderContract.Integer(value); } = ModelProviderContract.Integer(Context);
    [JsonPropertyName("output"), JsonRequired, JsonConverter(typeof(IntegerNumberJsonConverter))]
    public double Output { get; init => field = ModelProviderContract.Integer(value); } = ModelProviderContract.Integer(Output);
    [JsonPropertyName("input"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(OptionalIntegerNumberJsonConverter))]
    public double? Input { get; init => field = value is null ? null : ModelProviderContract.Integer(value.Value); } = Input is null ? null : ModelProviderContract.Integer(Input.Value);
}

public sealed record ModelVariant(
    VariantId Id,
    [property: JsonPropertyName("settings"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<IReadOnlyDictionary<string, JsonElement>>))] IReadOnlyDictionary<string, JsonElement>? Settings = null,
    [property: JsonPropertyName("headers"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(ProviderHeadersJsonConverter))] IReadOnlyDictionary<string, string>? Headers = null,
    [property: JsonPropertyName("body"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<IReadOnlyDictionary<string, JsonElement>>))] IReadOnlyDictionary<string, JsonElement>? Body = null
)
{
    [JsonPropertyName("id"), JsonRequired]
    public VariantId Id { get; init => field = ModelProviderContract.Id(value, value.IsInitialized()); } = ModelProviderContract.Id(Id, Id.IsInitialized());
}

public sealed record ModelTime(double Released)
{
    [JsonPropertyName("released"), JsonRequired, JsonConverter(typeof(FiniteNumberJsonConverter))]
    public double Released { get; init => field = FiniteNumberJsonConverter.Validate(value); } = FiniteNumberJsonConverter.Validate(Released);
}

public sealed record ModelInfo(
    ModelId Id,
    ModelId ModelId,
    ProviderId ProviderId,
    string Name,
    ModelCapabilities Capabilities,
    IReadOnlyList<ModelVariant> Variants,
    ModelTime Time,
    IReadOnlyList<ModelCost> Cost,
    string Status,
    [property: JsonPropertyName("enabled"), JsonRequired] bool Enabled,
    ModelLimit Limit,
    [property: JsonPropertyName("family"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(OptionalValueJsonConverter<ModelFamily>))] ModelFamily? Family = null,
    [property: JsonPropertyName("compatibility"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<ModelCompatibility>))] ModelCompatibility? Compatibility = null,
    [property: JsonPropertyName("package"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<string>))] string? Package = null,
    [property: JsonPropertyName("settings"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<IReadOnlyDictionary<string, JsonElement>>))] IReadOnlyDictionary<string, JsonElement>? Settings = null,
    [property: JsonPropertyName("headers"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(ProviderHeadersJsonConverter))] IReadOnlyDictionary<string, string>? Headers = null,
    [property: JsonPropertyName("body"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<IReadOnlyDictionary<string, JsonElement>>))] IReadOnlyDictionary<string, JsonElement>? Body = null
)
{
    [JsonPropertyName("id"), JsonRequired]
    public ModelId Id { get; init => field = ModelProviderContract.Id(value, value.IsInitialized()); } = ModelProviderContract.Id(Id, Id.IsInitialized());
    [JsonPropertyName("modelID"), JsonRequired]
    public ModelId ModelId { get; init => field = ModelProviderContract.Id(value, value.IsInitialized()); } = ModelProviderContract.Id(ModelId, ModelId.IsInitialized());
    [JsonPropertyName("providerID"), JsonRequired]
    public ProviderId ProviderId { get; init => field = ModelProviderContract.Id(value, value.IsInitialized()); } = ModelProviderContract.Id(ProviderId, ProviderId.IsInitialized());
    [JsonPropertyName("name"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string Name { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Name);
    [JsonPropertyName("capabilities"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<ModelCapabilities>))]
    public ModelCapabilities Capabilities { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Capabilities);
    [JsonPropertyName("variants"), JsonRequired, JsonConverter(typeof(PromptAttachmentListJsonConverter<ModelVariant>))]
    public IReadOnlyList<ModelVariant> Variants { get; init => field = PromptValidation.Required(PromptValidation.Attachments(value)); } = PromptValidation.Required(PromptValidation.Attachments(Variants));
    [JsonPropertyName("time"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<ModelTime>))]
    public ModelTime Time { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Time);
    [JsonPropertyName("cost"), JsonRequired, JsonConverter(typeof(PromptAttachmentListJsonConverter<ModelCost>))]
    public IReadOnlyList<ModelCost> Cost { get; init => field = PromptValidation.Required(PromptValidation.Attachments(value)); } = PromptValidation.Required(PromptValidation.Attachments(Cost));
    [JsonPropertyName("status"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<string>))]
    public string Status { get; init => field = ValidateStatus(value); } = ValidateStatus(Status);
    [JsonPropertyName("limit"), JsonRequired, JsonConverter(typeof(NonNullPromptJsonConverter<ModelLimit>))]
    public ModelLimit Limit { get; init => field = PromptValidation.Required(value); } = PromptValidation.Required(Limit);

    private static string ValidateStatus(string value) => value is "alpha" or "beta" or "deprecated" or "active"
        ? value : throw new JsonException("Unknown model status.");

    // These are the source's explicit helper defaults, never an implicit catalog metadata fallback.
    public static ModelInfo CreateDefault(ProviderId providerId, ModelId id) => new(
        id, id, providerId, PromptValidation.Required(id.Value), ModelCapabilities.CreateDefault(), [], new ModelTime(0), [],
        "active", true, new ModelLimit(200_000, 32_000));
}
