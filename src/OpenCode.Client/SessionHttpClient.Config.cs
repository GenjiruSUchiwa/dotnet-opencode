namespace OpenCode.Client;

using System.Text.Json;
using System.Text.Json.Serialization;
using OpenCode.Schema;

public sealed partial class SessionHttpClient
{
    /// <summary>Returns the canonical bare source-entry array, in server precedence order; virtual document paths remain absent.</summary>
    public async Task<IReadOnlyList<ConfigEntry>> GetConfigAsync(string? directory = null, string? workspace = null, CancellationToken ct = default)
    {
        var entries = await RequestAsync(HttpMethod.Get, "/api/config" + Query(
            ("location[directory]", directory), ("location[workspace]", workspace)), ConfigHttpJsonContext.Default.ConfigEntries, ct);
        if (entries.Any(entry => entry is null)) throw Malformed("config.get", "Configuration source entries cannot contain null.");
        return entries;
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true, AllowOutOfOrderMetadataProperties = true)]
[JsonSerializable(typeof(IReadOnlyList<ConfigEntry>), TypeInfoPropertyName = "ConfigEntries")]
internal partial class ConfigHttpJsonContext : JsonSerializerContext;
