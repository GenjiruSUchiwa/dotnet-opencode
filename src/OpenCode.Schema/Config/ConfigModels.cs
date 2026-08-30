namespace OpenCode.Schema;

using System.Text.Json.Serialization;

public sealed record OpenCodeConfig(
    [property: JsonPropertyName("model")] string? Model = null,
    [property: JsonPropertyName("autoupdate")] bool? Autoupdate = null,
    [property: JsonPropertyName("providers")] IReadOnlyDictionary<string, ProviderConfig>? Providers = null,
    [property: JsonPropertyName("provider")] IReadOnlyDictionary<string, ProviderConfig>? Provider = null
);

public sealed record ProviderConfig(
    [property: JsonPropertyName("name")] string? Name = null,
    [property: JsonPropertyName("api")] string? Api = null,
    [property: JsonPropertyName("npm")] string? Npm = null,
    [property: JsonPropertyName("models")] IReadOnlyDictionary<string, ModelConfig>? Models = null,
    [property: JsonPropertyName("options")] IReadOnlyDictionary<string, object>? Options = null
);

public sealed record ModelConfig(
    [property: JsonPropertyName("name")] string? Name = null,
    [property: JsonPropertyName("id")] string? Id = null,
    [property: JsonPropertyName("reasoning")] bool? Reasoning = null,
    [property: JsonPropertyName("options")] IReadOnlyDictionary<string, object>? Options = null
);

public sealed record AuthEntry(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("key")] string? Key = null,
    [property: JsonPropertyName("access")] string? Access = null,
    [property: JsonPropertyName("refresh")] string? Refresh = null,
    [property: JsonPropertyName("expires")] long? Expires = null,
    [property: JsonPropertyName("accountId")] string? AccountId = null
);
