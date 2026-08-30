namespace OpenCode.Core.Llm;

using System.Text.Json;
using OpenCode.Core.Config;
using OpenCode.Core.Database;
using OpenCode.Schema;

public sealed class ProviderResolver
{
    private readonly HttpClient _http;
    private readonly CredentialStore _credentialStore;

    public ProviderResolver(HttpClient http, CredentialStore credentialStore)
    {
        _http = http;
        _credentialStore = credentialStore;
    }

    public async Task<(ILlmClient Client, string ModelId)> ResolveAsync(string? requestedModel = null, CancellationToken ct = default)
    {
        var config = ConfigLoader.LoadConfig();
        var auth = ConfigLoader.LoadAuth();

        // 1. Get Google API key if available
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

        // 2. Get OpenAI token if available
        string? openAiToken = auth.GetValueOrDefault("openai")?.Access ?? auth.GetValueOrDefault("openai")?.Key;
        if (string.IsNullOrEmpty(openAiToken))
        {
            var storedOpenAi = await _credentialStore.GetActiveCredentialAsync("openai", ct);
            if (storedOpenAi is not null)
            {
                using var doc = JsonDocument.Parse(storedOpenAi.ValueJson);
                if (doc.RootElement.TryGetProperty("access", out var accProp))
                {
                    openAiToken = accProp.GetString();
                }
            }
        }

        // 3. Get OpenCode proxy token if available
        string? opencodeToken = null;
        var storedOpenCode = await _credentialStore.GetActiveCredentialAsync("opencode", ct);
        if (storedOpenCode is not null)
        {
            using var doc = JsonDocument.Parse(storedOpenCode.ValueJson);
            if (doc.RootElement.TryGetProperty("access", out var accProp))
            {
                opencodeToken = accProp.GetString();
            }
            else if (doc.RootElement.TryGetProperty("key", out var keyProp))
            {
                opencodeToken = keyProp.GetString();
            }
        }

        var targetModel = requestedModel ?? config.Model;

        // If target model explicitly points to Google or Gemini
        if (targetModel is not null && (targetModel.StartsWith("google", StringComparison.OrdinalIgnoreCase) || targetModel.Contains("gemini", StringComparison.OrdinalIgnoreCase)))
        {
            if (!string.IsNullOrEmpty(googleKey))
            {
                return (new GoogleLlmClient(_http, googleKey), targetModel);
            }
        }

        // If target model is specified and is OpenCode
        if (targetModel is not null && targetModel.StartsWith("opencode", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrEmpty(opencodeToken))
            {
                var modelName = targetModel["opencode/".Length..];
                return (new OpenAiLlmClient(_http, opencodeToken, "https://opencode.ai/inference/openai/v1"), modelName);
            }
        }

        // Reliable fallback: If Google key is present, it's immediately usable
        if (!string.IsNullOrEmpty(googleKey))
        {
            return (new GoogleLlmClient(_http, googleKey), "gemini-2.5-flash");
        }

        // Fallback: OpenAI
        if (!string.IsNullOrEmpty(openAiToken))
        {
            return (new OpenAiLlmClient(_http, openAiToken, "https://chatgpt.com/backend-api/codex"), "gpt-4o");
        }

        throw new InvalidOperationException(
            "No active AI provider credentials found in ~/.local/share/opencode/auth.json or opencode.db."
        );
    }
}
