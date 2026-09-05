namespace OpenCode.Core.Integrations.Wellknown;

using System.Text.Json;
using System.Text.Json.Nodes;
using OpenCode.Core.Config;
using OpenCode.Core.Database;
using OpenCode.Schema;

/// <summary>Config.loadWellknown's credential gate and source precedence, without ambient secret expansion or plugin execution.</summary>
public sealed class WellknownConfigSources(WellknownService wellknown, CredentialStore credentials)
{
    public async Task<ConfigSnapshot> LoadAsync(CancellationToken ct = default)
    {
        var documents = new List<ConfigSource>();
        var diagnostics = new List<ConfigDiagnostic>();
        IReadOnlyList<WellknownEntry> entries;
        try { entries = await wellknown.EntriesAsync(ct); }
        catch (Exception error) when (error is WellknownDiscoveryException or JsonException or IOException)
        {
            return new([], [new(null, "$", "unavailable", "Wellknown sources could not be discovered; the registry was retained.")]);
        }
        foreach (var entry in entries)
        {
            if (entry.Manifest.Auth is not { } auth) continue;
            // Exact URL integration identity only; never fall back to Console/provider credentials.
            var stored = await credentials.GetActiveCredentialAsync(entry.Origin, ct);
            if (stored is null || stored.IntegrationId != entry.Origin) continue;
            if (stored.Value.Deserialize(OpenCodeJsonContext.Default.CredentialValue) is not CredentialKey credential) continue;
            try
            {
                var configs = await wellknown.ResolveAuthenticatedAsync(entry, credential.Key, ct);
                foreach (var config in configs)
                {
                    var document = JsonNode.Parse(config.GetRawText())!.AsObject();
                    foreach (var field in new[] { "plugin", "plugins" })
                        if (document.Remove(field)) diagnostics.Add(new(entry.Origin, "$." + field, "unsupported",
                            "Downloaded plugin declarations are not loaded: a compatible native contribution runtime is required."));
                    Substitute(document, auth.Env, credential.Key);
                    var canonical = ConfigEntryProjection.Normalize(document, (path, kind, message) =>
                        diagnostics.Add(new(entry.Origin, path, kind, message)));
                    documents.Add(new ConfigSource.Document(null, JsonSerializer.SerializeToNode(canonical,
                        OpenCodeJsonContext.Default.OpenCodeConfiguration)!.AsObject()));
                }
            }
            catch (Exception error) when (error is WellknownDiscoveryException or JsonException or NotSupportedException)
            {
                diagnostics.Add(new(entry.Origin, "$", "unavailable", "Wellknown configuration is unavailable or unsupported; its source remains registered."));
            }
        }
        return new(documents.AsReadOnly(), diagnostics.AsReadOnly());
    }

    /// <summary>Wellknown documents precede all global/explicit/project/content entries; later sources retain precedence.</summary>
    public static ConfigSnapshot Prepend(ConfigSnapshot wellknown, ConfigSnapshot local) => new(
        wellknown.Sources.Concat(local.Sources).ToArray(), wellknown.Diagnostics.Concat(local.Diagnostics).ToArray());

    private static void Substitute(JsonNode? node, string env, string key)
    {
        if (node is JsonObject map)
            foreach (var name in map.Select(pair => pair.Key).ToArray())
            {
                if (map[name] is JsonValue value && value.TryGetValue<string>(out var text))
                    map[name] = WellknownTransport.Substitute(text, env, key);
                else Substitute(map[name], env, key);
            }
        if (node is JsonArray array)
            for (var index = 0; index < array.Count; index++)
            {
                if (array[index] is JsonValue value && value.TryGetValue<string>(out var text))
                    array[index] = WellknownTransport.Substitute(text, env, key);
                else Substitute(array[index], env, key);
            }
    }
}
