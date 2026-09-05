namespace OpenCode.Cli.Tui.Images;

using OpenTui.Blazor;

public sealed record ImagePreviewItem(string Uri, string? Mention = null);

/// <summary>UI-dispatcher-owned preview navigation. Fetch/decode work starts only on Load/Move.</summary>
public sealed class ImagePreviewController : IAsyncDisposable
{
    private readonly ImageSourceLoader _loader;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _request;
    private readonly List<Task> _loads = [];
    private long _generation;
    private bool _disposed;
    public IReadOnlyList<ImagePreviewItem> Items { get; }
    public int Index { get; private set; }
    public ImagePreviewItem? Current => Items.ElementAtOrDefault(Index);
    public ImageState Image { get; } = new();
    public event Action? Changed;

    public ImagePreviewController(ImageSourceLoader loader, IReadOnlyList<ImagePreviewItem> items, int initial = 0)
    {
        _loader = loader;
        Items = Array.AsReadOnly(items.ToArray());
        Index = Math.Clamp(initial, 0, Math.Max(0, Items.Count - 1));
        Image.Changed += Notify;
    }
    private void Notify() => Changed?.Invoke();
    public Task LoadAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
#pragma warning disable MA0042 // LoadAsync synchronously invalidates the previous generation before registering/returning the next owned task.
        _request?.Cancel();
#pragma warning restore MA0042
        var request = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _request = request;
        var generation = ++_generation;
        var item = Current;
        Image.BeginLoad();
        _loads.RemoveAll(task => task.IsCompleted);
        var load = Load();
        _loads.Add(load);
        return load;
        async Task Load()
        {
            try
            {
                if (item is null) throw new InvalidOperationException("No images are available.");
                var bytes = await _loader.ReadAsync(item.Uri, request.Token);
                request.Token.ThrowIfCancellationRequested();
                if (_disposed || generation != _generation) return;
                Image.SetEncoded(bytes);
            }
            catch (OperationCanceledException) when (request.IsCancellationRequested) { }
            catch (Exception exception) { if (!_disposed && generation == _generation) Image.Fail(exception); }
            finally { if (ReferenceEquals(_request, request)) _request = null; request.Dispose(); }
        }
    }
    public Task MoveAsync(int direction)
    {
        if (Items.Count < 2) return Task.CompletedTask;
        Index = ((Index + direction) % Items.Count + Items.Count) % Items.Count;
        Changed?.Invoke();
        return LoadAsync();
    }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        ++_generation;
        Image.Changed -= Notify;
        await _lifetime.CancelAsync();
        await Task.WhenAll(_loads);
        Image.Dispose();
        _lifetime.Dispose();
    }
}
