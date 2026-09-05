namespace OpenCode.Core.Tools;

using OpenCode.Core.Forms;
using OpenCode.Core.Permissions;
using OpenCode.Core.Tools.Builtins;
using OpenCode.Core.WebSearch;

/// <summary>One native plugin-scoped transform over the host's existing WebSearchRuntime.
/// Eligibility is refreshed before request snapshots, never by constructing provider transports.</summary>
internal sealed class WebSearchToolBinding(
    Func<CancellationToken, Task<WebSearchRuntime>> ready,
    PermissionService permission,
    FormService? forms,
    ToolRegistry registry,
    TimeProvider clock,
    CancellationToken lifetime) : IAsyncDisposable
{
    private readonly SemaphoreSlim _refresh = new(1, 1);
    private WebSearchRuntime? _runtime;
    private WebSearchTool? _producer;
    private ToolInfo? _definition;
    private bool _dirty;
    private int _disposed;

    public void Apply(IToolDraft draft)
    {
        if (Volatile.Read(ref _disposed) == 0 && Volatile.Read(ref _definition) is { } definition) draft.Add(definition);
    }

    public async Task RefreshAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime);
        await _refresh.WaitAsync(linked.Token).ConfigureAwait(true);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            WebSearchRuntime? runtime;
            ToolInfo? next;
            try
            {
                // This callback reads the initialized host entry directly. Calling the public
                // Location source here would recursively acquire this same tool Location.
                runtime = await ready(linked.Token).ConfigureAwait(true);
                if (!ReferenceEquals(runtime, _runtime)) _producer = new WebSearchTool(runtime, permission, forms, clock);
                next = await _producer!.CreateIfAvailableAsync(linked.Token).ConfigureAwait(true);
            }
            catch (WebSearchException error) when (error.Failure is WebSearchFailure.Unavailable or WebSearchFailure.Disabled or WebSearchFailure.ProviderNotFound)
            {
                // A known unavailable backend removes only our builtin contribution; no fake
                // executor or successful empty result replaces it. Other tools stay usable.
                runtime = null;
                next = null;
            }
            var current = Volatile.Read(ref _definition);
            if (!ReferenceEquals(_runtime, runtime) || (current is null) != (next is null) || current?.Description != next?.Description)
            {
                _runtime = runtime;
                Volatile.Write(ref _definition, next);
                _dirty = true;
            }
            if (!_dirty) return;
            // A cancelled flush must not permit a later capture to use the old registry view.
            // Keep the dirty bit until the same native transform has actually been replayed.
            await registry.ReloadAsync(linked.Token).ConfigureAwait(true);
            _dirty = false;
        }
        finally { _refresh.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        // NativePluginScope cancels its lifetime before unwinding resources. Finish the
        // in-flight refresh, but never dispose the borrowed runtime or its provider scopes.
        await _refresh.WaitAsync(CancellationToken.None).ConfigureAwait(true);
        try { Volatile.Write(ref _definition, null); _runtime = null; _producer = null; }
        finally { _refresh.Release(); }
    }
}
