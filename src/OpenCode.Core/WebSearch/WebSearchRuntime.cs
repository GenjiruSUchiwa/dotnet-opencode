namespace OpenCode.Core.WebSearch;

using OpenCode.Schema;

/// <summary>Location-owned providers and config selection. Captured registrations retain their real producer executors.</summary>
public sealed class WebSearchRuntime(IWebSearchSelectionStore? selections = null, Action? updated = null)
{
    private readonly Lock _gate = new();
    private readonly List<Registration> _registrations = [];
    private WebSearchSelection? _configured;
    public bool CanSelect => selections is not null;

    public IDisposable Register(IEnumerable<IWebSearchProvider> providers)
    {
        var entries = providers.ToArray();
        if (entries.Any(provider => provider is null || provider.Info.Id is null || provider.Info.Name is null))
            throw new ArgumentException("Web search registrations require complete provider definitions.", nameof(providers));
        var registration = new Registration(this, entries);
        lock (_gate) _registrations.Add(registration);
        updated?.Invoke();
        return registration;
    }

    /// <summary>The config observer supplies its latest effective value. Config overrides stored user selection.</summary>
    public void Configure(ConfigWebSearchSelection? configuration)
    {
        lock (_gate) _configured = configuration switch
        {
            ConfigWebSearchSelection.Disabled => new(Disabled: true),
            ConfigWebSearchInfo info => new(info.Provider), null => null,
            _ => throw new ArgumentException("Unsupported websearch configuration.", nameof(configuration))
        };
        updated?.Invoke();
    }

    public IReadOnlyList<WebSearchProvider> Providers() => Snapshot().Values.Select(provider => provider.Info)
        .OrderBy(provider => provider.Name, StringComparer.CurrentCulture).ToArray();

    public async Task<IReadOnlyList<WebSearchProvider>> AvailableProvidersAsync(CancellationToken ct)
    {
        var available = new List<WebSearchProvider>();
        foreach (var provider in Snapshot().Values)
            if (await provider.AvailableAsync(ct).ConfigureAwait(true)) available.Add(provider.Info);
        return available.OrderBy(provider => provider.Name, StringComparer.CurrentCulture).ToArray();
    }

    public async Task<WebSearchProvider?> DefaultAsync(CancellationToken ct)
    {
        WebSearchSelection? configured;
        lock (_gate) configured = _configured;
        var selection = configured ?? (selections is not null ? await selections.ReadAsync(ct).ConfigureAwait(true) : null);
        if (selection?.Disabled == true) throw new WebSearchException(WebSearchFailure.Disabled, "Web search is disabled");
        var providers = Snapshot();
        if (selection?.ProviderId == "random")
        {
            // The native host registers every implemented backend, but unlike source keyless
            // adapters only credentialed ones are usable. Do not randomly advertise or select
            // an unavailable backend; explicit provider IDs still fail rather than switching.
            var available = await AvailableProvidersAsync(ct).ConfigureAwait(true);
            if (available.Count == 0) throw new WebSearchException(WebSearchFailure.Unavailable, "No configured web search backend has a usable credential.");
            return available[Random.Shared.Next(available.Count)];
        }
        return selection?.ProviderId is { Length: > 0 } id ? providers.GetValueOrDefault(id)?.Info : null;
    }

    public Task SelectAsync(WebSearchSelection selection, CancellationToken ct) => selections?.SaveAsync(selection, ct)
        ?? throw new WebSearchException(WebSearchFailure.Unavailable, "Web search provider selection persistence is not configured.");

    public async Task<bool> CanExecuteAsync(string providerId, CancellationToken ct) =>
        Snapshot().TryGetValue(providerId, out var provider) && await provider.AvailableAsync(ct).ConfigureAwait(true);

    public async Task<WebSearchResponse> QueryAsync(WebSearchInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input.Query);
        var id = !string.IsNullOrEmpty(input.ProviderId) ? input.ProviderId : (await DefaultAsync(ct).ConfigureAwait(true))?.Id
            ?? throw new WebSearchException(WebSearchFailure.ProviderRequired, "Web search provider is required");
        if (!Snapshot().TryGetValue(id, out var provider))
            throw new WebSearchException(WebSearchFailure.ProviderNotFound, $"Web search provider not found: {id}", id);
        if (!await provider.AvailableAsync(ct).ConfigureAwait(true))
            throw new WebSearchException(WebSearchFailure.Unavailable, $"Web search credential or backend is unavailable: {id}", id);
        var response = new WebSearchResponse(id, await provider.ExecuteAsync(input.Query, ct).ConfigureAwait(true));
        try { response.Validate(); }
        catch (System.Text.Json.JsonException) { throw new WebSearchException(WebSearchFailure.Request, $"Invalid web search response: {id}", id); }
        return response;
    }

    private Dictionary<string, IWebSearchProvider> Snapshot()
    {
        lock (_gate)
        {
            var providers = new Dictionary<string, IWebSearchProvider>(StringComparer.Ordinal);
            foreach (var provider in _registrations.SelectMany(registration => registration.Providers)) providers[provider.Info.Id] = provider;
            return providers;
        }
    }

    private void Notify() => updated?.Invoke();

    private sealed class Registration(WebSearchRuntime owner, IReadOnlyList<IWebSearchProvider> providers) : IDisposable
    {
        public IReadOnlyList<IWebSearchProvider> Providers { get; } = providers;
        public void Dispose()
        {
            lock (owner._gate) if (!owner._registrations.Remove(this)) return;
            owner.Notify();
        }
    }
}
