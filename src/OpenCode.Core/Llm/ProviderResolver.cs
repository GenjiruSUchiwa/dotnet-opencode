namespace OpenCode.Core.Llm;

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using OpenCode.Core.Config;
using OpenCode.Core.Database;
using OpenCode.Schema;

public sealed record ResolvedModel(
    ILlmClient Client,
    string ModelId,
    object? GenerationConfig = null
)
{
    public ModelRef Selection { get; init; } = new(Client.ProviderId, ModelId);
    public string ProviderMetadataKey => Client.ProviderMetadataKey;
}

public sealed partial class ProviderResolver
{
    private readonly HttpClient _http;
    private readonly CredentialStore _credentialStore;
    private readonly ConsoleIntegrationService _console;
    private readonly OpenAiOAuthService _openAi;

    public ProviderResolver(HttpClient http, CredentialStore credentialStore)
    {
        _http = http;
        _credentialStore = credentialStore;
        _console = new ConsoleIntegrationService(http, credentialStore);
        _openAi = new OpenAiOAuthService(credentialStore);
    }

    public async Task<ResolvedModel> ResolveAsync(
        string? requestedModel = null,
        string? requestedVariant = null,
        CancellationToken ct = default,
        string? directory = null,
        ModelRef? sessionModel = null,
        string? sessionId = null)
    {
        ct.ThrowIfCancellationRequested();
        var explicitReference = requestedModel ?? sessionModel?.ToString();
        var selection = explicitReference is not null ? ParseSelection(JsonSerializer.SerializeToElement(explicitReference)) : default;
        var snapshot = await LoadCatalogSnapshotAsync(directory, ct);
        if (explicitReference is null)
        {
            var preferred = (await ProjectCatalogAsync(snapshot, ct)).DefaultSelection
                ?? throw new LlmException(new LlmFailure.InvalidRequest("No available model is present in the configured catalog."));
            selection = (preferred.ProviderId, preferred.Id, preferred.Variant);
        }
        var selectedVariant = requestedVariant ?? selection.Variant;
        var variantId = selectedVariant == "default" ? null : selectedVariant;
        if (variantId is not null && (variantId.Length == 0 || variantId.Contains('#')))
            throw new ArgumentException("Variant must be a nonempty identifier without '#'.", nameof(requestedVariant));

        var console = snapshot.Console;
        var discovered = console?.Providers.GetValueOrDefault(selection.Provider);
        var integrationId = discovered is not null ? ConsoleIntegrationService.IntegrationId : selection.Provider;
        if (snapshot.IntegrationErrors.TryGetValue(integrationId, out var integrationError)) throw integrationError;
        var provider = snapshot.Document["providers"]?[selection.Provider]?.Deserialize<ProviderConfig>();
        if (provider is null)
            throw new LlmException(new LlmFailure.InvalidRequest("The selected provider is unavailable in the effective catalog."));
        var model = provider.Models?.GetValueOrDefault(selection.Model)
            ?? throw new InvalidOperationException("Selected model is unavailable for the selected provider. No substitute was selected.");
        if (provider.Options is not null || provider.Api is not null || provider.Npm is not null
            || model.Options is not null || model.Id is not null)
            throw new NotSupportedException("Legacy provider fields must use the provider configuration map, not the canonical providers map.");
        if (model.Disabled == true) throw new InvalidOperationException("Selected model is disabled.");
        var variant = variantId is null ? null : model.Variants?.FirstOrDefault(item => item.Id == variantId)
            ?? throw new InvalidOperationException("Selected variant is unavailable for this model.");
        var package = model.Package ?? provider.Package;
        // Package identity, not provider-name substrings, determines the wire protocol.
        var google = package is "@opencode-ai/ai/providers/google" or "aisdk:@ai-sdk/google";
        var chat = package is "@opencode-ai/ai/providers/openai-compatible" or "aisdk:@ai-sdk/openai-compatible";
        var anthropic = package is "@opencode-ai/ai/providers/anthropic" or "aisdk:@ai-sdk/anthropic" or "@opencode-ai/ai/providers/anthropic-compatible";
        var responses = package is "aisdk:@ai-sdk/openai" or "@opencode-ai/ai/providers/openai" or "@opencode-ai/ai/providers/openai/responses";
        var nativeChat = package == "@opencode-ai/ai/providers/openai/chat";
        if (!google && !chat && !anthropic && !responses && !nativeChat)
            throw new NotSupportedException("Selected provider transport is not implemented. Supported protocols are Google, Anthropic Messages, OpenAI Responses, and explicit Chat routes.");
        var compatibility = ReadCompatibility(model.Compatibility);

        var settings = Overlay(provider.Settings, model.Settings, variant?.Settings);
        var body = Overlay(provider.Body, model.Body, variant?.Body);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in new[] { provider.Headers, model.Headers, variant?.Headers })
            if (source is not null)
                foreach (var (name, value) in source) headers[name] = value;

        StoredCredential? credential;
        var consoleBound = discovered is not null || selection.Provider == ConsoleIntegrationService.IntegrationId;
        try
        {
            credential = discovered is not null
                ? console!.Credential ?? throw new LlmException(new LlmFailure.Authentication("Console catalog has no credential snapshot."))
                : consoleBound ? await _console.ResolveCredentialAsync(ct)
                : selection.Provider == OpenAiOAuthService.IntegrationId ? snapshot.OpenAiCredential
                : await _credentialStore.GetActiveCredentialAsync(selection.Provider, ct);
        }
        catch (InvalidDataException)
        {
            throw new LlmException(new LlmFailure.Authentication("The channel contains an invalid credential for the selected integration."));
        }
        var oauth = credential?.Value.GetProperty("type").GetString() == "oauth";
        var chatGpt = !consoleBound && credential is not null && OpenAiOAuthService.IsChatGptCredential(credential);
        if (oauth && !consoleBound && !chatGpt)
            throw new LlmException(new LlmFailure.Unsupported("The selected OAuth integration is not supported; no other credential was selected."));
        if (chatGpt && !responses)
            throw new LlmException(new LlmFailure.Unsupported("ChatGPT OAuth requires the explicit OpenAI Responses protocol; other protocols are not substituted."));
        var connectionKey = credential is not null ? credential.Value.GetProperty(oauth ? "access" : "key").GetString()
            : provider.Env?.Where(name => name.Length > 0).Select(Environment.GetEnvironmentVariable).FirstOrDefault(value => !string.IsNullOrEmpty(value));
        // Validate configured transport settings before adding integration metadata.
        // Account/server/org metadata is not itself a provider option.
        if (package!.StartsWith("aisdk:", StringComparison.Ordinal))
            LlmHttp.RequireFields(settings, responses ? ["apiKey", "baseURL", "organization", "project", "queryParams", .. ResponsesRequestLowering.OptionNames]
                : anthropic ? ["apiKey", "authToken", "baseURL", .. AnthropicRequestLowering.OptionNames] : google
                ? ["apiKey", "baseURL", "thinkingConfig", "cachedContent", "safetySettings", "serviceTier"]
                : ["apiKey", "baseURL", "reasoningEffort", "store"]);
        else LlmHttp.RequireFields(settings, responses || nativeChat ? ["apiKey", "baseURL", "organization", "project", "queryParams", "providerOptions"]
            : anthropic ? ["apiKey", "authToken", "baseURL", "providerOptions", "provider"]
            : ["apiKey", "baseURL", "providerOptions", "provider"]);
        if (credential is not null)
        {
            // Upstream projects key metadata into the request body, then overlays
            // settings shallowly: model settings < metadata < form configuration.
            if (credential.Value.TryGetProperty("metadata", out var metadata))
            {
                if (!oauth) ConfigLoader.MergeOverlay(body, JsonNode.Parse(metadata.GetRawText())!.AsObject());
                foreach (var property in metadata.EnumerateObject()) settings[property.Name] = JsonNode.Parse(property.Value.GetRawText());
            }
            if (!oauth && credential.Value.TryGetProperty("configuration", out var configuration))
                foreach (var property in configuration.EnumerateObject()) settings[property.Name] = JsonNode.Parse(property.Value.GetRawText());
        }
        if (connectionKey is not null)
        {
            // Anthropic uses authToken for OAuth; key credentials remain apiKey.
            settings.Remove("accessToken");
            settings.Remove("authToken");
            settings.Remove("apiKey");
            settings[anthropic && oauth ? "authToken" : "apiKey"] = connectionKey;
        }

        JsonObject providerOptions;
        if (package.StartsWith("aisdk:", StringComparison.Ordinal))
        {
            // Select the options actually consumed by the supported native protocols.
            // Integration metadata (server, account, org) must not leak into this map.
            providerOptions = new JsonObject();
            foreach (var name in responses ? ResponsesRequestLowering.OptionNames : anthropic ? AnthropicRequestLowering.OptionNames
                : google ? ["thinkingConfig", "cachedContent", "safetySettings", "serviceTier"] : new[] { "reasoningEffort", "store" })
                if (settings[name] is { } value) providerOptions[name] = value.DeepClone();
            if (google && providerOptions["thinkingConfig"] is JsonObject { Count: 0 })
                providerOptions.Remove("thinkingConfig");
        }
        else
        {
            if (!responses && !nativeChat && settings.ContainsKey("provider") && (google || StringSetting(settings, "provider") != selection.Provider))
                throw new NotSupportedException("Provider settings must preserve the selected provider identity.");
            providerOptions = settings["providerOptions"] is { } options
                ? options.DeepClone() as JsonObject ?? throw new JsonException("providerOptions must be an object.")
                : new JsonObject();
        }
        if (responses || nativeChat) LlmHttp.RequireFields(providerOptions, ResponsesRequestLowering.OptionNames);
        else if (anthropic) LlmHttp.RequireFields(providerOptions, AnthropicRequestLowering.OptionNames);
        else if (google) LlmHttp.RequireFields(providerOptions, "thinkingConfig", "cachedContent", "safetySettings", "serviceTier");
        else LlmHttp.RequireFields(providerOptions, "reasoningEffort", "store");

        var modelId = model.ModelId ?? selection.Model;
        if (string.IsNullOrEmpty(modelId)) throw new InvalidOperationException("Selected API model ID must not be empty.");
        var baseUrl = StringSetting(settings, "baseURL");
        if (baseUrl is not null)
            baseUrl = Regex.Replace(baseUrl, @"\$\{([^}]+)\}", match => Environment.GetEnvironmentVariable(match.Groups[1].Value) ?? match.Value);
        if (chat && string.IsNullOrWhiteSpace(baseUrl))
            throw new InvalidOperationException("An OpenAI-compatible provider requires settings.baseURL.");
        if (package == "@opencode-ai/ai/providers/anthropic-compatible" && string.IsNullOrWhiteSpace(baseUrl))
            throw new LlmException(new LlmFailure.InvalidRequest("An Anthropic-compatible provider requires settings.baseURL."));
        if (baseUrl is not null && (baseUrl.Contains("${", StringComparison.Ordinal)
            || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var endpoint)
            || endpoint.Scheme is not ("http" or "https") || endpoint.Query.Length > 0 || endpoint.Fragment.Length > 0))
            throw new NotSupportedException("The configured endpoint must be an absolute HTTP(S) URL without query, fragment, or unresolved variables.");
        if (chatGpt)
        {
            // The source session hook redirects the public OpenAI origin. Do not
            // forward this account's OAuth material to any configured third-party origin.
            if (baseUrl is null || new Uri(baseUrl).GetLeftPart(UriPartial.Authority) == "https://api.openai.com") baseUrl = OpenAiOAuthService.BackendBaseUrl;
            if (!string.Equals(baseUrl.TrimEnd('/'), OpenAiOAuthService.BackendBaseUrl, StringComparison.Ordinal))
                throw new LlmException(new LlmFailure.Unsupported("ChatGPT OAuth credentials are restricted to the source backend endpoint."));
            if (!OpenAiOAuthService.Eligible(model.ModelId ?? selection.Model, body))
                throw new LlmException(new LlmFailure.InvalidRequest("The exact model/request is excluded by the source ChatGPT eligibility policy."));
        }
        if (!chatGpt && (responses || nativeChat) && baseUrl is not null && Uri.TryCreate(baseUrl, UriKind.Absolute, out var apiEndpoint)
            && apiEndpoint.Host.Equals("chatgpt.com", StringComparison.OrdinalIgnoreCase)
            && apiEndpoint.AbsolutePath.StartsWith("/backend-api/", StringComparison.Ordinal))
            throw new LlmException(new LlmFailure.Unsupported("ChatGPT backend routing and OAuth refresh require a separate integration; API transports are not a substitute."));

        var key = StringSetting(settings, "apiKey");
        if (key == "") key = null;
        var authToken = anthropic ? StringSetting(settings, "authToken") : null;
        if (key is not null && authToken is not null)
            throw new LlmException(new LlmFailure.InvalidRequest("Anthropic apiKey and authToken cannot be combined."));
        var secret = authToken ?? key;
        if (string.IsNullOrEmpty(secret))
            throw new LlmException(new LlmFailure.Authentication("Selected provider has no usable credential. Configure a channel credential, provider env connection, or explicit authentication setting. Shared auth files and databases are not consulted."));
        if (secret.Contains('\r') || secret.Contains('\n'))
            throw new LlmException(new LlmFailure.Authentication("The selected credential cannot be encoded as an HTTP header."));
        var query = responses || nativeChat ? QuerySettings(settings["queryParams"]) : null;
        var organization = responses || nativeChat ? StringSetting(settings, "organization") : null;
        var project = responses || nativeChat ? StringSetting(settings, "project") : null;
        if (chatGpt && query?.Count > 0)
            throw new LlmException(new LlmFailure.Unsupported("ChatGPT backend query overrides are not implemented."));
        if (chatGpt)
        {
            var scoped = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (organization is not null) scoped["OpenAI-Organization"] = organization;
            if (project is not null) scoped["OpenAI-Project"] = project;
            foreach (var (name, value) in headers) scoped[name] = value;
            headers = scoped;
        }
        ILlmClient transport = chatGpt
            ? new OpenAiResponsesLlmClient(credential!, headers, body, providerOptions, sessionId)
            : responses
            ? new OpenAiResponsesLlmClient(_http, secret, baseUrl, headers, body, providerOptions, organization, project, query)
            : nativeChat ? new OpenAiLlmClient(_http, secret, baseUrl, headers, body, providerOptions, query, true, organization, project)
            : anthropic
            ? new AnthropicLlmClient(_http, key, authToken, baseUrl, headers, body, providerOptions, selection.Provider)
            : google ? new GoogleLlmClient(_http, secret, baseUrl, extraHeaders: headers, body: body, providerOptions: providerOptions)
            : new OpenAiLlmClient(_http, secret, baseUrl, extraHeaders: headers, body: body, providerOptions: providerOptions);
        var generationConfig = google && body["generationConfig"] is { } generation
            ? generation is JsonObject ? (JsonElement?)JsonSerializer.SerializeToElement(generation)
                : throw new JsonException("generationConfig must be an object.")
            : null;
        return new ResolvedModel(new SelectedProviderClient(selection.Provider, modelId, transport, compatibility, google), modelId, generationConfig)
        {
            Selection = new ModelRef(selection.Provider, selection.Model, selectedVariant)
        };
    }

    private static IReadOnlyDictionary<string, string>? QuerySettings(JsonNode? node)
    {
        if (node is null) return null;
        if (node is not JsonObject fields) throw new LlmException(new LlmFailure.InvalidRequest("Provider queryParams must be an object."));
        return fields.ToDictionary(pair => pair.Key, pair => pair.Value is JsonValue value && value.TryGetValue<string>(out var text)
            ? text : throw new LlmException(new LlmFailure.InvalidRequest("Provider query parameter values must be strings.")), StringComparer.Ordinal);
    }

    private static LlmCompatibility ReadCompatibility(JsonElement? value)
    {
        if (value is null) return LlmCompatibility.Default;
        if (value.Value.ValueKind != JsonValueKind.Object)
            throw new LlmException(new LlmFailure.InvalidRequest("Model compatibility must be an object."));
        LlmHttp.RequireFields(JsonNode.Parse(value.Value.GetRawText())!.AsObject(), "reasoningField", "requireReasoning",
            "maxTokensField", "requireFinishReason", "requireAssistantAfterTool", "supportsStore", "supportsUsageInStreaming", "supportsStrictMode", "requireSignature");
        var compatibility = value.Value.Deserialize<LlmCompatibility>(new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })!;
        // OpenAI Chat requires replayed reasoning when a field is configured unless
        // requireReasoning explicitly disables that behavior.
        return compatibility.ReasoningField is not null && !value.Value.TryGetProperty("requireReasoning", out _)
            ? compatibility with { RequireReasoning = true } : compatibility;
    }

    private static (string Provider, string Model, string? Variant) ParseSelection(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (!value.TryGetProperty("providerID", out var provider) || provider.ValueKind != JsonValueKind.String
                || !value.TryGetProperty("model", out var model) || model.ValueKind != JsonValueKind.String)
                throw new JsonException("Model selection requires string providerID and model fields.");
            var variant = value.TryGetProperty("variant", out var item)
                ? item.ValueKind == JsonValueKind.String ? item.GetString() : throw new JsonException("Variant must be a string.")
                : null;
            if (string.IsNullOrEmpty(provider.GetString()) || provider.GetString()!.Contains('/') || provider.GetString()!.Contains('#')
                || string.IsNullOrEmpty(model.GetString()) || model.GetString()!.Contains('#')
                || (variant is not null && (variant.Length == 0 || variant.Contains('#'))))
                throw new JsonException("Invalid model selection.");
            return (provider.GetString()!, model.GetString()!, variant);
        }
        if (value.ValueKind != JsonValueKind.String) throw new JsonException("Model selection must be a string or object.");
        var text = value.GetString()!;
        var slash = text.IndexOf('/');
        var hash = text.IndexOf('#');
        if (slash <= 0 || (hash >= 0 && hash <= slash) || slash == text.Length - 1
            || (hash >= 0 && (hash == slash + 1 || hash == text.Length - 1 || text.IndexOf('#', hash + 1) >= 0)))
            throw new ArgumentException("Model selection must use provider/model#variant; the variant suffix is optional.");
        return (text[..slash], text[(slash + 1)..(hash < 0 ? text.Length : hash)], hash < 0 ? null : text[(hash + 1)..]);
    }

    private static JsonObject Overlay(params IReadOnlyDictionary<string, JsonElement>?[] sources)
    {
        var result = new JsonObject();
        foreach (var source in sources)
            if (source is not null)
                ConfigLoader.MergeOverlay(result, JsonSerializer.SerializeToNode(source)!.AsObject());
        return result;
    }

    private static string? StringSetting(JsonObject settings, string name) => settings[name] is { } value
        ? value is JsonValue scalar && scalar.TryGetValue<string>(out var text)
            ? text : throw new JsonException("Provider authentication and endpoint settings must be strings.")
        : null;

    private sealed class SelectedProviderClient(string providerId, string apiModelId, ILlmClient transport,
        LlmCompatibility compatibility, bool google) : ILlmClient
    {
        public string ProviderId => providerId;
        public string ProviderMetadataKey => transport.ProviderMetadataKey;

        public IAsyncEnumerable<LlmEvent> StreamAsync(LlmRequest request, CancellationToken ct = default)
        {
            if (request.ModelId != apiModelId)
                throw new LlmException(new LlmFailure.InvalidRequest("Request model ID differs from the resolved selection."));
            return transport.StreamAsync(ReferenceEquals(request.Compatibility, LlmCompatibility.Default)
                ? request with { Compatibility = compatibility } : request, ct);
        }

        public IAsyncEnumerable<string> StreamChatAsync(IReadOnlyList<LlmChatMessage> messages, string modelId,
            object? generationConfig = null, CancellationToken ct = default) =>
            LlmAnswerText.StreamAsync(this, messages, modelId, generationConfig, google, ct);
    }
}
