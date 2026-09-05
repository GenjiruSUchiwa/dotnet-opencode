namespace OpenCode.Core.Integrations.Wellknown;

using System.Text.Json;

/// <summary>Host-global source registry/cache. No polling, credential access, command execution, or plugin loading.</summary>
public sealed class WellknownService(WellknownSourceStore store, WellknownTransport transport) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private WellknownEntry[] _snapshot = [];
    public IReadOnlyList<WellknownEntry> Snapshot => Array.AsReadOnly(Volatile.Read(ref _snapshot));
    public event Action? Updated;

    public async Task<WellknownEntry> AddAsync(string url, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(true);
        try
        {
            var entry = await transport.InspectAsync(url, ct).ConfigureAwait(true);
            if (entry.Manifest.Auth is null) throw new WellknownDiscoveryException("No authentication method found in the wellknown manifest.");
            var origins = await store.AddAsync(entry.Origin, ct).ConfigureAwait(true);
            var cache = _snapshot.ToDictionary(item => item.Origin, StringComparer.Ordinal);
            cache[entry.Origin] = entry;
            _snapshot = origins.Where(cache.ContainsKey).Select(origin => cache[origin]).ToArray();
            Updated?.Invoke();
            return entry;
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<WellknownEntry>> EntriesAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(true);
        try
        {
            var cache = _snapshot.ToDictionary(entry => entry.Origin, StringComparer.Ordinal);
            var entries = new List<WellknownEntry>();
            foreach (var origin in await store.ReadAsync(ct).ConfigureAwait(true))
                entries.Add(cache.GetValueOrDefault(origin) ?? await transport.InspectAsync(origin, ct).ConfigureAwait(true));
            _snapshot = entries.DistinctBy(entry => entry.Origin).ToArray();
            return Array.AsReadOnly(entries.ToArray());
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> RefreshAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(true);
        try
        {
            var origins = await store.ReadAsync(ct).ConfigureAwait(true);
            if (origins.Length == 0) return false;
            var entries = new List<WellknownEntry>();
            foreach (var origin in origins) entries.Add(await transport.InspectAsync(origin, ct).ConfigureAwait(true));
            var next = entries.DistinctBy(entry => entry.Origin).ToArray();
            if (next.Length == _snapshot.Length && next.Zip(_snapshot).All(pair => pair.First.Origin == pair.Second.Origin
                && JsonElement.DeepEquals(JsonSerializer.SerializeToElement(pair.First.Manifest, WellknownJsonContext.Default.WellknownManifest),
                    JsonSerializer.SerializeToElement(pair.Second.Manifest, WellknownJsonContext.Default.WellknownManifest)))) return false;
            _snapshot = next;
            Updated?.Invoke();
            return true;
        }
        finally { _gate.Release(); }
    }

    // Domain operation only. Upstream exposes no HTTP remove/list/refresh wellknown routes.
    public async Task RemoveAsync(string url, CancellationToken ct = default)
    {
        var origin = WellknownTransport.Origin(url);
        await _gate.WaitAsync(ct).ConfigureAwait(true);
        try
        {
            await store.RemoveAsync(origin, ct).ConfigureAwait(true);
            _snapshot = _snapshot.Where(entry => entry.Origin != origin).ToArray();
            Updated?.Invoke();
        }
        finally { _gate.Release(); }
    }

    public Task<IReadOnlyList<JsonElement>> ResolveAsync(WellknownEntry entry, CancellationToken ct = default) => transport.ResolveAsync(entry, null, ct);
    internal Task<IReadOnlyList<JsonElement>> ResolveAuthenticatedAsync(WellknownEntry entry, string key, CancellationToken ct) => transport.ResolveAsync(entry, key, ct);
    public void Dispose() => _gate.Dispose();
}
