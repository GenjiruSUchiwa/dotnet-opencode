namespace OpenCode.Schema;

using System.Text.Json;
using System.Text.Json.Serialization;

public sealed record OpenCodeConfig(
    [property: JsonPropertyName("model")] JsonElement? Model = null,
    [property: JsonPropertyName("autoupdate")] JsonElement? Autoupdate = null,
    [property: JsonPropertyName("providers")] IReadOnlyDictionary<string, ProviderConfig>? Providers = null,
    [property: JsonPropertyName("provider")] IReadOnlyDictionary<string, ProviderConfig>? Provider = null,
    [property: JsonPropertyName("default_agent")] string? DefaultAgent = null
);

public sealed record ProviderConfig(
    [property: JsonPropertyName("name")] string? Name = null,
    [property: JsonPropertyName("api")] string? Api = null,
    [property: JsonPropertyName("npm")] string? Npm = null,
    [property: JsonPropertyName("models")] IReadOnlyDictionary<string, ModelConfig>? Models = null,
    [property: JsonPropertyName("options")] IReadOnlyDictionary<string, JsonElement>? Options = null,
    [property: JsonPropertyName("package")] string? Package = null,
    [property: JsonPropertyName("env")] IReadOnlyList<string>? Env = null,
    [property: JsonPropertyName("settings")] IReadOnlyDictionary<string, JsonElement>? Settings = null,
    [property: JsonPropertyName("headers")] IReadOnlyDictionary<string, string>? Headers = null,
    [property: JsonPropertyName("body")] IReadOnlyDictionary<string, JsonElement>? Body = null
);

public sealed record ModelConfig(
    [property: JsonPropertyName("name")] string? Name = null,
    [property: JsonPropertyName("id")] string? Id = null,
    [property: JsonPropertyName("reasoning")] bool? Reasoning = null,
    [property: JsonPropertyName("options")] IReadOnlyDictionary<string, JsonElement>? Options = null,
    [property: JsonPropertyName("modelID")] string? ModelId = null,
    [property: JsonPropertyName("package")] string? Package = null,
    [property: JsonPropertyName("disabled")] bool? Disabled = null,
    [property: JsonPropertyName("settings")] IReadOnlyDictionary<string, JsonElement>? Settings = null,
    [property: JsonPropertyName("headers")] IReadOnlyDictionary<string, string>? Headers = null,
    [property: JsonPropertyName("body")] IReadOnlyDictionary<string, JsonElement>? Body = null,
    [property: JsonPropertyName("variants")] IReadOnlyList<ModelVariantConfig>? Variants = null,
    [property: JsonPropertyName("compatibility")] JsonElement? Compatibility = null
);

public sealed record ModelVariantConfig(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("settings")] IReadOnlyDictionary<string, JsonElement>? Settings = null,
    [property: JsonPropertyName("headers")] IReadOnlyDictionary<string, string>? Headers = null,
    [property: JsonPropertyName("body")] IReadOnlyDictionary<string, JsonElement>? Body = null
);

public sealed record AuthEntry(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("key")] string? Key = null,
    [property: JsonPropertyName("access")] string? Access = null,
    [property: JsonPropertyName("refresh")] string? Refresh = null,
    [property: JsonPropertyName("expires")] long? Expires = null,
    [property: JsonPropertyName("accountId")] string? AccountId = null
);
