namespace OpenCode.Core.Integrations.Wellknown;

using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenCode.Schema;

public sealed record WellknownAuth(ImmutableArray<string> Command, string Env);
public sealed record WellknownRemoteConfig(string Url, ImmutableDictionary<string, string>? Headers = null);
public sealed record WellknownManifest(WellknownAuth? Auth = null, JsonElement? Config = null,
    [property: JsonPropertyName("remote_config")] WellknownRemoteConfig? RemoteConfig = null)
{
    public static WellknownManifest Decode(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new JsonException("Wellknown manifest must be an object.");
        foreach (var field in new[] { "auth", "remote_config" })
            if (value.TryGetProperty(field, out var part) && part.ValueKind != JsonValueKind.Object)
                throw new JsonException($"Wellknown {field} must be an object.");
        var manifest = value.Deserialize(WellknownJsonContext.Default.WellknownManifest)
            ?? throw new JsonException("Wellknown manifest is missing.");
        if (manifest.Auth is { } auth && (auth.Command.IsDefault || auth.Command.Any(item => item is null) || auth.Env is null))
            throw new JsonException("Wellknown auth requires a command array and env string.");
        if (manifest.Config is { ValueKind: not (JsonValueKind.Object or JsonValueKind.Null) })
            throw new JsonException("Wellknown config must be an object or null.");
        if (manifest.RemoteConfig is { } remote && (remote.Url is null || remote.Headers?.Any(pair => pair.Value is null) == true))
            throw new JsonException("Wellknown remote_config requires a URL and string headers.");
        if (value.TryGetProperty("remote_config", out var remoteValue) && remoteValue.TryGetProperty("headers", out var headers)
            && headers.ValueKind != JsonValueKind.Object) throw new JsonException("Wellknown headers must be an object.");
        return manifest with { Config = manifest.Config?.Clone() };
    }
}

public sealed record WellknownEntry(string Origin, IntegrationId IntegrationId, WellknownManifest Manifest);
public sealed class WellknownDiscoveryException(string message, Exception? inner = null) : Exception(message, inner);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, RespectNullableAnnotations = true)]
[JsonSerializable(typeof(WellknownManifest))]
[JsonSerializable(typeof(string[]))]
internal partial class WellknownJsonContext : JsonSerializerContext;
