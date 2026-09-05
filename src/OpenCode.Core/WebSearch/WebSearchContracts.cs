namespace OpenCode.Core.WebSearch;

using OpenCode.Schema;

public enum WebSearchFailure { ProviderRequired, ProviderNotFound, Disabled, Unavailable, Request }

/// <summary>Safe public detail only. Never attach a credential-bearing request URI or upstream body.</summary>
public sealed class WebSearchException(WebSearchFailure failure, string message, string? providerId = null, int? statusCode = null) : Exception(message)
{
    public WebSearchFailure Failure { get; } = failure;
    public string? ProviderId { get; } = providerId;
    public int? StatusCode { get; } = statusCode;
}

public interface IWebSearchProvider
{
    WebSearchProvider Info { get; }
    Task<bool> AvailableAsync(CancellationToken ct);
    Task<IReadOnlyList<WebSearchResult>> ExecuteAsync(string query, CancellationToken ct);
}

/// <summary>Internal source selection: provider ID (including "random"), false, or absent. Not another HTTP endpoint.</summary>
public sealed record WebSearchSelection(string? ProviderId = null, bool Disabled = false)
{
    public void Validate()
    {
#pragma warning disable MA0015 // Preserve the existing ArgumentException contract for cross-property validation; this method has no argument.
        if (Disabled && ProviderId is not null) throw new ArgumentException("A disabled selection cannot also select a provider.");
#pragma warning restore MA0015
    }
}

public interface IWebSearchSelectionStore
{
    Task<WebSearchSelection?> ReadAsync(CancellationToken ct);
    Task SaveAsync(WebSearchSelection selection, CancellationToken ct);
}

/// <summary>Implemented by the shared Location/plugin host, not by an endpoint-owned second tool factory.</summary>
public interface IWebSearchLocationSource
{
    /// <summary>Wait for actual Location plugin readiness and keep its scope leased until disposal.</summary>
    ValueTask<WebSearchLocationLease> AcquireAsync(LocationRef location, CancellationToken ct);
}

public sealed class WebSearchLocationLease(LocationInfo location, WebSearchRuntime runtime, Func<ValueTask> release) : IAsyncDisposable
{
    private int _disposed;
    public LocationInfo Location { get; } = location;
    public WebSearchRuntime Runtime { get; } = runtime;
    public ValueTask DisposeAsync() => Interlocked.Exchange(ref _disposed, 1) == 0 ? release() : ValueTask.CompletedTask;
}
