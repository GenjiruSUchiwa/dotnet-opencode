namespace OpenCode.Core.Llm;

using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using OpenCode.Core.Config;
using OpenCode.Core.Database;
using OpenCode.Schema;

internal sealed record CatalogProviderInfo(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("activation")] ProviderActivation Activation,
    [property: JsonPropertyName("package")] string Package,
    [property: JsonPropertyName("available")] bool Available,
    [property: JsonPropertyName("credentialConfigured")] bool CredentialConfigured,
    [property: JsonPropertyName("hasStoredCredential")] bool HasStoredCredential,
    [property: JsonPropertyName("hasEnvironmentCredential")] bool HasEnvironmentCredential,
    [property: JsonPropertyName("integrationID"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? IntegrationId = null);

internal sealed record CatalogCapabilities(
    [property: JsonPropertyName("tools"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Tools,
    [property: JsonPropertyName("input"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Input,
    [property: JsonPropertyName("output"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Output,
    [property: JsonPropertyName("responsesWebsockets"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? ResponsesWebsockets);

internal sealed record CatalogLimit(
    [property: JsonPropertyName("context"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Context,
    [property: JsonPropertyName("output"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Output,
    [property: JsonPropertyName("input"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Input);
internal sealed record CatalogCacheCost([property: JsonPropertyName("read")] double Read, [property: JsonPropertyName("write")] double Write);
internal sealed record CatalogCostTier([property: JsonPropertyName("type")] string Type, [property: JsonPropertyName("size")] double Size);
internal sealed record CatalogCost(
    [property: JsonPropertyName("input")] double Input, [property: JsonPropertyName("output")] double Output,
    [property: JsonPropertyName("cache")] CatalogCacheCost Cache,
    [property: JsonPropertyName("tier"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CatalogCostTier? Tier = null);
internal sealed record CatalogTime([property: JsonPropertyName("released")] double Released);
internal sealed record CatalogVariantInfo([property: JsonPropertyName("id")] string Id);

/// <summary>Sanitized model metadata. TransportSupported identifies an implemented protocol, not a successful auth/request check.</summary>
internal sealed record CatalogModelInfo(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("modelID")] string ModelId,
    [property: JsonPropertyName("providerID")] string ProviderId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("available")] bool Available,
    [property: JsonPropertyName("transportSupported")] bool TransportSupported,
    [property: JsonPropertyName("credentialConfigured")] bool CredentialConfigured)
{
    [JsonPropertyName("package"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Package { get; init; }
    [JsonPropertyName("family"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Family { get; init; }
    [JsonPropertyName("capabilities"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CatalogCapabilities? Capabilities { get; init; }
    [JsonPropertyName("limit"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CatalogLimit? Limit { get; init; }
    [JsonPropertyName("compatibility"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ImmutableDictionary<string, JsonElement>? Compatibility { get; init; }
    [JsonPropertyName("cost")]
    public ImmutableArray<CatalogCost> Cost { get; init; } = [];
    [JsonPropertyName("variants")]
    public ImmutableArray<CatalogVariantInfo> Variants { get; init; } = [];
    [JsonPropertyName("time")]
    public CatalogTime Time { get; init; } = new(0);
    [JsonPropertyName("status")]
    public string Status { get; init; } = "active";
}

internal sealed record ResolvedCatalog(
    string Directory, ImmutableArray<CatalogProviderInfo> Providers, ImmutableArray<CatalogModelInfo> Models,
    ModelRef? ConfiguredDefault, CatalogModelInfo? DefaultModel, ModelRef? DefaultSelection,
    string DefaultSource, bool ConfiguredDefaultAvailable)
{
    public ImmutableArray<CatalogProviderInfo> AvailableProviders => Providers.Where(provider => provider.Available).ToImmutableArray();
    public ImmutableArray<CatalogModelInfo> AvailableModels => Models.Where(model => model.Available).ToImmutableArray();
    public ImmutableDictionary<string, string> IntegrationErrors { get; init; } = ImmutableDictionary<string, string>.Empty;
}

public sealed class CatalogMetadataUnavailableException(string message) : IOException(message);

/// <summary>Canonical shared Schema values; internal availability diagnostics are not protocol fields.</summary>
public sealed record CanonicalCatalog(IReadOnlyList<ModelInfo> Models, IReadOnlyList<ProviderInfo> Providers, ModelInfo? DefaultModel);
public enum CatalogResource { All, Models, Default, Providers }

public sealed partial class ProviderResolver
{
    private sealed record CatalogSnapshot(string Directory, JsonObject Document, ConsoleProviderCatalog? Console,
        IReadOnlySet<string> LocalProviderIds, IReadOnlySet<string> BuiltInProviderIds, StoredCredential? OpenAiCredential,
        Dictionary<string, LlmException> IntegrationErrors);

    /// <summary>Reads the same console/local transform used for resolution, without preparing or invoking a model.</summary>
    internal async Task<ResolvedCatalog> ReadCatalogAsync(string? directory = null, CancellationToken ct = default) =>
        await ProjectCatalogAsync(await LoadCatalogSnapshotAsync(directory, ct).ConfigureAwait(false), ct).ConfigureAwait(false);

    public async Task<CanonicalCatalog> ReadCanonicalCatalogAsync(string? directory = null, CancellationToken ct = default,
        CatalogResource resource = CatalogResource.All)
    {
        var snapshot = await LoadCatalogSnapshotAsync(directory, ct).ConfigureAwait(false);
        var catalog = await ProjectCatalogAsync(snapshot, ct).ConfigureAwait(false);
        var models = new List<ModelInfo>();
        ModelInfo? defaultModel = null;
        var selectedModels = resource == CatalogResource.Providers ? ImmutableArray<CatalogModelInfo>.Empty
            : resource == CatalogResource.Default ? catalog.DefaultModel is { } selected ? [selected] : [] : catalog.AvailableModels;
        foreach (var model in selectedModels)
        {
            var provider = snapshot.Document["providers"]![model.ProviderId]!.AsObject();
            var definition = provider["models"]![model.Id]!.AsObject();
            if (model.Capabilities?.Tools is not { } tools || model.Capabilities.Input is null || model.Capabilities.Output is null
                || model.Limit?.Context is not { } context || model.Limit.Output is not { } output)
                throw new CatalogMetadataUnavailableException("Available model metadata is incomplete: canonical capabilities and context/output limits are required.");
            var capabilities = new JsonObject
            {
                ["tools"] = tools,
                ["input"] = JsonSerializer.SerializeToNode(model.Capabilities.Input),
                ["output"] = JsonSerializer.SerializeToNode(model.Capabilities.Output)
            };
            if (model.Capabilities.ResponsesWebsockets is { } sockets) capabilities["responsesWebsockets"] = sockets;
            var limit = new JsonObject { ["context"] = context, ["output"] = output };
            if (model.Limit.Input is { } input) limit["input"] = input;
            var settings = provider["settings"]?.DeepClone() as JsonObject ?? new JsonObject();
            if (definition["settings"] is JsonObject modelSettings) ConfigLoader.MergeOverlay(settings, modelSettings);
            var variants = new JsonArray();
            if (definition["variants"] is JsonArray definitions)
                foreach (var variant in definitions)
                    variants.Add(new JsonObject { ["id"] = variant!["id"]!.GetValue<string>(), ["settings"] = SanitizeCatalogSettings(variant["settings"]) });
            var info = new JsonObject
            {
                ["id"] = model.Id, ["modelID"] = model.ModelId, ["providerID"] = model.ProviderId, ["name"] = model.Name,
                ["capabilities"] = capabilities, ["limit"] = limit, ["variants"] = variants,
                ["time"] = JsonSerializer.SerializeToNode(model.Time), ["cost"] = JsonSerializer.SerializeToNode(model.Cost),
                ["status"] = model.Status, ["enabled"] = model.Enabled, ["settings"] = SanitizeCatalogSettings(settings)
            };
            if (model.Package is not null) info["package"] = model.Package;
            if (model.Family is not null) info["family"] = model.Family;
            if (model.Compatibility is { } compatibility)
            {
                var safe = new JsonObject();
                foreach (var name in new[] { "reasoningField", "maxTokensField", "requireReasoning", "requireFinishReason", "requireAssistantAfterTool" })
                    if (compatibility.TryGetValue(name, out var value))
                    {
                        if (name == "maxTokensField" && value.GetString() is not ("max_tokens" or "max_completion_tokens"))
                            throw new CatalogMetadataUnavailableException("Model compatibility is not a canonical contract value.");
                        safe[name] = JsonNode.Parse(value.GetRawText());
                    }
                info["compatibility"] = safe;
            }
            var shared = info.Deserialize(OpenCodeJsonContext.Default.ModelInfo)
                ?? throw new CatalogMetadataUnavailableException("Canonical Model.Info could not be constructed.");
            if (catalog.DefaultModel?.ProviderId == model.ProviderId && catalog.DefaultModel.Id == model.Id) defaultModel = shared;
            models.Add(shared);
        }
        var providers = new List<ProviderInfo>();
        foreach (var provider in resource is CatalogResource.All or CatalogResource.Providers
            ? catalog.AvailableProviders : ImmutableArray<CatalogProviderInfo>.Empty)
        {
            var info = new JsonObject
            {
                ["id"] = provider.Id, ["name"] = provider.Name, ["package"] = provider.Package,
                ["activation"] = provider.Activation.ToString().ToLowerInvariant(),
                ["settings"] = SanitizeCatalogSettings(snapshot.Document["providers"]![provider.Id]!["settings"])
            };
            if (provider.IntegrationId is not null) info["integrationID"] = provider.IntegrationId;
            var shared = info.Deserialize(OpenCodeJsonContext.Default.ProviderInfo)
                ?? throw new CatalogMetadataUnavailableException("Canonical Provider.Info could not be constructed.");
            providers.Add(shared);
        }
        return new CanonicalCatalog(models, providers, defaultModel);
    }

    private static JsonObject SanitizeCatalogSettings(JsonNode? source)
    {
        var result = new JsonObject();
        if (source is not JsonObject settings) return result;
        // Only known non-credential option fields cross the protocol boundary.
        // Endpoints, arbitrary bodies and all request headers remain private.
        foreach (var name in new[] { "reasoningEffort", "reasoningSummary", "textVerbosity", "serviceTier", "effort", "service_tier", "truncation" })
            if (settings[name] is JsonValue value && value.TryGetValue<string>(out var text)) result[name] = text;
        foreach (var name in new[] { "store", "disabled", "parallelToolCalls" })
            if (settings[name] is JsonValue value && value.TryGetValue<bool>(out var flag)) result[name] = flag;
        if (settings["include"] is JsonArray includedFields && includedFields.All(value => value is JsonValue scalar && scalar.TryGetValue<string>(out _)))
            result["include"] = includedFields.DeepClone();
        if (settings["thinkingConfig"] is JsonObject thinking)
        {
            var projected = new JsonObject();
            if (thinking["thinkingBudget"] is JsonValue budget && budget.GetValueKind() == JsonValueKind.Number)
                projected["thinkingBudget"] = CatalogNumbers.Finite(budget);
            if (thinking["thinkingLevel"] is JsonValue level && level.TryGetValue<string>(out var text)) projected["thinkingLevel"] = text;
            if (thinking["includeThoughts"] is JsonValue include && include.TryGetValue<bool>(out var flag)) projected["includeThoughts"] = flag;
            result["thinkingConfig"] = projected;
        }
        if (settings["thinking"] is JsonObject anthropicThinking)
        {
            var projected = new JsonObject();
            foreach (var name in new[] { "type", "display" })
                if (anthropicThinking[name] is JsonValue value && value.TryGetValue<string>(out var text)) projected[name] = text;
            foreach (var name in new[] { "budgetTokens", "budget_tokens" })
                if (anthropicThinking[name] is JsonValue value && value.GetValueKind() == JsonValueKind.Number)
                    projected[name] = CatalogNumbers.Finite(value);
            result["thinking"] = projected;
        }
        if (settings["providerOptions"] is JsonObject options)
        {
            var leaf = options.DeepClone().AsObject();
            leaf.Remove("providerOptions");
            result["providerOptions"] = SanitizeCatalogSettings(leaf);
        }
        return result;
    }

    public static bool SupportsPackage(string? package) => package is "aisdk:@ai-sdk/google" or "@opencode-ai/ai/providers/google"
        or "aisdk:@ai-sdk/openai-compatible" or "@opencode-ai/ai/providers/openai-compatible"
        or "aisdk:@ai-sdk/anthropic" or "@opencode-ai/ai/providers/anthropic" or "@opencode-ai/ai/providers/anthropic-compatible"
        or "aisdk:@ai-sdk/openai" or "@opencode-ai/ai/providers/openai" or "@opencode-ai/ai/providers/openai/responses"
        or "@opencode-ai/ai/providers/openai/chat";

    private async Task<CatalogSnapshot> LoadCatalogSnapshotAsync(string? directory, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var path = Path.GetFullPath(directory ?? System.IO.Directory.GetCurrentDirectory());
        var document = ConfigLoader.LoadDocument(directory: path);
        var errors = new Dictionary<string, LlmException>(StringComparer.Ordinal);
        var console = await ObserveIntegrationAsync(ConsoleIntegrationService.IntegrationId,
            () => _console.DiscoverProvidersAsync(ct), errors, ct).ConfigureAwait(false);
        var providers = ModelsDevCatalog.Load();
        // The built-in Anthropic plugin runs after models-dev and before console/config.
        foreach (var provider in providers.Select(pair => pair.Value!.AsObject()))
            if (provider["package"]?.GetValue<string>() == "aisdk:@ai-sdk/anthropic")
            {
                provider["headers"] ??= new JsonObject();
                provider["headers"]!["anthropic-beta"] = "interleaved-thinking-2025-05-14,fine-grained-tool-streaming-2025-05-14";
            }
        var builtIn = providers.Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal);
        if (console?.ProviderDocument is { } remote) ConfigLoader.MergeProviders(providers, remote);
        var openAiCredential = await ObserveIntegrationAsync(OpenAiOAuthService.IntegrationId,
            () => _openAi.ResolveCredentialAsync(ct), errors, ct).ConfigureAwait(false);
        if (providers[OpenAiOAuthService.IntegrationId] is JsonObject openAi)
        {
            if (openAi["models"] is JsonObject models)
                foreach (var model in models.Select(pair => pair.Value!.AsObject()))
                {
                    model["capabilities"] ??= new JsonObject();
                    model["capabilities"]!["responsesWebsockets"] = true;
                }
            // Console provenance remains authoritative if it owns this provider ID;
            // never combine a console catalog binding with another account's OAuth headers.
            if (console?.Providers.ContainsKey(OpenAiOAuthService.IntegrationId) != true
                && openAiCredential is not null && OpenAiOAuthService.IsChatGptCredential(openAiCredential))
                OpenAiOAuthService.ApplyCatalog(openAi);
        }
        var local = new HashSet<string>(StringComparer.Ordinal);
        if (document["providers"] is JsonObject configured)
        {
            foreach (var (id, node) in configured)
            {
                local.Add(id);
                var provider = Pick(node?.AsObject() ?? throw new JsonException("Provider must be an object."),
                    "name", "env", "package", "settings", "headers", "body", "options", "api", "npm");
                if (node!["models"] is JsonObject models)
                {
                    var selected = new JsonObject();
                    foreach (var (modelId, model) in models)
                    {
                        selected[modelId] = Pick(model?.AsObject() ?? throw new JsonException("Model must be an object."),
                            "modelID", "family", "name", "compatibility", "package", "settings", "headers", "body",
                            "capabilities", "variants", "cost", "disabled", "limit", "options", "id");
                        // Catalog.model.update starts a missing model with the
                        // canonical defaults; it does not require models-dev metadata.
                        providers[id] ??= new JsonObject();
                        providers[id]!["models"] ??= new JsonObject();
                        providers[id]!["models"]![modelId] ??= JsonSerializer.SerializeToNode(
                            ModelInfo.CreateDefault(ProviderId.FromExisting(id), ModelId.FromExisting(modelId)));
                    }
                    provider["models"] = selected;
                }
                ConfigLoader.MergeProviders(providers, new JsonObject { [id] = provider }, configured: true);
            }
        }
        if (document["experimental"]?["policies"] is JsonArray policies)
            foreach (var id in providers.Select(pair => pair.Key).ToArray())
            {
                var matched = policies.LastOrDefault(policy => MatchProviderPolicy(id, policy!["resource"]!.GetValue<string>()));
                if (matched?["effect"]?.GetValue<string>() == "deny") providers.Remove(id);
            }
        document["providers"] = providers;
        return new CatalogSnapshot(path, document, console, local, builtIn, openAiCredential, errors);
    }

    // Plugin discovery is optional; selected runtime resolution rethrows the
    // retained failure. Never retain a previous account's catalog or credentials.
    private static async Task<T?> ObserveIntegrationAsync<T>(string integrationId, Func<Task<T?>> load,
        Dictionary<string, LlmException> errors, CancellationToken ct) where T : class
    {
        try { return await load().ConfigureAwait(false); }
        catch (LlmException error)
        {
            ct.ThrowIfCancellationRequested();
            errors[integrationId] = error;
            return null;
        }
        catch (InvalidDataException)
        {
            ct.ThrowIfCancellationRequested();
            errors[integrationId] = new LlmException(new LlmFailure.Authentication("The channel contains an invalid integration credential."));
            return null;
        }
        catch (Exception error) when (error is HttpRequestException or IOException or JsonException
            || error is OperationCanceledException && !ct.IsCancellationRequested)
        {
            ct.ThrowIfCancellationRequested();
            errors[integrationId] = new LlmException(new LlmFailure.Transport("Integration discovery or credential resolution failed."), error);
            return null;
        }
    }

    private async Task<ResolvedCatalog> ProjectCatalogAsync(CatalogSnapshot snapshot, CancellationToken ct)
    {
        // Neither these records nor the endpoint response can carry settings, headers,
        // body overlays, account labels, or credential values.
        var stored = new Dictionary<string, StoredCredential?>(StringComparer.Ordinal);
        var providers = ImmutableArray.CreateBuilder<CatalogProviderInfo>();
        var models = new List<CatalogModelInfo>();
        foreach (var (id, node) in snapshot.Document["providers"]!.AsObject())
        {
            ct.ThrowIfCancellationRequested();
            var provider = node!.AsObject();
            var consoleBound = snapshot.Console?.Providers.ContainsKey(id) == true;
            var integrationId = consoleBound ? ConsoleIntegrationService.IntegrationId : id;
            if (!stored.ContainsKey(integrationId))
                stored[integrationId] = snapshot.IntegrationErrors.ContainsKey(integrationId) ? null
                    : consoleBound ? snapshot.Console!.Credential
                    : integrationId == OpenAiOAuthService.IntegrationId ? snapshot.OpenAiCredential
                    : await ObserveIntegrationAsync(integrationId, () => _credentialStore.GetActiveCredentialAsync(integrationId, ct), snapshot.IntegrationErrors, ct).ConfigureAwait(false);
            var hasStored = stored[integrationId] is not null;
            var hasEnvironment = !consoleBound && Strings(provider["env"])?.Any(name => name.Length > 0 && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name))) == true;
            var activation = snapshot.LocalProviderIds.Contains(id) ? ProviderActivation.Enabled : ProviderActivation.Auto;
            var available = !snapshot.IntegrationErrors.ContainsKey(integrationId)
                && (activation == ProviderActivation.Enabled || activation != ProviderActivation.Disabled && (hasStored || hasEnvironment));
            var credentialConfigured = hasStored || hasEnvironment || HasInlineAuth(provider["settings"]);
            var package = Text(provider["package"]) ?? "";
            providers.Add(new CatalogProviderInfo(id, Text(provider["name"]) ?? id, activation, package, available,
                credentialConfigured, hasStored, hasEnvironment, consoleBound || snapshot.BuiltInProviderIds.Contains(id) ? integrationId : null));
            if (provider["models"] is not JsonObject definitions) continue;
            foreach (var (modelId, definition) in definitions)
            {
                var model = definition!.AsObject();
                var modelPackage = Text(model["package"]) ?? (package.Length == 0 ? null : package);
                var enabled = model["disabled"]?.GetValue<bool>() != true;
                var status = Text(model["status"]) ?? "active";
                if (status is not ("alpha" or "beta" or "deprecated" or "active")) throw new JsonException("Invalid catalog model status.");
                models.Add(new CatalogModelInfo(modelId, Text(model["modelID"]) ?? modelId, id, Text(model["name"]) ?? modelId,
                    enabled, available && enabled, SupportsPackage(modelPackage), hasStored || hasEnvironment || HasInlineAuth(provider["settings"], model["settings"]))
                {
                    Package = modelPackage,
                    Family = Text(model["family"]),
                    Status = status,
                    Time = new CatalogTime(Finite(model["time"]?["released"]) ?? 0),
                    Capabilities = model["capabilities"] is JsonObject capabilities ? new CatalogCapabilities(
                        capabilities["tools"]?.GetValue<bool>(), Strings(capabilities["input"]), Strings(capabilities["output"]),
                        capabilities["responsesWebsockets"]?.GetValue<bool>()) : null,
                    Limit = model["limit"] is JsonObject limit ? new CatalogLimit(
                        limit["context"] is { } context ? CatalogNumbers.Integer(context) : null,
                        limit["output"] is { } output ? CatalogNumbers.Integer(output) : null,
                        limit["input"] is { } input ? CatalogNumbers.Integer(input) : null) : null,
                    Compatibility = model["compatibility"] is JsonObject compatibility ? SafeCompatibility(compatibility) : null,
                    Cost = Costs(model["cost"]),
                    Variants = model["variants"] is JsonArray variants ? variants.Select(variant => new CatalogVariantInfo(
                        Text(variant?["id"]) ?? throw new JsonException("Variant requires an ID."))).ToImmutableArray() : []
                });
            }
        }
        var ordered = models.OrderByDescending(model => model.Time.Released).ToImmutableArray();
        var configuredDefault = snapshot.Document["model"] is { } configured
            ? ParseSelection(JsonSerializer.SerializeToElement(configured)) : default;
        var configuredRef = configuredDefault.Provider is null ? null : new ModelRef(configuredDefault.Provider, configuredDefault.Model, configuredDefault.Variant);
        var preferred = configuredRef is null ? null : ordered.FirstOrDefault(model => model.Available
            && model.ProviderId == configuredRef.ProviderId && model.Id == configuredRef.Id);
        var selected = preferred ?? ordered.FirstOrDefault(model => model.Available);
        var defaultSelection = selected is null ? null : new ModelRef(selected.ProviderId, selected.Id,
            preferred is null ? null : configuredRef?.Variant);
        var configuredAvailable = preferred is not null && (configuredRef?.Variant is null or "default"
            || preferred.Variants.Any(variant => variant.Id == configuredRef.Variant));
        return new ResolvedCatalog(snapshot.Directory, providers.ToImmutable(), ordered, configuredRef, selected, defaultSelection,
            preferred is not null ? "configured" : selected is not null ? "first-available" : "none", configuredAvailable)
        {
            IntegrationErrors = snapshot.IntegrationErrors.ToImmutableDictionary(pair => pair.Key,
                pair => pair.Value.Reason.GetType().Name, StringComparer.Ordinal)
        };
    }

    private static JsonObject Pick(JsonObject value, params string[] names)
    {
        var result = new JsonObject();
        foreach (var name in names) if (value[name] is { } item) result[name] = item.DeepClone();
        return result;
    }

    private static bool MatchProviderPolicy(string id, string pattern)
    {
        var expression = Regex.Escape(pattern.Replace('\\', '/')).Replace("\\ ", " ").Replace("\\*", ".*").Replace("\\?", ".");
        if (expression.EndsWith(" .*", StringComparison.Ordinal)) expression = expression[..^3] + "(?: .*)?";
        return OperatingSystem.IsWindows()
            ? Regex.IsMatch(id.Replace('\\', '/'), "^" + expression + "$", RegexOptions.Singleline | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking | RegexOptions.IgnoreCase)
            : Regex.IsMatch(id.Replace('\\', '/'), "^" + expression + "$", RegexOptions.Singleline | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    }

    private static string? Text(JsonNode? node) => node?.GetValue<string>();
    private static IReadOnlyList<string>? Strings(JsonNode? node) => node is null ? null
        : node.AsArray().Select(value => value?.GetValue<string>() ?? throw new JsonException("Expected a string array.")).ToImmutableArray();
    private static double? Finite(JsonNode? node)
    {
        if (node is null) return null;
        return CatalogNumbers.Finite(node);
    }
    private static bool HasInlineAuth(JsonNode? settings, JsonNode? overlay = null) =>
        new[] { "apiKey", "authToken", "accessToken" }.Any(name =>
        {
            var value = overlay is JsonObject changes && changes.TryGetPropertyValue(name, out var changed) ? changed : settings?[name];
            return value is JsonValue scalar && scalar.TryGetValue<string>(out var text) && text.Length > 0;
        });

    private static ImmutableDictionary<string, JsonElement> SafeCompatibility(JsonObject compatibility)
    {
        var result = ImmutableDictionary.CreateBuilder<string, JsonElement>();
        foreach (var name in new[] { "reasoningField", "maxTokensField", "requireReasoning", "requireFinishReason", "requireAssistantAfterTool",
            "supportsStore", "supportsUsageInStreaming", "supportsStrictMode" })
        {
            if (compatibility[name] is not { } value) continue;
            if (name is "reasoningField" or "maxTokensField") _ = value.GetValue<string>();
            else _ = value.GetValue<bool>();
            result[name] = JsonSerializer.SerializeToElement(value);
        }
        return result.ToImmutable();
    }

    private static ImmutableArray<CatalogCost> Costs(JsonNode? value)
    {
        if (value is null) return [];
        var costs = value is JsonArray array ? array.ToArray() : [value];
        return costs.Select(node =>
        {
            var cost = node?.AsObject() ?? throw new JsonException("Cost must be an object.");
            var tier = cost["tier"] is JsonObject threshold ? new CatalogCostTier(
                Text(threshold["type"]) ?? throw new JsonException("Cost tier requires a type."),
                threshold["size"] is { } size ? CatalogNumbers.Integer(size) : throw new JsonException("Cost tier requires a size.")) : null;
            if (tier is not null && tier.Type != "context") throw new JsonException("Unsupported model cost tier type.");
            return new CatalogCost(Finite(cost["input"]) ?? throw new JsonException("Cost requires input pricing."),
                Finite(cost["output"]) ?? throw new JsonException("Cost requires output pricing."),
                new CatalogCacheCost(Finite(cost["cache"]?["read"]) ?? 0, Finite(cost["cache"]?["write"]) ?? 0), tier);
        }).ToImmutableArray();
    }
}
