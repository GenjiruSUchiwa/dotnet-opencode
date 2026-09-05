namespace OpenCode.Server.Services;

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenCode.Core.Config;
using OpenCode.Core.Database;
using OpenCode.Core.Integrations;
using OpenCode.Core.Locations;
using OpenCode.Core.Plugins;
using OpenCode.Core.WebSearch;
using OpenCode.Schema;

/// <summary>Providers belong to actual native plugin scopes, not HTTP requests or config declarations.</summary>
public sealed class WebSearchPluginSource(CredentialStore credentials, IDatabase database, IEventFeedService feed) : INativePluginSource
{
    private readonly Lock _gate = new();
    private readonly Dictionary<LocationRef, Entry> _entries = [];

    private sealed class Entry(WebSearchRuntime runtime)
    {
        internal WebSearchRuntime Runtime { get; } = runtime;
        internal Dictionary<string, NativeWebSearchProvider> Providers { get; } = new(StringComparer.Ordinal);
        internal SemaphoreSlim Configure { get; } = new(1, 1);
        internal bool Configured;
        internal ConfigWebSearchSelection? Configuration;
    }

    public IReadOnlyList<NativePluginDefinition> Definitions(LocationInfo location)
    {
        var key = PermissionLocationMap.Canonical(new(location.Directory, location.WorkspaceId));
        var entry = new Entry(new WebSearchRuntime(new WebSearchSelectionStore(database), () => feed.Publish(
            new OpenCodeEvent(EventId.Create(), "websearch.updated", database.Clock.GetUtcNow().ToUnixTimeMilliseconds(),
                JsonSerializer.SerializeToElement(new Dictionary<string, string>()), key))));
        // This order is PluginInternal's WebSearchPlugins order. Constructors and
        // registry writes occur inside initialization, not catalog projection.
        return new[] { "exa", "firecrawl", "parallel", "tavily" }.Select(id => new NativePluginDefinition(
            PluginId.FromExisting("opencode.websearch." + id), "native", (scope, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                var provider = scope.Own(new NativeWebSearchProvider(id, credentials, OpenCodeChannel.UserAgent));
                var registration = entry.Runtime.Register([provider]);
                scope.Own(new NativePluginRegistration(registration.Dispose));
                scope.Own(new NativePluginRegistration(() =>
                {
                    lock (_gate)
                    {
                        entry.Providers.Remove(id);
                        if (entry.Providers.Count == 0 && _entries.GetValueOrDefault(key) == entry) _entries.Remove(key);
                    }
                }));
                lock (_gate)
                {
                    entry.Providers.Add(id, provider);
                    _entries[key] = entry;
                }
                return ValueTask.CompletedTask;
            })).ToArray();
    }

    internal IReadOnlyList<IntegrationDefinition> Integrations(LocationInfo location)
    {
        lock (_gate)
            return _entries.GetValueOrDefault(PermissionLocationMap.Canonical(new(location.Directory, location.WorkspaceId)))
                ?.Providers.Values.Select(provider => provider.Integration).ToArray() ?? [];
    }

    /// <summary>
    /// Returns the actual initialized native generation without acquiring/reentering
    /// the tool Location. The caller must own that Location's lease (or be inside
    /// its factory after backend plugin activation) and preserve plugin readiness guards.
    /// Does not create providers, resolve credentials, or advertise a model tool.
    /// </summary>
    public async Task<WebSearchRuntime> ReadyAsync(LocationInfo location, CancellationToken ct)
    {
        var key = PermissionLocationMap.Canonical(new(location.Directory, location.WorkspaceId));
        Entry entry;
        lock (_gate)
            entry = _entries.GetValueOrDefault(key)
                ?? throw new WebSearchException(WebSearchFailure.Unavailable, "Native web search plugins have not initialized.");
        await entry.Configure.WaitAsync(ct);
        try
        {
            var snapshot = await ConfigLoader.LoadSnapshotAsync(location.Directory, ct);
            var configuration = snapshot.Merge()["websearch"]?.Deserialize(OpenCodeJsonContext.Default.ConfigWebSearchSelection);
            // Do not return an old generation if its scope closed/replaced while
            // this acquisition was reading configuration or waiting for serialization.
            lock (_gate)
                if (_entries.GetValueOrDefault(key) != entry)
                    throw new WebSearchException(WebSearchFailure.Unavailable, "Web search plugin generation changed during acquisition.");
            // Configure publishes websearch.updated. Reapplying equal state on every
            // query/snapshot creates a spurious refresh loop for model-tool observers.
            if (!entry.Configured || !Equals(entry.Configuration, configuration))
            {
                entry.Runtime.Configure(configuration);
                entry.Configuration = configuration;
                entry.Configured = true;
            }
            return entry.Runtime;
        }
        finally { entry.Configure.Release(); }
    }
}

/// <summary>Borrow the authoritative tool/plugin Location and its existing readiness boundary.</summary>
public sealed class WebSearchLocationSource(WebSearchPluginSource plugins, CommandHostService commands) : IWebSearchLocationSource
{
    public async ValueTask<WebSearchLocationLease> AcquireAsync(LocationRef location, CancellationToken ct)
    {
        var lease = await commands.AcquireAsync(location, ct);
        try { return new(lease.Location, await plugins.ReadyAsync(lease.Location, ct), lease.DisposeAsync); }
        catch { await lease.DisposeAsync(); throw; }
    }
}

public static class WebSearchHostComposition
{
    /// <summary>Requires the existing native plugin factory, channel stores, command readiness, and event feed.</summary>
    public static IServiceCollection AddNativeWebSearch(this IServiceCollection services)
    {
        services.TryAddSingleton<WebSearchPluginSource>();
        services.AddSingleton<INativePluginSource>(provider => provider.GetRequiredService<WebSearchPluginSource>());
        services.TryAddSingleton<IWebSearchLocationSource, WebSearchLocationSource>();
        return services;
    }
}
