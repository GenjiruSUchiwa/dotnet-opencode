namespace OpenCode.Protocol.Groups;

using System.Text.Json;
using System.Text.Json.Serialization;
using OpenCode.Schema;

/// <summary>
/// Shared Location.response envelope from packages/schema/src/location.ts.
/// </summary>
public sealed record LocationResponse<T>(
    [property: JsonPropertyName("location"), JsonRequired] LocationInfo Location,
    [property: JsonPropertyName("data"), JsonRequired] T Data
) : IJsonOnDeserialized
{
    void IJsonOnDeserialized.OnDeserialized()
    {
        if (Location is null) throw new JsonException("Location response requires location.");
    }
}

/// <summary>model.default permits an absent data field (UndefinedOr), not a fabricated default Model.Info.</summary>
public sealed record DefaultModelResponse(
    [property: JsonPropertyName("location"), JsonRequired] LocationInfo Location,
    [property: JsonPropertyName("data"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonConverter(typeof(NonNullPromptJsonConverter<ModelInfo>))] ModelInfo? Data = null
) : IJsonOnDeserialized
{
    void IJsonOnDeserialized.OnDeserialized()
    {
        if (Location is null) throw new JsonException("Default model response requires location.");
    }
}
