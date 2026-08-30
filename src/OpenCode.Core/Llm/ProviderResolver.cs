namespace OpenCode.Core.Llm;

using System.Text.Json;
using OpenCode.Core.Config;
using OpenCode.Core.Database;

public sealed record ResolvedModel(
    ILlmClient Client,
    string ModelId,
    object? GenerationConfig = null
);

public sealed class ProviderResolver
{
    private readonly HttpClient _http;
    private readonly CredentialStore _credentialStore;

    public ProviderResolver(HttpClient http, CredentialStore credentialStore)
    {
        _http = http;
        _credentialStore = credentialStore;
    }

    public async Task<ResolvedModel> ResolveAsync(
        string? requestedModel = null,
        string? requestedVariant = null,
        CancellationToken ct = default)
    {
        var config = ConfigLoader.LoadConfig();
        var auth = ConfigLoader.LoadAuth();

        var modelSpec = requestedModel ?? "gemini-2.5-flash";

        // Support model:variant syntax (e.g. "gemini-2.5-flash:high")
        string? variant = requestedVariant;
        if (modelSpec.Contains(':') && !modelSpec.Contains("://"))
        {
            var parts = modelSpec.Split(':');
            modelSpec = parts[0];
            variant ??= parts[1];
        }

        // 1. Check if user has direct Google API key in auth.json or opencode.db
        string? googleKey = auth.GetValueOrDefault("google")?.Key;
        if (string.IsNullOrEmpty(googleKey))
        {
            var storedGoogle = await _credentialStore.GetActiveCredentialAsync("google", ct);
            if (storedGoogle is not null)
            {
                using var doc = JsonDocument.Parse(storedGoogle.ValueJson);
                if (doc.RootElement.TryGetProperty("key", out var keyProp))
                {
                    googleKey = keyProp.GetString();
                }
            }
        }

        if (!string.IsNullOrEmpty(googleKey) && (requestedModel == null || modelSpec.Contains("google") || modelSpec.Contains("gemini")))
        {
            var targetModel = modelSpec.Contains("3.7") ? "gemini-2.5-flash" : modelSpec;
            if (targetModel.StartsWith("google/")) targetModel = targetModel["google/".Length..];
            return new ResolvedModel(new GoogleLlmClient(_http, googleKey), targetModel);
        }

        // 2. Check OpenCode Console credentials
        var opencodeCred = await _credentialStore.GetActiveCredentialAsync("opencode", ct);
        if (opencodeCred is not null)
        {
            using var doc = JsonDocument.Parse(opencodeCred.ValueJson);
            var token = doc.RootElement.TryGetProperty("access", out var accProp) ? accProp.GetString() : null;
            var orgId = doc.RootElement.TryGetProperty("metadata", out var metaProp) && metaProp.TryGetProperty("orgID", out var orgProp) ? orgProp.GetString() : null;
            var server = (doc.RootElement.TryGetProperty("metadata", out var metaServer) && metaServer.TryGetProperty("server", out var servProp) ? servProp.GetString() : null) ?? "https://opencode.ai/console";

            if (!string.IsNullOrEmpty(token))
            {
                var consoleConfig = await FetchConsoleConfigAsync(server, token, orgId, ct);
                if (consoleConfig is not null)
                {
                    foreach (var (providerId, providerData) in consoleConfig)
                    {
                        var targetId = modelSpec.StartsWith($"{providerId}/", StringComparison.OrdinalIgnoreCase)
                            ? modelSpec[(providerId.Length + 1)..]
                            : modelSpec;

                        if (providerData.Models.TryGetValue(targetId, out var modelData))
                        {
                            object? generationConfig = null;
                            if (variant is not null && modelData.Variants is not null && modelData.Variants.TryGetValue(variant, out var variantData))
                            {
                                generationConfig = variantData.GenerationConfig;
                            }

                            ILlmClient client = providerId.Contains("google")
                                ? new GoogleLlmClient(_http, apiKey: token, baseUrl: providerData.BaseUrl, orgId: orgId)
                                : new OpenAiLlmClient(_http, token: token, baseUrl: providerData.BaseUrl);

                            return new ResolvedModel(client, targetId, generationConfig);
                        }
                    }
                }
            }
        }

        // Fallback: If Google is available
        if (!string.IsNullOrEmpty(googleKey))
        {
            return new ResolvedModel(new GoogleLlmClient(_http, googleKey), "gemini-2.5-flash");
        }

        throw new InvalidOperationException("No suitable AI provider credentials found.");
    }

    private async Task<Dictionary<string, ConsoleProviderData>?> FetchConsoleConfigAsync(
        string server,
        string token,
        string? orgId,
        CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{server.TrimEnd('/')}/api/config");
            req.Headers.UserAgent.ParseAdd("opencode");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            if (!string.IsNullOrEmpty(orgId))
            {
                req.Headers.Add("x-org-id", orgId);
            }

            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("config", out var configProp) ||
                !configProp.TryGetProperty("provider", out var provProp))
            {
                return null;
            }

            var result = new Dictionary<string, ConsoleProviderData>(StringComparer.OrdinalIgnoreCase);
            foreach (var prov in provProp.EnumerateObject())
            {
                var provId = prov.Name;
                string? baseUrl = null;
                if (prov.Value.TryGetProperty("api", out var apiProp))
                {
                    baseUrl = apiProp.GetString();
                }
                else if (prov.Value.TryGetProperty("options", out var optProp) && optProp.TryGetProperty("baseURL", out var optBaseUrl))
                {
                    baseUrl = optBaseUrl.GetString();
                }

                var models = new Dictionary<string, ConsoleModelData>(StringComparer.OrdinalIgnoreCase);
                if (prov.Value.TryGetProperty("models", out var modelsProp))
                {
                    foreach (var m in modelsProp.EnumerateObject())
                    {
                        var modelId = m.Name;
                        var variants = new Dictionary<string, ConsoleVariantData>(StringComparer.OrdinalIgnoreCase);

                        if (m.Value.TryGetProperty("variants", out var varProp))
                        {
                            foreach (var v in varProp.EnumerateObject())
                            {
                                JsonElement? genConfig = null;
                                if (v.Value.TryGetProperty("generationConfig", out var gcProp) ||
                                    v.Value.TryGetProperty("generation_config", out gcProp))
                                {
                                    genConfig = gcProp.Clone();
                                }
                                else if (v.Value.TryGetProperty("settings", out var settProp) &&
                                         (settProp.TryGetProperty("generationConfig", out gcProp) ||
                                          settProp.TryGetProperty("generation_config", out gcProp)))
                                {
                                    genConfig = gcProp.Clone();
                                }

                                variants[v.Name] = new ConsoleVariantData(genConfig);
                            }
                        }

                        models[modelId] = new ConsoleModelData(variants);
                    }
                }

                result[provId] = new ConsoleProviderData(baseUrl, models);
            }

            return result;
        }
        catch
        {
            return null;
        }
    }

    private sealed record ConsoleProviderData(string? BaseUrl, Dictionary<string, ConsoleModelData> Models);
    private sealed record ConsoleModelData(Dictionary<string, ConsoleVariantData> Variants);
    private sealed record ConsoleVariantData(JsonElement? GenerationConfig);
}
